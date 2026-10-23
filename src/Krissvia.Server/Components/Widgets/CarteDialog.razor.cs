using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;
using Krissvia.Server.Components.Inventaire;

namespace Krissvia.Server.Components.Widgets;

/// <summary>
/// Ajout ou réglage d'une carte : on choisit d'abord l'entité, puis son widget. Par défaut « Automatique » : la carte
/// suit le widget de l'entité (écran Entités, sinon le plus précis) et lie seule les entités du même appareil. On peut
/// aussi imposer un widget à cette carte, avec ses slots (WIDGETS.md §6).
/// </summary>
public partial class CarteDialog
{
    [CascadingParameter] private IMudDialogInstance Dialogue { get; set; } = default!;
    [Parameter] public WidgetCatalogSnapshot Catalogue { get; set; } = new([], []);
    [Parameter] public IReadOnlyList<RegisteredEntity> Entites { get; set; } = [];
    [Parameter] public DashboardCard? Carte { get; set; }

    private RegisteredEntity? _principale;
    private IReadOnlyList<CatalogWidget> _compatibles = [];
    private string? _widget;
    private readonly Dictionary<string, string?> _liens = new(StringComparer.Ordinal);
    private int _cols = 1;

    /// <summary>Nom propre à la carte ; vide : celui de l'entité.</summary>
    private string? _titre;
    private double _rows = 1;
    private SettingField? _schema;
    private JsonObject? _reglages;
    private string? _erreur;

    // Une liste et non une boucle for : le contenu d'un élément de liste est rendu plus tard, et capturerait le
    // compteur de boucle à sa dernière valeur.
    /// <summary>Les largeurs usuelles, et celle de la carte si elle n'en est pas (une page convertie des sixièmes).</summary>
    private IEnumerable<int> ChoixLargeurs => Largeurs.Usuelles.Append(_cols).Where(n => n is >= 1 and <= WidgetCatalog.MaxCols).Distinct().Order();

    /// <summary>Valeur de la liste pour « la carte suit son entité ».</summary>
    private const string Auto = "";

    private bool EstAuto => _widget == Auto;

    /// <summary>Le widget qui s'affichera : celui de l'entité en automatique, sinon celui choisi.</summary>
    private CatalogWidget? Widget =>
        EstAuto ? (_principale is null ? null : WidgetResolver.ChooseFor(_principale, Catalogue))
        : _widget is null ? null : Catalogue.Find(_widget);

    /// <summary>Les liens qu'une carte automatique fera seule, pour les montrer.</summary>
    private IReadOnlyDictionary<string, string> LiensAuto =>
        EstAuto && _principale is not null && Widget is { } w ? WidgetResolver.Bind(w, _principale, Entites) : new Dictionary<string, string>();

    private string LibelleAuto =>
        Widget is { } w && _principale is not null
            ? $"Automatique — {w.Manifest.Name}{(_principale.Widget is not null ? " (choisi pour l'entité)" : "")}"
            : "Automatique";

    protected override void OnInitialized()
    {
        if (Carte is null) return;
        _titre = Carte.Title;

        if (Carte.Widget is null)
        {
            _principale = Entites.FirstOrDefault(e => e.EntityId == Carte.Entity);
            _compatibles = _principale is null ? [] : WidgetCatalog.ChoosableFor(Catalogue, _principale);
            Appliquer(Auto, Carte);
            return;
        }

        var widget = Catalogue.Find(Carte.Widget);
        var principalId = widget is null ? Carte.Entities.Values.FirstOrDefault() : Carte.Entities.GetValueOrDefault(widget.Manifest.Slots[0].Name);
        _principale = Entites.FirstOrDefault(e => e.EntityId == principalId);
        if (_principale is null) return;

        _compatibles = WidgetCatalog.ChoosableFor(Catalogue, _principale);
        Appliquer(widget?.Manifest.Id ?? _compatibles.FirstOrDefault()?.Manifest.Id, Carte);
    }

    private Task<IEnumerable<RegisteredEntity>> ChercherAsync(string? texte, CancellationToken ct) =>
        Task.FromResult<IEnumerable<RegisteredEntity>>(Entites
            .Where(e => string.IsNullOrWhiteSpace(texte)
                        || e.DisplayName.Contains(texte, StringComparison.CurrentCultureIgnoreCase)
                        || e.EntityId.Contains(texte, StringComparison.OrdinalIgnoreCase)
                        || Categories.De(e).Libelle.Contains(texte, StringComparison.CurrentCultureIgnoreCase))
            .Where(e => WidgetCatalog.ChoosableFor(Catalogue, e).Count > 0)
            // Rangées par type, dans l'ordre du filtre de l'écran Entités, puis par nom.
            .OrderBy(e => Array.IndexOf(Categories.Toutes, Categories.De(e)))
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase));

    private void ChoisirEntite(RegisteredEntity? entite)
    {
        _principale = entite;
        _compatibles = entite is null ? [] : WidgetCatalog.ChoosableFor(Catalogue, entite);
        Appliquer(Auto, null);
    }

    private void ChoisirWidget(string? id) => Appliquer(id, null);

    /// <summary>Choisit un widget : taille par défaut, slots secondaires repris s'ils conviennent encore, options.</summary>
    private void Appliquer(string? widgetId, DashboardCard? existante)
    {
        _widget = widgetId;
        _erreur = null;
        if (Widget is not { } widget)
        {
            _schema = null;
            _reglages = null;
            return;
        }

        var anciens = new Dictionary<string, string?>(_liens);
        _liens.Clear();
        foreach (var slot in EstAuto ? Enumerable.Empty<WidgetSlot>() : widget.Manifest.Slots.Skip(1))
        {
            var lien = existante?.Entities.GetValueOrDefault(slot.Name) ?? anciens.GetValueOrDefault(slot.Name);
            _liens[slot.Name] = Entites.FirstOrDefault(e => e.EntityId == lien) is { } e && WidgetCatalog.Score(slot, e, ignoreUsage: true) >= 0 ? lien : null;
        }

        _cols = existante?.Cols ?? widget.Manifest.Size.Cols;
        _rows = existante?.Rows ?? widget.Manifest.Size.Rows;
        _schema = SettingsSchema.Parse(widget.Manifest.Settings);
        _reglages = _schema is null ? null
            : SettingsSchema.Complete(_schema, existante?.Settings is { } s ? JsonNode.Parse(s.GetRawText()) as JsonObject : null);
    }

    private void Valider()
    {
        if (_principale is null || Widget is not { } widget) return;

        var manquant = EstAuto ? null : widget.Manifest.Slots.Skip(1).FirstOrDefault(s => s.Required && _liens.GetValueOrDefault(s.Name) is null);
        if (manquant is not null)
        {
            _erreur = $"Choisissez l'entité « {manquant.Name} ».";
            return;
        }

        if (_schema is not null && _reglages is not null && SettingsSchema.Validate(_schema, _reglages) is { Count: > 0 } erreurs)
        {
            _erreur = string.Join(" ", erreurs);
            return;
        }

        var liens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!EstAuto)
        {
            liens[widget.Manifest.Slots[0].Name] = _principale.EntityId;
            foreach (var (slot, entite) in _liens)
                if (entite is not null) liens[slot] = entite;
        }

        var titre = _titre?.Trim();
        if (titre is { Length: > DashboardService.MaxTitleLength })
        {
            _erreur = $"Nom de {DashboardService.MaxTitleLength} caractères au plus.";
            return;
        }

        Dialogue.Close(DialogResult.Ok(new DashboardCard
        {
            Id = Carte?.Id ?? Guid.NewGuid().ToString("N")[..12],
            Widget = EstAuto ? null : widget.Manifest.Id,
            Entity = EstAuto ? _principale.EntityId : null,
            Entities = liens,
            // Vide ou identique au nom de l'entité : la carte suit ce nom, y compris s'il change ensuite.
            Title = string.IsNullOrEmpty(titre) || titre == _principale.DisplayName ? null : titre,
            Settings = _reglages is null ? null : JsonSerializer.SerializeToElement(_reglages),
            // La taille du manifeste n'est pas recopiée : si le widget change de taille par défaut, la carte suit.
            Cols = _cols == widget.Manifest.Size.Cols ? null : _cols,
            Rows = _rows == widget.Manifest.Size.Rows ? null : _rows,
        }));
    }
}
