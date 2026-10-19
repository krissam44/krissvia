using System.Text.Json;
using Krissvia.Core.Configuration;
using Krissvia.Core.Navigation;

namespace Krissvia.Server.Components.Layout;

/// <summary>Une entrée du menu latéral. <see cref="Key"/> est STABLE : c'est elle que la personnalisation retient.</summary>
public sealed record MenuEntry(string Key, string Label, string Href, string Icon, bool Exact = false)
{
    public bool Hidden { get; init; }
}

/// <summary>
/// Menu latéral : les pages de Krissvia, rangées et masquées selon la personnalisation, commune à tous les comptes
/// (document <c>disposition</c> de la collection <c>menu</c>). L'entrée « Services » se déplie en liste des plugins.
/// </summary>
/// <remarks>
/// La logique de rangement est dans <see cref="MenuLayout"/> (testée) ; ce service ne fait que fournir le catalogue
/// et persister.
/// </remarks>
public sealed class MenuService
{
    public const string Collection = "menu";
    private const string DocumentId = "disposition";

    /// <summary>Les pages, dans l'ordre par défaut.</summary>
    public static readonly IReadOnlyList<MenuEntry> Pages =
    [
        new("tableau", "Tableau de bord", "/", Icons.Material.Filled.Dashboard, Exact: true),
        new("thermostats", "Thermostats", "/thermostats", Icons.Material.Filled.Thermostat),
        new("sondes", "Sondes", "/sondes", Icons.Material.Filled.DeviceThermostat),
        new("actionneurs", "Actionneurs", "/actionneurs", Icons.Material.Filled.Lightbulb),
        new("programmations", "Programmations", "/programmations", Icons.Material.Filled.Schedule),
        new("delestage", "Délestage", "/delestage", Icons.Material.Filled.Bolt),
        new("absence", "Absence", "/absence", Icons.Material.Filled.Luggage),
        new("entites", "Entités", "/entites", Icons.Material.Filled.Devices),
        new("services", "Services", "/parametres/services", Icons.Material.Filled.Extension, Exact: true),
        new("notifications", "Notifications", "/parametres/notifications", Icons.Material.Filled.NotificationsActive),
        new("comptes", "Comptes", "/parametres/comptes", Icons.Material.Filled.ManageAccounts),
    ];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IConfigurationStore _store;
    private readonly PluginSupervisor _supervisor;
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private IReadOnlyList<MenuItemSetting>? _reglages;

    public MenuService(IConfigurationStore store, PluginSupervisor supervisor)
    {
        _store = store;
        _supervisor = supervisor;
        _supervisor.EnabledChanged += () => Changed?.Invoke();
    }

    /// <summary>Personnalisation enregistrée, ou service activé/désactivé. Levé hors du verrou.</summary>
    public event Action? Changed;

    /// <summary>Les entrées disponibles, dans l'ordre du catalogue.</summary>
    public IReadOnlyList<MenuEntry> Available() => Pages;

    /// <summary>
    /// Les plugins installés, actifs ou non : le groupe « Services » du menu les liste, chacun menant à sa page de
    /// configuration. Un plugin par entrée de premier niveau encombrait le menu à mesure qu'on en ajoute.
    /// </summary>
    public IReadOnlyList<ServiceSnapshot> Plugins() =>
        [.. _supervisor.Snapshots().Where(s => s.Installed).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>La clé de l'entrée qui se déplie en liste des plugins.</summary>
    public const string ServicesKey = "services";

    /// <summary>Toutes les entrées disponibles, masquées comprises, dans l'ordre choisi : pour la personnalisation.</summary>
    public async Task<IReadOnlyList<MenuEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        var disponibles = Available().ToDictionary(e => e.Key, StringComparer.Ordinal);
        var rangees = MenuLayout.Arrange([.. disponibles.Keys], await ReglagesAsync(ct));
        return [.. rangees.Select(r => disponibles[r.Key] with { Hidden = r.Hidden })];
    }

    /// <summary>Les entrées à afficher.</summary>
    public async Task<IReadOnlyList<MenuEntry>> GetVisibleAsync(CancellationToken ct = default) =>
        [.. (await GetEntriesAsync(ct)).Where(e => !e.Hidden)];

    /// <summary>Enregistre l'ordre et le masquage choisis pour les entrées disponibles.</summary>
    public async Task SaveAsync(IReadOnlyList<MenuEntry> entries, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var fusion = MenuLayout.Merge(await LireAsync(ct), [.. entries.Select(e => new MenuItemSetting(e.Key, e.Hidden))]);
            await _store.SaveAsync(Collection, new ConfigurationDocument(DocumentId, JsonSerializer.Serialize(fusion, Json)), ct);
            _reglages = fusion;
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>L'entrée de la page affichée, pour le fil d'Ariane : la plus longue adresse qui la préfixe.</summary>
    public MenuEntry? Current(string path)
    {
        if (path is "" or "/") return Pages[0];
        return Available()
            .Where(e => path.Equals(e.Href, StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith(e.Href + "/", StringComparison.OrdinalIgnoreCase))
            .MaxBy(e => e.Href.Length);
    }

    private async Task<IReadOnlyList<MenuItemSetting>> ReglagesAsync(CancellationToken ct)
    {
        if (_reglages is { } connus) return connus;

        await _verrou.WaitAsync(ct);
        try
        {
            return await LireAsync(ct);
        }
        finally
        {
            _verrou.Release();
        }
    }

    // Sous le verrou.
    private async Task<IReadOnlyList<MenuItemSetting>> LireAsync(CancellationToken ct)
    {
        if (_reglages is { } connus) return connus;

        IReadOnlyList<MenuItemSetting> lus = [];
        if ((await _store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == DocumentId) is { } document)
        {
            try
            {
                lus = JsonSerializer.Deserialize<List<MenuItemSetting>>(document.Json, Json) ?? [];
            }
            catch (JsonException)
            {
                // Une personnalisation illisible ne doit pas priver de menu : on repart du catalogue.
            }
        }

        return _reglages = lus;
    }
}
