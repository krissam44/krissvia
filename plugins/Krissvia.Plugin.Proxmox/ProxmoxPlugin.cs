using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.Proxmox;

/// <summary>Ce que l'utilisateur a choisi pour une machine : la surveiller (alerte si elle s'arrête), la relancer seule.</summary>
public sealed record GuestPreference(int VmId, bool Tracked = true, bool AutoRestart = false);

/// <summary>
/// Les préférences des machines, gardées par le plugin dans son dossier de données (<c>preferences.json</c>) : elles se
/// règlent depuis la carte de chaque machine, pas dans les réglages.
/// </summary>
public sealed class PreferenceStore(string? fichier)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Dictionary<int, GuestPreference> Load()
    {
        if (fichier is null || !File.Exists(fichier)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<GuestPreference>>(File.ReadAllText(fichier), Json) ?? []).ToDictionary(p => p.VmId);
        }
        catch (JsonException)
        {
            return [];   // illisible : les valeurs par défaut (surveillée, pas de relance) plutôt qu'un plugin qui ne démarre pas
        }
    }

    public void Save(IEnumerable<GuestPreference> preferences)
    {
        if (fichier is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        var temporaire = fichier + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(preferences.OrderBy(p => p.VmId).ToList(), Json));
        File.Move(temporaire, fichier, overwrite: true);   // jamais un fichier à moitié écrit
    }
}

/// <summary>
/// Proxmox VE : les nœuds, les machines virtuelles et les conteneurs, leur état et leurs ressources ; démarrer, arrêter
/// proprement, redémarrer ; signaler un arrêt inattendu ; relancer seul ce qu'on a demandé de relancer.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Arrêt inattendu</b> (DomoMud) : seul un passage « en marche » → « arrêtée » d'une machine surveillée compte.
/// Une machine déjà arrêtée au premier relevé n'est pas une panne, et un arrêt demandé depuis Krissvia non plus. Il est
/// publié en <c>binary_sensor</c> de classe <c>problem</c> : la supervision du cœur en fait l'alerte.</item>
/// <item><b>Relance automatique</b>, avec le garde-fou de DomoMud : au-delà de <see cref="MaxAutoRestarts"/> relances en
/// <see cref="AutoRestartWindow"/>, c'est un vrai plantage — on cesse, et l'arrêt inattendu reste levé pour que l'alerte
/// parte. Une relance réussie ne lève rien : la machine tourne.</item>
/// <item>Trois relevés ratés : tout devient indisponible. Jeton sans droits (réponse vide) : dit une fois au journal,
/// avec le remède.</item>
/// </list>
/// </remarks>
internal sealed partial class ProxmoxPlugin(
    IProxmoxApi api,
    TimeProvider clock,
    ILogger<ProxmoxPlugin> logger,
    TimeSpan? periode = null,
    PreferenceStore? preferences = null) : ICorePlugin
{
    public const string Start = "start";
    public const string Shutdown = "shutdown";
    public const string Reboot = "reboot";
    public const string SetTracked = "set_tracked";
    public const string SetAutoRestart = "set_auto_restart";
    public const int MaxAutoRestarts = 3;
    public const int EchecsAvantIndisponible = 3;
    public static readonly TimeSpan AutoRestartWindow = TimeSpan.FromMinutes(30);

    private static readonly IReadOnlyList<string> CommandesMachine = [Start, Shutdown, Reboot, SetTracked, SetAutoRestart];

    private readonly TimeSpan _periode = periode ?? TimeSpan.FromSeconds(30);
    private readonly PreferenceStore _stockage = preferences ?? new PreferenceStore(null);
    private readonly Dictionary<int, GuestPreference> _preferences = (preferences ?? new PreferenceStore(null)).Load();
    private readonly Lock _verrou = new();
    private readonly Dictionary<int, ProxmoxGuest> _invites = [];
    private IReadOnlyList<ProxmoxNode> _noeuds = [];
    private readonly HashSet<int> _arretsInattendus = [];
    private readonly HashSet<int> _arretsDemandes = [];
    private readonly HashSet<int> _relancesAttendues = [];
    private readonly Dictionary<int, List<DateTimeOffset>> _relances = [];
    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _envoyes = new(StringComparer.Ordinal);
    private string? _declaration;
    private int _echecs;
    private bool _disponible;
    private bool _sansDroitsSignale;

    public CoreClient? Coeur { get; set; }

    public static string GuestId(int vmid) => $"vm{vmid}";
    public static string StopId(int vmid) => $"vm{vmid}:arret";

    public async Task RunAsync(CancellationToken ct)
    {
        using var minuterie = new PeriodicTimer(_periode, clock);
        try
        {
            do
            {
                await CycleAsync(ct);
            }
            while (await minuterie.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Un relevé (les tests l'appellent sans attendre la minuterie).</summary>
    internal async Task CycleAsync(CancellationToken ct)
    {
        ProxmoxSnapshot releve;
        try
        {
            releve = await api.ResourcesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ProxmoxAuthenticationException)
        {
            await EchecAsync(ex, ct);
            return;
        }

        if (releve.NoRights && !_sansDroitsSignale) SansDroits(logger);
        _sansDroitsSignale = releve.NoRights;

        var aRelancer = new List<ProxmoxGuest>();
        lock (_verrou)
        {
            _echecs = 0;
            _noeuds = releve.Nodes;
            foreach (var g in releve.Guests)
            {
                var avant = _invites.GetValueOrDefault(g.VmId);
                _invites[g.VmId] = g;
                if (g.Running)
                {
                    _arretsInattendus.Remove(g.VmId);
                    _arretsDemandes.Remove(g.VmId);
                    _relancesAttendues.Remove(g.VmId);
                    continue;
                }

                // Relancée au relevé précédent et toujours arrêtée : la relance n'a pas pris, c'est une panne à dire.
                if (_relancesAttendues.Remove(g.VmId))
                {
                    _arretsInattendus.Add(g.VmId);
                    RelanceSansEffet(logger, g.Name);
                    continue;
                }

                // Seul un passage « en marche » → « arrêtée » compte : au premier relevé, on ne sait pas d'où l'on part.
                if (avant is not { Running: true }) continue;
                if (_arretsDemandes.Remove(g.VmId))
                {
                    ArretDemande(logger, g.TypeLabel, g.VmId, g.Name);
                    continue;
                }

                var pref = Preference(g.VmId);
                if (!pref.Tracked) continue;
                if (pref.AutoRestart && Relance(g.VmId)) aRelancer.Add(g);
                else
                {
                    _arretsInattendus.Add(g.VmId);
                    if (pref.AutoRestart) RelanceAbandonnee(logger, g.Name, MaxAutoRestarts, (int)AutoRestartWindow.TotalMinutes);
                    else ArretInattendu(logger, g.TypeLabel, g.VmId, g.Name, g.Node);
                }
            }

            foreach (var parti in _invites.Keys.Except(releve.Guests.Select(g => g.VmId)).ToList())
            {
                _invites.Remove(parti);
                _arretsInattendus.Remove(parti);
            }
        }

        foreach (var g in aRelancer) await RelancerAsync(g, ct);

        var retour = !_disponible;
        _disponible = true;
        await PublierAsync(retour, ct);
    }

    /// <summary>Sous le verrou : vrai si une relance de plus reste permise dans la fenêtre, et la compte.</summary>
    private bool Relance(int vmid)
    {
        var maintenant = clock.GetUtcNow();
        var tentatives = _relances.TryGetValue(vmid, out var l) ? l : _relances[vmid] = [];
        tentatives.RemoveAll(t => maintenant - t >= AutoRestartWindow);
        if (tentatives.Count >= MaxAutoRestarts) return false;
        tentatives.Add(maintenant);
        return true;
    }

    private async Task RelancerAsync(ProxmoxGuest g, CancellationToken ct)
    {
        int tentative;
        lock (_verrou) tentative = _relances[g.VmId].Count;
        RelanceAuto(logger, g.Name, tentative, MaxAutoRestarts);
        try
        {
            await api.ActionAsync(g, Start, ct);
            lock (_verrou) _relancesAttendues.Add(g.VmId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ProxmoxAuthenticationException)
        {
            // La relance n'a pas pu partir : c'est un arrêt inattendu, l'alerte doit le dire.
            lock (_verrou) _arretsInattendus.Add(g.VmId);
            RelanceEchouee(logger, g.Name, ex.Message);
        }
    }

    private GuestPreference Preference(int vmid) => _preferences.GetValueOrDefault(vmid) ?? new GuestPreference(vmid);

    private async Task PublierAsync(bool retour, CancellationToken ct)
    {
        Declare? declaration;
        List<EntityState> etats;
        lock (_verrou)
        {
            var d = Declaration();
            var json = JsonSerializer.Serialize(d);
            declaration = json == _declaration ? null : d;
            _declaration = json;

            etats = [.. Etats().Where(e => _envoyes.GetValueOrDefault(e.EntityUniqueId) != e.Value.GetRawText())];
            foreach (var e in etats) _envoyes[e.EntityUniqueId] = e.Value.GetRawText();
        }

        if (Coeur is not { } coeur) return;
        // Déclarer d'abord : une machine apparue doit exister au registre avant que son état n'arrive.
        if (declaration is not null) await coeur.SendAsync(declaration, ct);
        if (etats.Count > 0) await coeur.SendAsync(new StateBatch { States = etats }, ct);
        if (retour) await coeur.SendAsync(new Availability { Online = true }, ct);
    }

    /// <summary>Sous le verrou.</summary>
    private List<EntityState> Etats()
    {
        var etats = new List<EntityState>();
        foreach (var g in _invites.Values.OrderBy(g => g.VmId))
        {
            etats.Add(Noter(GuestId(g.VmId), Objet(g)));
            etats.Add(Noter(StopId(g.VmId), _arretsInattendus.Contains(g.VmId)));
        }

        foreach (var n in _noeuds)
        {
            etats.Add(Noter($"noeud:{n.Name}:hors_ligne", !n.Online));
            if (n.Online)
            {
                etats.Add(Noter($"noeud:{n.Name}:processeur", Math.Round(n.CpuFraction * 100, 1)));
                if (n.MaxMem > 0) etats.Add(Noter($"noeud:{n.Name}:memoire", Math.Round(100.0 * n.Mem / n.MaxMem, 1)));
            }
        }

        return etats;
    }

    /// <summary>La valeur que dessine la carte « Machine virtuelle ».</summary>
    private JsonObject Objet(ProxmoxGuest g)
    {
        var pref = Preference(g.VmId);
        return new JsonObject
        {
            ["vmid"] = g.VmId, ["name"] = g.Name, ["type"] = g.IsVm ? "vm" : "lxc", ["node"] = g.Node, ["status"] = g.Status,
            ["cpu_percent"] = g.Running ? Math.Round(g.CpuFraction * 100, 1) : null, ["max_cpu"] = g.MaxCpu,
            ["memory_percent"] = g.Running && g.MaxMem > 0 ? Math.Round(100.0 * g.Mem / g.MaxMem, 1) : null,
            ["max_memory_bytes"] = g.MaxMem,
            // Une VM sans agent invité ne remonte pas son disque utilisé (0) : on ne connaît que la taille allouée.
            ["disk_percent"] = g.Disk > 0 && g.MaxDisk > 0 ? Math.Round(100.0 * g.Disk / g.MaxDisk, 1) : null,
            ["max_disk_bytes"] = g.MaxDisk,
            ["uptime_s"] = g.Running ? g.Uptime : null,
            ["tracked"] = pref.Tracked, ["auto_restart"] = pref.AutoRestart,
            ["unexpected_stop"] = _arretsInattendus.Contains(g.VmId),
        };
    }

    private EntityState Noter(string id, object valeur)
    {
        var json = valeur is JsonNode n ? JsonSerializer.SerializeToElement(n) : JsonSerializer.SerializeToElement(valeur);
        _valeurs[id] = json;
        return new EntityState { EntityUniqueId = id, Value = json };
    }

    /// <summary>Sous le verrou.</summary>
    private Declare Declaration()
    {
        var appareils = new List<DeviceDescriptor>();
        foreach (var g in _invites.Values.OrderBy(g => g.VmId))
        {
            appareils.Add(new DeviceDescriptor
            {
                UniqueId = GuestId(g.VmId), Name = g.Name, Manufacturer = "Proxmox", Model = $"{g.TypeLabel} {g.VmId}",
                Entities =
                [
                    new EntityDescriptor { UniqueId = GuestId(g.VmId), Kind = EntityKind.Sensor, Name = "Machine", DeviceClass = DeviceClasses.VirtualMachine, Commands = CommandesMachine },
                    new EntityDescriptor { UniqueId = StopId(g.VmId), Kind = EntityKind.BinarySensor, Name = "Arrêt inattendu", DeviceClass = DeviceClasses.Problem },
                ],
            });
        }

        foreach (var n in _noeuds)
        {
            appareils.Add(new DeviceDescriptor
            {
                UniqueId = $"noeud:{n.Name}", Name = $"Nœud {n.Name}", Manufacturer = "Proxmox", Model = "Nœud",
                Entities =
                [
                    new EntityDescriptor { UniqueId = $"noeud:{n.Name}:hors_ligne", Kind = EntityKind.BinarySensor, Name = "Hors ligne", DeviceClass = DeviceClasses.Problem },
                    new EntityDescriptor { UniqueId = $"noeud:{n.Name}:processeur", Kind = EntityKind.Sensor, Name = "Processeur", Unit = "%", StateClass = StateClass.Measurement },
                    new EntityDescriptor { UniqueId = $"noeud:{n.Name}:memoire", Kind = EntityKind.Sensor, Name = "Mémoire", Unit = "%", StateClass = StateClass.Measurement },
                ],
            });
        }

        return new Declare { Devices = appareils };
    }

    public IReadOnlyList<Message> Snapshot()
    {
        var messages = new List<Message>();
        lock (_verrou)
        {
            messages.Add(Declaration());
            if (_valeurs.Count > 0)
                messages.Add(new StateBatch { States = [.. _valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value })] });
        }

        messages.Add(new Availability { Online = _disponible });
        return messages;
    }

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        ProxmoxGuest? g;
        lock (_verrou) g = _invites.Values.FirstOrDefault(i => GuestId(i.VmId) == request.EntityUniqueId);
        if (g is null) return Echec(request, $"« {request.EntityUniqueId} » : machine inconnue (supprimée ?)");

        switch (request.Command)
        {
            case Start or Shutdown or Reboot:
                // Avant l'ordre : le relevé qui suit verra l'arrêt, et il ne doit pas le prendre pour une panne.
                if (request.Command == Shutdown) lock (_verrou) _arretsDemandes.Add(g.VmId);
                try
                {
                    await api.ActionAsync(g, request.Command, ct);
                }
                catch (ProxmoxAuthenticationException ex)
                {
                    lock (_verrou) _arretsDemandes.Remove(g.VmId);
                    return Echec(request, ex.Message);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    lock (_verrou) _arretsDemandes.Remove(g.VmId);
                    return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Unreachable, Detail = ex.Message };
                }

                Ordre(logger, request.Command, g.TypeLabel, g.VmId, g.Name);
                return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Accepted };

            case SetTracked or SetAutoRestart:
                if (request.Args is not { ValueKind: JsonValueKind.Object } args
                    || !args.TryGetProperty("enabled", out var e) || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Echec(request, "attendu : {\"enabled\": true}");
                lock (_verrou)
                {
                    var pref = Preference(g.VmId);
                    pref = request.Command == SetTracked ? pref with { Tracked = e.GetBoolean() } : pref with { AutoRestart = e.GetBoolean() };
                    _preferences[g.VmId] = pref;
                    if (!pref.Tracked) _arretsInattendus.Remove(g.VmId);   // plus surveillée : plus de panne à dire
                    _stockage.Save(_preferences.Values);
                }

                await PublierAsync(false, ct);
                return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Accepted };

            default:
                return Echec(request, $"commande « {request.Command} » inconnue");
        }
    }

    private static CommandResult Echec(CommandRequest r, string detail) => new() { Id = r.Id, Outcome = CommandOutcome.Failed, Detail = detail };

    private async Task EchecAsync(Exception ex, CancellationToken ct)
    {
        int echecs;
        lock (_verrou) echecs = ++_echecs;
        if (ex is ProxmoxAuthenticationException) Refus(logger, ex.Message);
        else ReleveRate(logger, echecs, ex.Message);

        if (echecs < EchecsAvantIndisponible || !_disponible) return;
        _disponible = false;
        lock (_verrou) _envoyes.Clear();   // au retour, tout sera renvoyé
        Muet(logger, echecs);
        if (Coeur is { } coeur) await coeur.SendAsync(new Availability { Online = false }, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proxmox : connecté, mais aucune machine visible. Le jeton n'a probablement pas les droits : décocher « Privilege Separation » à sa création, ou lui donner le rôle PVEAuditor (et PVEVMUser pour démarrer et arrêter) sur « / » (Datacenter → Permissions → Add → API Token Permission).")]
    private static partial void SansDroits(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Type} {VmId} « {Nom} » sur {Noeud} s'est arrêtée.")]
    private static partial void ArretInattendu(ILogger logger, string type, int vmId, string nom, string noeud);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Type} {VmId} « {Nom} » arrêtée à la demande.")]
    private static partial void ArretDemande(ILogger logger, string type, int vmId, string nom);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relance automatique de « {Nom} » (tentative {Tentative}/{Max}).")]
    private static partial void RelanceAuto(ILogger logger, string nom, int tentative, int max);

    [LoggerMessage(Level = LogLevel.Error, Message = "« {Nom} » arrêtée {Max} fois en {Minutes} min : relance automatique abandonnée, intervention nécessaire.")]
    private static partial void RelanceAbandonnee(ILogger logger, string nom, int max, int minutes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relance automatique de « {Nom} » en échec : {Motif}")]
    private static partial void RelanceEchouee(ILogger logger, string nom, string motif);

    [LoggerMessage(Level = LogLevel.Error, Message = "« {Nom} » toujours arrêtée après sa relance automatique.")]
    private static partial void RelanceSansEffet(ILogger logger, string nom);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ordre {Commande} : {Type} {VmId} « {Nom} ».")]
    private static partial void Ordre(ILogger logger, string commande, string type, int vmId, string nom);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proxmox : {Motif}.")]
    private static partial void Refus(ILogger logger, string motif);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proxmox injoignable (relevé raté n° {Echecs}) : {Motif}")]
    private static partial void ReleveRate(ILogger logger, int echecs, string motif);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proxmox muet depuis {Echecs} relevés : machines indisponibles.")]
    private static partial void Muet(ILogger logger, int echecs);
}
