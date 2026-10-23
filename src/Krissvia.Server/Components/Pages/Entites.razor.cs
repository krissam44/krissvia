using System.Globalization;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Server.Components.Inventaire;
using Microsoft.JSInterop;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Entités du registre, à la manière de l'écran Équipements de DomoMud : filtres par type, recherche, valeur et
/// fraîcheur en direct (relues toutes les 3 s), et un dialogue pour les champs de l'utilisateur.
/// </summary>
public partial class Entites : IDisposable
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>Colonne Valeur : masquée par défaut (le tableau tient mieux), le choix est gardé par navigateur.</summary>
    private bool _avecValeur;
    private const string CleValeur = "krissvia.entites.valeur";

    /// <summary>
    /// Entités désactivées : montrées par défaut. Masquées, la liste ne garde que ce qui sert — l'entité « événement »
    /// d'un module Zigbee dont on n'utilise que le relais, par exemple. Même mémoire que la colonne Valeur.
    /// </summary>
    private bool _avecDesactivees = true;
    private const string CleDesactivees = "krissvia.entites.desactivees";

    private IReadOnlyList<RegisteredEntity> _entites = [];
    private Dictionary<string, RegisteredDevice> _appareils = [];
    private Categorie? _categorie;

    /// <summary>Valeur de la liste pour « toutes les catégories ».</summary>
    private const string Toutes = "";

    private void ChoisirCategorie(string? cle) => _categorie = Categories.Toutes.FirstOrDefault(c => c.Cle == cle);

    private string LibelleFiltre(string? cle) =>
        Categories.Toutes.FirstOrDefault(c => c.Cle == cle) is { } c
            ? $"{c.Libelle} ({_entites.Count(e => Categories.De(e) == c)})"
            : $"Toutes ({_entites.Count})";
    private string? _recherche;
    private Timer? _minuterie;

    protected override async Task OnInitializedAsync()
    {
        await RelireAsync();
        _minuterie = new Timer(_ => InvokeAsync(StateHasChanged), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    protected override async Task OnAfterRenderAsync(bool premierRendu)
    {
        if (!premierRendu) return;
        try
        {
            _avecValeur = await JS.InvokeAsync<string?>("localStorage.getItem", CleValeur) == "1";
            _avecDesactivees = await JS.InvokeAsync<string?>("localStorage.getItem", CleDesactivees) != "0";
            StateHasChanged();
        }
        catch (JSException)
        {
            // Stockage bloqué (navigation privée) : on garde le réglage par défaut.
        }
    }

    private Task AfficherValeurAsync(bool oui)
    {
        _avecValeur = oui;
        return RetenirAsync(CleValeur, oui);
    }

    private Task AfficherDesactiveesAsync(bool oui)
    {
        _avecDesactivees = oui;
        return RetenirAsync(CleDesactivees, oui);
    }

    private async Task RetenirAsync(string cle, bool oui)
    {
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", cle, oui ? "1" : "0");
        }
        catch (JSException)
        {
            // Le choix vaut pour cette visite seulement.
        }
    }

    private async Task RelireAsync()
    {
        _entites = await Registre.ListAsync();
        _appareils = (await Registre.ListDevicesAsync()).ToDictionary(d => d.DeviceId);
    }

    private string? Piece(RegisteredEntity e) => _appareils.GetValueOrDefault(e.DeviceId)?.Room;

    /// <summary>Filtrées, puis rangées par pièce et par nom : les entités d'une même pièce se suivent.</summary>
    private IEnumerable<RegisteredEntity> Visibles()
    {
        var texte = _recherche?.Trim();
        return _entites
            .Where(e => _categorie is null || Categories.De(e) == _categorie)
            .Where(e => _avecDesactivees || e.Enabled)
            .Where(e => string.IsNullOrEmpty(texte)
                        || e.DisplayName.Contains(texte, StringComparison.CurrentCultureIgnoreCase)
                        || e.EntityId.Contains(texte, StringComparison.OrdinalIgnoreCase)
                        || (_appareils.GetValueOrDefault(e.DeviceId)?.Room?.Contains(texte, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderBy(e => _appareils.GetValueOrDefault(e.DeviceId)?.Room ?? "￿", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>Le niveau de pile de l'appareil, s'il en déclare un ; sinon un tiret.</summary>
    private RenderFragment Alimentation(RegisteredEntity e) => builder =>
    {
        var pile = _entites.FirstOrDefault(x => x.DeviceId == e.DeviceId && x.DeviceClass == DeviceClasses.Battery);
        if (pile is not null && Etats.Get(pile.EntityId) is { } etat && etat.TryGetNumber(out var pourcent))
        {
            var couleur = pourcent < 20 ? "#e53935" : "#ef8a17";
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "alim");
            builder.AddAttribute(2, "style", $"color:{couleur};border-color:{couleur}88");
            builder.AddContent(3, $"pile {pourcent:0} %");
            builder.CloseElement();
        }
        else
        {
            builder.AddContent(4, "—");
        }
    };

    /// <summary>Heure de la dernière donnée, colorée selon son âge : une sonde muette depuis longtemps se voit.</summary>
    private RenderFragment Fraicheur(EntityStateSnapshot? etat) => builder =>
    {
        if (etat is null)
        {
            builder.AddContent(0, "— jamais");
            return;
        }

        var age = Horloge.GetUtcNow() - etat.MeasuredAt;
        var (couleur, marque) = !etat.Available ? ("#e53935", "✕") : age > TimeSpan.FromHours(2) ? ("#ef8a17", "!") : ("#43a047", "✓");
        var local = etat.MeasuredAt.ToLocalTime();
        var texte = local.Date == Horloge.GetLocalNow().Date ? local.ToString("HH:mm:ss", Fr) : local.ToString("dd/MM HH:mm", Fr);
        builder.OpenElement(1, "span");
        builder.AddAttribute(2, "style", $"color:{couleur}");
        builder.AddAttribute(3, "title", !etat.Available ? "indisponible" : $"il y a {Duree(age)}");
        builder.AddContent(4, $"{marque} {texte}");
        builder.CloseElement();
    };

    private static string Duree(TimeSpan d) =>
        d.TotalMinutes < 1 ? "moins d'une minute" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalDays} j";

    private async Task ModifierAsync(RegisteredEntity e)
    {
        var parametres = new DialogParameters<EntiteDialog>
        {
            { x => x.Entite, e },
            { x => x.Appareil, _appareils.GetValueOrDefault(e.DeviceId) },
            { x => x.AutresEntites, _entites.Where(x => x.DeviceId == e.DeviceId && x.EntityId != e.EntityId).ToList() },
        };
        var dialogue = await Dialogues.ShowAsync<EntiteDialog>("Modifier l'entité", parametres,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true });
        if (await dialogue.Result is { Canceled: false }) await RelireAsync();
    }

    public void Dispose() => _minuterie?.Dispose();
}
