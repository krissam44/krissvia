using System.Text.Json;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Widgets;
using Krissvia.Widgets;
using Krissvia.Server.Components.Widgets;
using Microsoft.JSInterop;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Tableau de bord : des pages (« Confort », « Technique »…), des sections repliables, et dans chaque section une
/// grille de cartes, chacune un widget (WIDGETS.md). Cette page ne fait que la disposition et les réglages ; le
/// contenu des cartes est rendu par le module <c>tableau-de-bord.js</c>, qui ne parle qu'à l'API publique.
/// </summary>
/// <remarks>
/// Organisation reprise de DomoMud. Le mode édition travaille sur une copie : « Enregistrer » écrit tout d'un
/// coup, « Annuler » ne laisse aucune trace. Replier une section hors édition s'enregistre aussitôt, comme dans
/// DomoMud.
/// </remarks>
public partial class TableauDeBord
{
    [Parameter] public string? PageId { get; set; }

    [Inject] private DashboardService Tableaux { get; set; } = default!;
    [Inject] private WidgetCatalogService Widgets { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private Krissvia.Core.Live.LiveFeed Flux { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private ICommandBus Commandes { get; set; } = default!;

    private List<Dashboard> _pages = [new() { Id = DashboardService.MainId }];
    private IReadOnlyList<Dashboard> _avantEdition = [];
    private string _pageId = DashboardService.MainId;
    private WidgetCatalogSnapshot _catalogue = new([], []);
    private bool _edition;
    private bool _renommage;
    private (string Section, string Carte)? _glissee;
    private bool _aAppliquer = true;
    private ElementReference _racine;
    private IJSObjectReference? _module;
    private DotNetObjectReference<TableauDeBord>? _dotnet;
    private string? _erreurNavigateur;
    private IReadOnlyList<RegisteredEntity> _entites = [];
    private Dictionary<string, string?> _pieces = new(StringComparer.Ordinal);
    private SuiviEnDirect? _suivi;

    // Largeurs du menu de taille : null = celle du widget, puis les fractions usuelles d'un grand écran.
    private static readonly (int? Cols, string Libelle)[] Largeurs =
        [(null, "Auto (taille du widget)"), .. Krissvia.Server.Components.Widgets.Largeurs.Usuelles.Select(n => ((int?)n, Krissvia.Server.Components.Widgets.Largeurs.Libelle(n)))];

    private Dashboard Page => _pages.FirstOrDefault(p => p.Id == _pageId) ?? _pages[0];

    protected override async Task OnInitializedAsync()
    {
        _catalogue = Widgets.Load();
        await ChargerAsync();
        Tableaux.Changed += SurChangement;

        // Le widget d'une entité changé dans l'écran Entités change ses cartes automatiques : on relit le registre.
        // Les widgets Blazor sont rendus ici, côté serveur : un changement d'état les redessine (les Web Components,
        // eux, reçoivent les états par leur propre flux dans le navigateur).
        _suivi = new SuiviEnDirect(Flux, async registre =>
        {
            if (registre) await RelireRegistreAsync();
            if (!registre && !AfficheDuBlazor) return;
            await InvokeAsync(() =>
            {
                if (registre) _aAppliquer = true;
                StateHasChanged();
            });
        }, TimeSpan.FromMinutes(1));
    }

    private async Task ChargerAsync()
    {
        _pages = [.. await Tableaux.ListAsync()];
        await RelireRegistreAsync();
        _pageId = _pages.Any(p => p.Id == (PageId ?? _pageId)) ? PageId ?? _pageId : _pages[0].Id;
        _aAppliquer = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Une erreur du navigateur ne doit pas couper le circuit (et toute la page) : elle s'affiche à la place.
        try
        {
            if (firstRender)
            {
                // « ./ » : un import de module veut une adresse relative explicite.
                _module = await JS.InvokeAsync<IJSObjectReference>("import", "./" + Assets["js/tableau-de-bord.js"]);
                _dotnet ??= DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("demarrer", _racine, _catalogue.Widgets.Select(WidgetApi.Vue).Select(PourJs), _dotnet);
            }

            if (_module is not null && _aAppliquer)
            {
                _aAppliquer = false;
                // Seulement les Web Components : les widgets Blazor sont rendus par la page elle-même.
                await _module.InvokeVoidAsync("appliquer", Page.Sections.Where(s => !s.Collapsed).SelectMany(s => s.Cards)
                    .Select(Resoudre)
                    .Where(r => r.Widget?.IsBlazor != true)
                    .Select(r => new
                    {
                        id = r.Card.Id, widget = r.WidgetId, entities = r.Entities, settings = r.Card.Settings,
                        title = r.Card.Title, principal = r.Widget?.Manifest.Slots[0].Name,
                    }));
            }
        }
        catch (JSException ex)
        {
            _erreurNavigateur = ex.Message;
            StateHasChanged();
        }
    }

    /// <summary>Les noms du catalogue tels que l'API les donne (snake_case), quel que soit le format de l'interop.</summary>
    private static Dictionary<string, object?> PourJs(object vue) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
            System.Text.Json.JsonSerializer.Serialize(vue, Krissvia.Climate.Thermostats.ThermostatService.Json))!;

    private async Task RelireRegistreAsync()
    {
        _entites = await Registre.ListAsync();
        _pieces = (await Registre.ListDevicesAsync()).ToDictionary(d => d.DeviceId, d => d.Room, StringComparer.Ordinal);
    }

    private bool AfficheDuBlazor =>
        Page.Sections.Where(s => !s.Collapsed).SelectMany(s => s.Cards).Any(c => Resoudre(c).Widget?.IsBlazor == true);

    /// <summary>Le composant d'un widget Blazor, ou null pour un Web Component.</summary>
    private Type? Composant(ResolvedCard carte) => carte.Widget is { IsBlazor: true } w ? Widgets.Component(w.Manifest.Id) : null;

    /// <summary>
    /// Ce que reçoit un widget Blazor : ses entités et leur dernier état, ses options, et l'envoi d'ordres limité à
    /// ses slots, vérifié et signé <c>widget:{id}</c> — les mêmes règles qu'un Web Component (WIDGETS.md §4-5).
    /// </summary>
    private WidgetContext Contexte(ResolvedCard carte)
    {
        var entites = new Dictionary<string, WidgetEntity>(StringComparer.Ordinal);
        var etats = new Dictionary<string, WidgetState?>(StringComparer.Ordinal);
        foreach (var (slot, id) in carte.Entities)
        {
            var e = _entites.FirstOrDefault(x => x.EntityId == id);
            entites[slot] = e is null
                ? new WidgetEntity(id, id, "", null, null, [], null, null)
                : new WidgetEntity(e.EntityId, e.DisplayName, JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Kind.ToString()), e.DeviceClass, e.Unit,
                    e.Commands, _pieces.GetValueOrDefault(e.DeviceId),
                    e.ExtraJson is { Length: > 0 } extra ? JsonSerializer.Deserialize<JsonElement>(extra) : null);
            etats[slot] = Etats.Get(id) is { } etat ? new WidgetState(etat.Value, etat.Available, etat.MeasuredAt) : null;
        }

        // Le nom propre à la carte remplace celui de l'entité principale, comme pour un Web Component.
        if (carte.Card.Title is { Length: > 0 } titre && carte.Widget?.Manifest.Slots[0].Name is { } principal
            && entites.TryGetValue(principal, out var e0))
            entites[principal] = e0 with { Name = titre };

        var reglages = carte.Card.Settings ?? JsonSerializer.SerializeToElement(new { });
        var widget = carte.WidgetId;
        return new WidgetContext(entites, etats, reglages, async (slot, commande, args) =>
        {
            if (!carte.Entities.TryGetValue(slot, out var id))
                return new WidgetCommandResult("refused", "not_sent", $"slot « {slot} » non lié");
            if (_entites.FirstOrDefault(x => x.EntityId == id) is not { } entite || !entite.Commands.Contains(commande, StringComparer.Ordinal))
                return new WidgetCommandResult("refused", "not_sent", $"« {id} » n'accepte pas « {commande} »");

            var resultat = await Commandes.SendVerifiedAsync(new EntityCommand(id, commande)
            {
                Args = args is null ? null : JsonSerializer.SerializeToElement(args),
                Origin = $"widget:{widget}",
            });
            return new WidgetCommandResult(
                JsonNamingPolicy.SnakeCaseLower.ConvertName(resultat.Outcome.ToString()),
                JsonNamingPolicy.SnakeCaseLower.ConvertName(resultat.Verification.ToString()),
                resultat.Detail);
        });
    }

    /// <summary>Le widget de la carte : le sien, sinon celui de son entité, sinon le plus précis (WIDGETS.md §6).</summary>
    private ResolvedCard Resoudre(DashboardCard carte) => WidgetResolver.Resolve(carte, _entites, _catalogue);

    private (int Cols, double Rows) Taille(DashboardCard carte)
    {
        var defaut = Resoudre(carte).Widget?.Manifest.Size ?? new WidgetSize();
        return (carte.Cols ?? defaut.Cols, carte.Rows ?? defaut.Rows);
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    private void ChoisirPage(string id)
    {
        _pageId = id;
        _renommage = false;
        _aAppliquer = true;
    }

    private void AjouterPage()
    {
        var page = new Dashboard { Id = DashboardService.NewPageId("Nouvelle page", _pages.Select(p => p.Id)), Name = "Nouvelle page" };
        _pages.Add(page);
        ChoisirPage(page.Id);
        _renommage = true;
    }

    private void RenommerPage(string nom) => ChangerPage(p => p with { Name = nom });

    private void DeplacerPage(int sens)
    {
        var i = _pages.FindIndex(p => p.Id == Page.Id);
        var cible = i + sens;
        if (cible < 0 || cible >= _pages.Count) return;
        (_pages[i], _pages[cible]) = (_pages[cible], _pages[i]);
    }

    private void SupprimerPage()
    {
        if (_pages.Count <= 1) return;
        var i = _pages.FindIndex(p => p.Id == Page.Id);
        _pages.RemoveAt(i);
        ChoisirPage(_pages[Math.Max(0, i - 1)].Id);
    }

    // ── Mode édition ─────────────────────────────────────────────────────────

    private void Modifier()
    {
        _avantEdition = [.. _pages];
        _edition = true;
    }

    private void Annuler()
    {
        _pages = [.. _avantEdition];
        if (_pages.All(p => p.Id != _pageId)) _pageId = _pages[0].Id;
        _edition = false;
        _renommage = false;
        _aAppliquer = true;
    }

    private async Task EnregistrerAsync()
    {
        // Une page créée pendant cette édition prend l'identifiant de son nom définitif (il figure dans son adresse) :
        // « Technique » plutôt que « nouvelle-page ». Une page existante garde le sien.
        var existantes = _avantEdition.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var pris = new HashSet<string>(existantes, StringComparer.Ordinal);
        var renommees = new Dictionary<string, string>(StringComparer.Ordinal);
        var pages = _pages.Select(p =>
        {
            if (existantes.Contains(p.Id)) return p;
            var id = DashboardService.NewPageId(p.Name, pris);
            pris.Add(id);
            renommees[p.Id] = id;
            return p with { Id = id };
        }).ToList();

        try
        {
            _pages = [.. await Tableaux.SaveAllAsync(pages)];
            _pageId = renommees.GetValueOrDefault(_pageId, _pageId);
            _edition = false;
            _renommage = false;
            _aAppliquer = true;
            Snackbar.Add("Tableau de bord enregistré.", Severity.Success);
        }
        catch (DashboardValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    /// <summary>Ajoute à la page les sections proposées d'après les entités (thermostats, températures…).</summary>
    private async Task ProposerAsync()
    {
        var radiateurs = Thermostats.List().SelectMany(t => t.SwitchEntityIds).ToHashSet(StringComparer.Ordinal);
        await RelireRegistreAsync();
        var sections = DashboardProposal.Propose(_entites, _catalogue, radiateurs);
        if (sections.Count == 0)
        {
            Snackbar.Add("Aucune entité à proposer : activez d'abord un plugin.", Severity.Info);
            return;
        }

        ChangerPage(p => p with { Sections = [.. p.Sections, .. sections] });
        Snackbar.Add($"{sections.Sum(s => s.Cards.Count)} carte(s) proposée(s) : relisez, puis « Enregistrer ».", Severity.Info);
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    private void AjouterSection() =>
        ChangerPage(p => p with { Sections = [.. p.Sections, new DashboardSection { Id = NouvelId(), Title = "Nouvelle section" }] });

    private void RenommerSection(string id, string titre) => ChangerSection(id, s => s with { Title = titre });

    /// <summary>Largeurs proposées pour une section : toute la ligne, puis les fractions qui en mettent plusieurs côte à côte.</summary>
    private static readonly int[] LargeursSection = [DashboardService.GridColumns, 18, 16, 12, 8, 6];

    private static string LibelleSection(int cols) =>
        cols >= DashboardService.GridColumns ? "Toute la ligne" : Krissvia.Server.Components.Widgets.Largeurs.Libelle(cols);

    /// <summary>Couleurs proposées pour une section ; null : sans fond.</summary>
    private static readonly (string? Couleur, string Nom)[] CouleursSection =
    [
        (null, "Sans couleur"), ("#42A5F5", "Bleu"), ("#26A69A", "Turquoise"), ("#66BB6A", "Vert"), ("#FFCA28", "Jaune"),
        ("#FFA726", "Orange"), ("#EF5350", "Rouge"), ("#EC407A", "Rose"), ("#AB47BC", "Violet"), ("#8D6E63", "Brun"), ("#78909C", "Gris"),
    ];

    private void ColorerSection(string id, string? couleur) => ChangerSection(id, s => s with { Color = couleur });

    /// <summary>La largeur passe par --kv-cols (<see cref="StyleSection"/>) : la classe ne dit que la teinte.</summary>
    private static string ClasseSection(DashboardSection s) => s.Color is null ? "kv-section" : "kv-section kv-section-teinte";

    /// <summary>La couleur passe par une variable CSS : la feuille de style en tire la teinte du fond et du liseré.</summary>
    private static string StyleSection(DashboardSection s, int largeur) =>
        s.Color is { } couleur ? $"--kv-cols: {largeur}; --kv-teinte: {couleur}" : $"--kv-cols: {largeur}";

    private static string StyleCouleur(string? couleur) => couleur is null ? "" : $"color: {couleur}";

    /// <summary>Toute la ligne s'enregistre comme une absence de largeur : une page d'avant reste identique.</summary>
    private void ElargirSection(string id, int cols) =>
        ChangerSection(id, s => s with { Cols = cols >= DashboardService.GridColumns ? null : cols });

    private void DeplacerSection(string id, int sens) => ChangerPage(p =>
    {
        var sections = p.Sections.ToList();
        var i = sections.FindIndex(s => s.Id == id);
        var cible = i + sens;
        if (i < 0 || cible < 0 || cible >= sections.Count) return p;
        (sections[i], sections[cible]) = (sections[cible], sections[i]);
        return p with { Sections = sections };
    });

    private void SupprimerSection(string id) => ChangerPage(p => p with { Sections = [.. p.Sections.Where(s => s.Id != id)] });

    private async Task BasculerSectionAsync(string id)
    {
        ChangerSection(id, s => s with { Collapsed = !s.Collapsed });
        if (_edition) return;

        try
        {
            await Tableaux.SaveAsync(Page);
        }
        catch (DashboardValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    // ── Cartes ───────────────────────────────────────────────────────────────

    private async Task AjouterCarteAsync(string sectionId)
    {
        if (await OuvrirAsync(null) is { } carte) ChangerSection(sectionId, s => s with { Cards = [.. s.Cards, carte] });
    }

    private async Task ModifierCarteAsync(string sectionId, DashboardCard carte)
    {
        if (await OuvrirAsync(carte) is { } modifiee)
            ChangerSection(sectionId, s => s with { Cards = [.. s.Cards.Select(c => c.Id == carte.Id ? modifiee : c)] });
    }

    private void SupprimerCarte(string sectionId, string carteId) =>
        ChangerSection(sectionId, s => s with { Cards = [.. s.Cards.Where(c => c.Id != carteId)] });

    private void DeplacerCarte(string sectionId, int index, int sens) => ChangerSection(sectionId, s =>
    {
        var cartes = s.Cards.ToList();
        var cible = index + sens;
        if (cible < 0 || cible >= cartes.Count) return s;
        (cartes[index], cartes[cible]) = (cartes[cible], cartes[index]);
        return s with { Cards = cartes };
    });

    private void Redimensionner(string sectionId, string carteId, int? cols) =>
        ChangerSection(sectionId, s => s with { Cards = [.. s.Cards.Select(c => c.Id == carteId ? c with { Cols = cols } : c)] });

    private void Rehausser(string sectionId, string carteId, double? rows) =>
        ChangerSection(sectionId, s => s with { Cards = [.. s.Cards.Select(c => c.Id == carteId ? c with { Rows = rows } : c)] });

    /// <summary>
    /// Glisser-déposer : une carte lâchée sur une autre se place devant elle, lâchée dans le vide d'une section elle
    /// va à la fin. D'une section à l'autre comme dans DomoMud ; les flèches restent pour le téléphone.
    /// </summary>
    private void Deposer(string sectionCible, string? carteCible)
    {
        if (_glissee is not { } glissee || glissee.Carte == carteCible) return;
        _glissee = null;
        if (Page.Sections.FirstOrDefault(s => s.Id == glissee.Section)?.Cards.FirstOrDefault(c => c.Id == glissee.Carte) is not { } carte) return;

        ChangerPage(p => p with
        {
            Sections = [.. p.Sections.Select(s =>
            {
                var cartes = s.Cards.Where(c => c.Id != carte.Id).ToList();
                if (s.Id == sectionCible)
                {
                    var position = carteCible is null ? -1 : cartes.FindIndex(c => c.Id == carteCible);
                    cartes.Insert(position < 0 ? cartes.Count : position, carte);
                }
                return s with { Cards = cartes };
            })],
        });
    }

    private async Task<DashboardCard?> OuvrirAsync(DashboardCard? carte)
    {
        var parametres = new DialogParameters<CarteDialog>
        {
            { d => d.Catalogue, _catalogue },
            { d => d.Entites, [.. (await Registre.ListAsync()).Where(e => e.Enabled && !e.Missing)] },
            { d => d.Carte, carte },
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true };
        var dialogue = await Dialogues.ShowAsync<CarteDialog>(carte is null ? "Ajouter une carte" : "Régler la carte", parametres, options);
        return (await dialogue.Result) is { Canceled: false, Data: DashboardCard resultat } ? resultat : null;
    }

    // ── Outils ───────────────────────────────────────────────────────────────

    private void ChangerPage(Func<Dashboard, Dashboard> modification)
    {
        var i = _pages.FindIndex(p => p.Id == Page.Id);
        _pages[i] = modification(_pages[i]);
        _aAppliquer = true;
    }

    private void ChangerSection(string id, Func<DashboardSection, DashboardSection> modification) =>
        ChangerPage(p => p with { Sections = [.. p.Sections.Select(s => s.Id == id ? modification(s) : s)] });

    private static string NouvelId() => Guid.NewGuid().ToString("N")[..12];

    // Enregistré ailleurs (autre onglet, API) : on relit, sauf pendant une édition, qui ne doit rien perdre.
    private void SurChangement()
    {
        if (_edition) return;
        _ = InvokeAsync(async () =>
        {
            if (_edition) return;
            await ChargerAsync();
            StateHasChanged();
        });
    }

    /// <summary>Un widget demande l'historique d'une de ses entités (<c>kv.openHistory</c>).</summary>
    [JSInvokable]
    public async Task OuvrirHistorique(string entityId)
    {
        if (await Registre.GetAsync(entityId) is { } entite) await InvokeAsync(() => HistoriqueDialog.OuvrirAsync(Dialogues, entite));
    }

    public async ValueTask DisposeAsync()
    {
        Tableaux.Changed -= SurChangement;
        _dotnet?.Dispose();
        _suivi?.Dispose();
        if (_module is null) return;
        try
        {
            await _module.InvokeVoidAsync("arreter");
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit déjà fermé : le navigateur a quitté la page.
        }
    }
}
