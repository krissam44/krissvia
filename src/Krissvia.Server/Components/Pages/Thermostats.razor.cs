using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;
using Microsoft.JSInterop;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Thermostats : une carte par thermostat, qui est le widget de son entité <c>climate</c> (celui choisi pour l'entité,
/// sinon le widget « thermostat » standard) — la même carte que sur le tableau de bord, à la manière de l'écran
/// Thermostats de DomoMud.
/// </summary>
/// <remarks>
/// Les cartes envoient leurs ordres par l'API des entités, comme tout widget : l'écran ne peut produire aucun état
/// qu'un appel d'API ne produirait pas. La liste est relue toutes les 2 s pour voir arriver un thermostat créé
/// ailleurs ; les valeurs, elles, arrivent par le flux en direct.
/// </remarks>
public partial class Thermostats : IAsyncDisposable
{
    /// <summary>Le widget d'un thermostat dont l'entité n'en a pas d'affichable ici (un widget Blazor).</summary>
    private const string WidgetParDefaut = "thermostat";

    [Inject] private ThermostatService Service { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private WidgetCatalogService Widgets { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private sealed record Carte(string Id, string Nom, string? EntityId, string Widget, IReadOnlyDictionary<string, string> Entities);

    private List<Carte> _cartes = [];
    private WidgetCatalogSnapshot _catalogue = new([], []);
    private ElementReference _racine;
    private IJSObjectReference? _module;
    private bool _aAppliquer = true;
    private string? _erreurNavigateur;
    private Timer? _minuterie;

    protected override void OnInitialized()
    {
        _catalogue = Widgets.Load();
        Relire();
        _minuterie = new Timer(_ => InvokeAsync(() =>
        {
            if (Relire()) StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Recalcule les cartes ; vrai si elles ont changé (thermostat ajouté, renommé, publié, autre widget).</summary>
    private bool Relire()
    {
        var entites = Registre.Loaded;
        var cartes = Service.List().Select(t =>
        {
            var entite = entites.FirstOrDefault(e => e.IntegrationId == ThermostatEntities.IntegrationId && e.UniqueId == t.Id);
            var nom = t.Name is { Length: > 0 } n ? n : t.Id;
            if (entite is null) return new Carte(t.Id, nom, null, WidgetParDefaut, new Dictionary<string, string>());

            var resolue = WidgetResolver.Resolve(new DashboardCard { Id = t.Id, Entity = entite.EntityId }, entites, _catalogue);
            return resolue.Widget is { IsBlazor: false } widget
                ? new Carte(t.Id, nom, entite.EntityId, widget.Manifest.Id, resolue.Entities)
                : new Carte(t.Id, nom, entite.EntityId, WidgetParDefaut,
                    new Dictionary<string, string> { [WidgetParDefaut] = entite.EntityId });
        }).ToList();

        if (cartes.Select(Cle).SequenceEqual(_cartes.Select(Cle))) return false;
        _cartes = cartes;
        _aAppliquer = true;
        return true;
    }

    private static string Cle(Carte c) =>
        $"{c.Id}|{c.Nom}|{c.EntityId}|{c.Widget}|{string.Join(",", c.Entities.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}"))}";

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
                await _module.InvokeVoidAsync("appliquer", _cartes.Where(c => c.EntityId is not null)
                    .Select(c => new { id = c.Id, widget = c.Widget, entities = c.Entities }));
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
            System.Text.Json.JsonSerializer.Serialize(vue, ThermostatService.Json))!;

    public async ValueTask DisposeAsync()
    {
        _minuterie?.Dispose();
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
