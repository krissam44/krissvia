using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Krissvia.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost.Services;

/// <summary>Ce que la page Services montre d'un plugin.</summary>
public enum ServiceStatus
{
    /// <summary>Désactivé, ou activé mais jamais lancé.</summary>
    Stopped,

    /// <summary>Lancé, pas encore connecté au cœur.</summary>
    Starting,

    /// <summary>Lancé et connecté : il fonctionne.</summary>
    Connected,

    /// <summary>Lancé depuis un moment sans se connecter : configuration fausse, cœur injoignable…</summary>
    NotConnected,

    /// <summary>Sorti alors qu'il est activé : relance programmée.</summary>
    Restarting,

    /// <summary>Connecté, mais lancé ailleurs (Visual Studio, autre machine) : le superviseur n'y touche pas.</summary>
    External,

    /// <summary>Activé, mais introuvable dans les dossiers de plugins.</summary>
    NotInstalled,
}

public sealed record ServiceSnapshot(
    string Id,
    string Name,
    string Description,
    bool Installed,
    bool Enabled,
    ServiceStatus Status,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    int Restarts,
    string? LastExit,
    string? PluginVersion,
    string? Directory,
    IReadOnlyList<string> Logs);

/// <summary>
/// Lance, surveille et arrête les plugins installés sur la machine du cœur (page Services).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Désactivé par défaut : rien ne démarre sans qu'on l'ait demandé. L'état activé est enregistré en base
/// (collection <c>services</c>) et rejoué au démarrage du cœur.</item>
/// <item>Chaque lancement reçoit un jeton neuf (variable d'environnement), inscrit auprès du hub : seul le
/// processus lancé peut ouvrir la session de ce plugin.</item>
/// <item>Un plugin déjà connecté depuis ailleurs n'est pas lancé : deux instances du même plugin se
/// remplaceraient l'une l'autre sans fin (un nouveau hello remplace la session).</item>
/// <item>Sortie d'un plugin activé : relance, espacée de plus en plus (2 s à 1 min) tant qu'il plante vite ; un
/// plugin resté en vie plus d'une minute repart du délai le plus court.</item>
/// </list>
/// </remarks>
public sealed partial class PluginSupervisor(
    IConfigurationStore store,
    PluginHub hub,
    PluginHostOptions options,
    IPluginLauncher launcher,
    TimeProvider clock,
    ILogger<PluginSupervisor> logger,
    IPluginSettingsProvider? settings = null)
{
    public const string Collection = "services";
    private const int LignesGardees = 200;
    private static readonly TimeSpan[] Attentes =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];
    private static readonly TimeSpan DureeStable = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DelaiConnexion = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, Service> _services = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _verrou = new(1, 1);

    private sealed class Service(string id)
    {
        public string Id { get; } = id;
        public PluginManifest? Fiche;
        public bool Active;
        public IPluginProcess? Processus;
        public DateTimeOffset? DemarreLe;
        public int Relances;
        public int EchecsRapides;
        public string? DerniereSortie;
        public CancellationTokenSource? RelanceProgrammee;
        public ConcurrentQueue<string> Journal { get; } = new();
    }

    /// <summary>Fiches <c>plugin.json</c> illisibles trouvées au chargement, pour la page Services.</summary>
    public IReadOnlyList<string> CatalogErrors { get; set; } = [];

    /// <summary>Adresse du point d'entrée des plugins, transmise à chaque plugin lancé.</summary>
    public string CoreUrl { get; set; } = "ws://127.0.0.1:12795/plugins";

    /// <summary>Plugins installés trouvés au chargement, et états enregistrés.</summary>
    public async Task LoadAsync(IReadOnlyList<PluginManifest> installes, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            foreach (var fiche in installes) _services.GetOrAdd(fiche.Id, id => new Service(id)).Fiche = fiche;

            foreach (var document in await store.ListAsync(Collection, ct))
            {
                var service = _services.GetOrAdd(document.Id, id => new Service(id));
                try
                {
                    using var json = JsonDocument.Parse(document.Json);
                    service.Active = json.RootElement.TryGetProperty("enabled", out var a) && a.ValueKind == JsonValueKind.True;
                }
                catch (JsonException)
                {
                    service.Active = false;
                }
            }
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Lance les plugins activés. Appelé une fois le cœur prêt à les recevoir.</summary>
    public async Task StartEnabledAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            foreach (var service in _services.Values.Where(s => s.Active)) Lancer(service);
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Active ou désactive un plugin. Renvoie la raison d'un refus, ou null.</summary>
    public async Task<string?> SetEnabledAsync(string id, bool actif, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var service = _services.GetOrAdd(id, i => new Service(i));
            if (actif)
            {
                if (service.Fiche is null) return $"le plugin « {id} » n'est pas installé.";
                if (service.Processus is null && hub.ConnectedPlugins.Contains(id))
                    return $"« {id} » est déjà connecté, lancé ailleurs (Visual Studio ?) : arrêtez cette instance d'abord.";
            }

            await store.SaveAsync(Collection, new ConfigurationDocument(id, JsonSerializer.Serialize(new { enabled = actif })), ct);
            service.Active = actif;

            if (actif)
            {
                service.EchecsRapides = 0;
                if (service.Processus is null) Lancer(service);
            }
            else
            {
                Arreter(service, "désactivé");
            }

            return null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Arrête puis relance un plugin activé.</summary>
    public async Task<string?> RestartAsync(string id, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (!_services.TryGetValue(id, out var service) || !service.Active) return $"« {id} » n'est pas activé.";
            Arreter(service, "relance demandée");
            service.EchecsRapides = 0;
            Lancer(service);
            return null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Arrêt du cœur : tous les plugins lancés s'arrêtent avec lui.</summary>
    public void StopAll()
    {
        foreach (var service in _services.Values) Arreter(service, "arrêt du cœur");
    }

    public IReadOnlyList<ServiceSnapshot> Snapshots()
    {
        var connectes = hub.ConnectedPlugins.ToHashSet(StringComparer.Ordinal);
        foreach (var id in connectes) _services.GetOrAdd(id, i => new Service(i));

        return _services.Values
            .Select(s => Instantane(s, connectes))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>La fiche du plugin installé, ou null.</summary>
    public PluginManifest? GetManifest(string id) => _services.TryGetValue(id, out var s) ? s.Fiche : null;

    public ServiceSnapshot? GetSnapshot(string id) =>
        Snapshots().FirstOrDefault(s => s.Id == id);

    // ── Interne (sous verrou) ────────────────────────────────────────────────

    private ServiceSnapshot Instantane(Service s, HashSet<string> connectes)
    {
        var connecte = connectes.Contains(s.Id);
        var maintenant = clock.GetUtcNow();
        var statut = s.Processus is not null
            ? connecte ? ServiceStatus.Connected
            : maintenant - s.DemarreLe < DelaiConnexion ? ServiceStatus.Starting : ServiceStatus.NotConnected
            : s.Active && s.Fiche is null ? ServiceStatus.NotInstalled
            : s.RelanceProgrammee is not null ? ServiceStatus.Restarting
            : connecte ? ServiceStatus.External
            : ServiceStatus.Stopped;

        return new ServiceSnapshot(
            s.Id, s.Fiche?.Name ?? s.Id, s.Fiche?.Description ?? "", s.Fiche is not null, s.Active, statut,
            s.Processus?.ProcessId, s.DemarreLe, s.Relances, s.DerniereSortie,
            connecte ? hub.GetPluginVersion(s.Id) : null, s.Fiche?.Directory, s.Journal.ToArray());
    }

    private void Lancer(Service service)
    {
        if (service.Fiche is not { } fiche || service.Processus is not null) return;

        service.RelanceProgrammee?.Cancel();
        service.RelanceProgrammee = null;

        var jeton = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        options.Tokens[service.Id] = jeton;   // seul ce lancement-ci pourra ouvrir la session

        try
        {
            // Les réglages de la page d'abord ; l'adresse, le jeton et l'identité ensuite, que rien ne peut écraser.
            var environnement = new Dictionary<string, string>(settings?.EnvironmentFor(service.Id) ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["Krissvia__Url"] = CoreUrl,
                ["Krissvia__Token"] = jeton,
                ["Krissvia__PluginId"] = service.Id,
            };
            var processus = launcher.Start(fiche, environnement, ligne => Noter(service, ligne));

            service.Processus = processus;
            service.DemarreLe = clock.GetUtcNow();
            Noter(service, $"── lancé (processus {processus.ProcessId})");
            Lance(logger, service.Id, processus.ProcessId);
            _ = SurveillerAsync(service, processus);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Noter(service, $"── lancement impossible : {ex.Message}");
            LancementImpossible(logger, service.Id, ex.Message);
            ProgrammerRelance(service);
        }
    }

    private async Task SurveillerAsync(Service service, IPluginProcess processus)
    {
        var code = await processus.Exited;

        await _verrou.WaitAsync();
        try
        {
            if (!ReferenceEquals(service.Processus, processus)) return;   // arrêté volontairement ou remplacé

            var duree = clock.GetUtcNow() - service.DemarreLe;
            service.Processus = null;
            service.DerniereSortie = $"code {code} le {clock.GetLocalNow():dd/MM HH:mm:ss}";
            Noter(service, $"── sorti, code {code}");
            Sorti(logger, service.Id, code);

            // Resté en vie plus d'une minute : ce n'est pas une série de plantages, on repart du délai le plus court.
            if (duree >= DureeStable) service.EchecsRapides = 0;
            if (service.Active) ProgrammerRelance(service);
        }
        finally
        {
            _verrou.Release();
        }
    }

    private void ProgrammerRelance(Service service)
    {
        var attente = Attentes[Math.Min(service.EchecsRapides, Attentes.Length - 1)];
        service.EchecsRapides++;
        var annulation = new CancellationTokenSource();
        service.RelanceProgrammee = annulation;
        Noter(service, $"── relance dans {attente.TotalSeconds:0} s");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(attente, clock, annulation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _verrou.WaitAsync();
            try
            {
                if (annulation.IsCancellationRequested || !service.Active) return;
                service.RelanceProgrammee = null;
                service.Relances++;
                Lancer(service);
            }
            finally
            {
                _verrou.Release();
            }
        });
    }

    private void Arreter(Service service, string motif)
    {
        service.RelanceProgrammee?.Cancel();
        service.RelanceProgrammee = null;
        options.Tokens.TryRemove(service.Id, out _);

        if (service.Processus is not { } processus) return;
        service.Processus = null;   // avant Kill : sa sortie ne doit pas être prise pour un plantage
        processus.Kill();
        Noter(service, $"── arrêté ({motif})");
        Arrete(logger, service.Id, motif);
    }

    private static void Noter(Service service, string ligne)
    {
        service.Journal.Enqueue(ligne);
        while (service.Journal.Count > LignesGardees && service.Journal.TryDequeue(out _)) { }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Service {Id} lancé (processus {Pid}).")]
    private static partial void Lance(ILogger logger, string id, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service {Id} sorti, code {Code}.")]
    private static partial void Sorti(ILogger logger, string id, int code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service {Id} arrêté ({Motif}).")]
    private static partial void Arrete(ILogger logger, string id, string motif);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service {Id} : lancement impossible ({Erreur}).")]
    private static partial void LancementImpossible(ILogger logger, string id, string erreur);
}
