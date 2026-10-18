using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;
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
    private string? _erreurNavigateur;

    // Largeurs du menu de taille (DomoMud) : null = celle du widget, 6 = toute la largeur d'un grand écran.
    private static readonly (int? Cols, string Libelle)[] Largeurs =
        [(null, "Auto (taille du widget)"), (1, "Petite"), (2, "Moyenne"), (3, "Grande"), (4, "Très grande"), (WidgetCatalog.MaxCols, "Pleine largeur")];

    private Dashboard Page => _pages.FirstOrDefault(p => p.Id == _pageId) ?? _pages[0];

    protected override async Task OnInitializedAsync()
    {
        _catalogue = Widgets.Load();
        await ChargerAsync();
        Tableaux.Changed += SurChangement;
    }

    private async Task ChargerAsync()
    {
        _pages = [.. await Tableaux.ListAsync()];
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
                await _module.InvokeVoidAsync("demarrer", _racine, _catalogue.Widgets.Select(WidgetApi.Vue).Select(PourJs));
            }

            if (_module is not null && _aAppliquer)
            {
                _aAppliquer = false;
                await _module.InvokeVoidAsync("appliquer", Page.Sections.Where(s => !s.Collapsed).SelectMany(s => s.Cards)
                    .Select(c => new { id = c.Id, widget = c.Widget, entities = c.Entities, settings = c.Settings }));
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

    private (int Cols, int Rows) Taille(DashboardCard carte)
    {
        var defaut = _catalogue.Find(carte.Widget)?.Manifest.Size ?? new WidgetSize();
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
        var sections = DashboardProposal.Propose(await Registre.ListAsync(), _catalogue, radiateurs);
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

    public async ValueTask DisposeAsync()
    {
        Tableaux.Changed -= SurChangement;
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
