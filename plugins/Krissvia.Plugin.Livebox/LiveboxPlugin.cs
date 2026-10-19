using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.Livebox;

/// <summary>Un appareil dont on suit la présence (le téléphone de quelqu'un) : son adresse MAC et un nom.</summary>
public sealed record TrackedDevice
{
    public string Mac { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>
/// La liste des appareils suivis, gardée par le plugin dans son dossier de données (<c>suivis.json</c>) : on la change
/// depuis la grille « Appareils du réseau », pas dans les réglages. Au premier démarrage, elle part des réglages
/// « Suivis » s'il y en a.
/// </summary>
public sealed class TrackingStore(string? fichier)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<TrackedDevice> Load(IReadOnlyList<TrackedDevice> depart)
    {
        if (fichier is not null && File.Exists(fichier))
        {
            try
            {
                return JsonSerializer.Deserialize<List<TrackedDevice>>(File.ReadAllText(fichier), Json) ?? [];
            }
            catch (JsonException)
            {
                // Illisible : on repart des réglages plutôt que de perdre toute présence.
            }
        }

        return [.. depart];
    }

    public void Save(IReadOnlyList<TrackedDevice> suivis)
    {
        if (fichier is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        var temporaire = fichier + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(suivis, Json));
        File.Move(temporaire, fichier, overwrite: true);   // jamais un fichier à moitié écrit
    }
}

/// <summary>
/// La Livebox : présence des appareils suivis, supervision de la connexion, et la liste des appareils du réseau.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Présence : un capteur par appareil suivi ; présent s'il est connecté ou l'a été depuis moins du délai de grâce
/// (un téléphone en veille quitte le Wi-Fi sans quitter la maison). Son retour compte aussitôt.</item>
/// <item>Supervision (désactivable) : classes standard, les mêmes pour toute box, et l'objet « Connexion » complet que
/// dessine la carte « Connexion Internet ».</item>
/// <item>« Appareils du réseau » : tout ce que voit la box ; la commande <c>set_tracked</c> y ajoute ou en retire un
/// appareil suivi, enregistré aussitôt, et l'appareil est déclaré (ou marqué manquant) dans la foulée.</item>
/// <item>Trois relevés ratés de suite : tout devient indisponible ; le cœur ne prend jamais une box muette pour une
/// maison vide. Lecture seule sur la box.</item>
/// </list>
/// </remarks>
internal sealed partial class LiveboxPlugin(
    IReadOnlyList<TrackedDevice> suivis,
    ILiveboxApi box,
    TimeProvider clock,
    ILogger<LiveboxPlugin> logger,
    TimeSpan grace,
    TimeSpan? periode = null,
    bool supervision = true,
    TrackingStore? stockage = null) : ICorePlugin
{
    public const string BoxDeviceId = "livebox";
    public const string NetworkId = "reseau";
    public const string ConnectionId = "connexion";
    public const string SetTracked = "set_tracked";
    public const int EchecsAvantIndisponible = 3;
    private static readonly TimeSpan PeriodeWan = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _periode = periode ?? TimeSpan.FromSeconds(60);
    private readonly TrackingStore _stockage = stockage ?? new TrackingStore(null);
    private readonly List<TrackedDevice> _suivis =
        [.. (stockage ?? new TrackingStore(null)).Load(suivis).Select(s => s with { Mac = LiveboxParser.Normaliser(s.Mac) })];
    private readonly Lock _verrou = new();
    private readonly Dictionary<string, DateTimeOffset> _vusLe = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _envoyes = new(StringComparer.Ordinal);   // id → dernier JSON envoyé
    private IReadOnlyList<LiveboxHost> _appareils = [];
    private int _echecs;
    private bool _disponible;
    private DateTimeOffset? _wanLe;
    private string? _derniereListe;

    public CoreClient? Coeur { get; set; }

    public static string PresenceId(string mac) => $"presence:{mac.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant()}";

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
        IReadOnlyList<LiveboxHost> appareils;
        WanStatus? wan = null;
        try
        {
            appareils = await box.HostsAsync(ct);
            if (supervision && (_wanLe is null || clock.GetUtcNow() - _wanLe >= PeriodeWan))
            {
                wan = await box.WanAsync(ct);
                _wanLe = clock.GetUtcNow();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or LiveboxAuthenticationException)
        {
            await EchecAsync(ex, ct);
            return;
        }

        List<EntityState> etats;
        lock (_verrou)
        {
            _echecs = 0;
            _appareils = appareils;
            var maintenant = clock.GetUtcNow();
            foreach (var a in appareils.Where(a => a.Active)) _vusLe[a.Mac] = maintenant;

            etats = Presences(maintenant);
            etats.Add(Noter(NetworkId, Reseau()));
            if (wan is not null)
            {
                if (wan.Up is { } up) etats.Add(Noter("internet", up));
                if (wan.PublicIp is { } ip) etats.Add(Noter("ip", ip));
                if (wan.DownKbps is { } d) etats.Add(Noter("descendant", d));
                if (wan.UpKbps is { } m) etats.Add(Noter("montant", m));
                etats.Add(Noter(ConnectionId, Connexion(wan)));
            }

            // Seulement ce qui a changé : la liste du réseau pèse quelques kilo-octets, inutile de la renvoyer chaque minute.
            etats = [.. etats.Where(e => _envoyes.GetValueOrDefault(e.EntityUniqueId) != e.Value.GetRawText())];
            foreach (var e in etats) _envoyes[e.EntityUniqueId] = e.Value.GetRawText();
        }

        JournaliserListe(appareils);
        // La disponibilité se tient même hors session : la resynchronisation suivante la dira telle qu'elle est.
        var retour = !_disponible;
        _disponible = true;
        if (Coeur is not { } coeur) return;
        if (etats.Count > 0) await coeur.SendAsync(new StateBatch { States = etats }, ct);
        if (retour) await coeur.SendAsync(new Availability { Online = true }, ct);
    }

    /// <summary>Sous le verrou.</summary>
    private List<EntityState> Presences(DateTimeOffset maintenant) =>
    [
        .. _suivis.Select(s => Noter(PresenceId(s.Mac), _vusLe.TryGetValue(s.Mac, out var vu) && maintenant - vu < grace)),
    ];

    /// <summary>Sous le verrou : tout ce que voit la box, les suivis en plus (même s'ils ne sont pas là en ce moment).</summary>
    private JsonObject Reseau()
    {
        var suivis = _suivis.ToDictionary(s => s.Mac, StringComparer.Ordinal);
        var hotes = _appareils.Select(a => (JsonNode)new JsonObject
        {
            ["mac"] = a.Mac, ["name"] = a.Name, ["ip"] = a.Ip, ["type"] = a.Type,
            ["active"] = a.Active, ["tracked"] = suivis.ContainsKey(a.Mac),
        });
        var absents = _suivis.Where(s => _appareils.All(a => a.Mac != s.Mac)).Select(s => (JsonNode)new JsonObject
        {
            ["mac"] = s.Mac, ["name"] = s.Name, ["ip"] = null, ["type"] = null, ["active"] = false, ["tracked"] = true,
        });
        return new JsonObject { ["hosts"] = new JsonArray([.. hotes, .. absents]) };
    }

    private static JsonObject Connexion(WanStatus w) => new()
    {
        ["up"] = w.Up, ["link_type"] = w.LinkType, ["public_ip"] = w.PublicIp,
        ["down_kbps"] = w.DownKbps, ["up_kbps"] = w.UpKbps, ["down_max_kbps"] = w.DownMaxKbps, ["up_max_kbps"] = w.UpMaxKbps,
        ["snr_down_db"] = w.NoiseMarginDown, ["snr_up_db"] = w.NoiseMarginUp,
        ["attenuation_down_db"] = w.AttenuationDown, ["attenuation_up_db"] = w.AttenuationUp, ["profile"] = w.Profile,
    };

    private EntityState Noter(string id, object valeur)
    {
        var json = valeur is JsonNode n ? JsonSerializer.SerializeToElement(n) : JsonSerializer.SerializeToElement(valeur);
        _valeurs[id] = json;
        return new EntityState { EntityUniqueId = id, Value = json };
    }

    private async Task EchecAsync(Exception ex, CancellationToken ct)
    {
        int echecs;
        lock (_verrou) echecs = ++_echecs;
        if (ex is LiveboxAuthenticationException) AuthentificationRefusee(logger, ex.Message);
        else ReleveRate(logger, echecs, ex.Message);

        if (echecs < EchecsAvantIndisponible || !_disponible) return;
        _disponible = false;
        lock (_verrou) _envoyes.Clear();   // au retour, tout sera renvoyé
        BoxMuette(logger, echecs);
        if (Coeur is { } coeur) await coeur.SendAsync(new Availability { Online = false }, ct);
    }

    /// <summary>La liste des appareils vus, dans le journal, seulement quand elle change.</summary>
    private void JournaliserListe(IReadOnlyList<LiveboxHost> appareils)
    {
        HashSet<string> suivis;
        lock (_verrou) suivis = _suivis.Select(s => s.Mac).ToHashSet(StringComparer.Ordinal);
        var liste = string.Join(", ", appareils.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => $"{a.Name} ({a.Mac}{(a.Active ? "" : ", déconnecté")}{(suivis.Contains(a.Mac) ? ", suivi" : "")})"));
        if (liste == _derniereListe) return;
        _derniereListe = liste;
        AppareilsVus(logger, appareils.Count, liste);
    }

    private Declare Declaration()
    {
        var appareils = new List<DeviceDescriptor>
        {
            new()
            {
                UniqueId = NetworkId, Name = "Réseau local", Manufacturer = "Orange",
                Entities = [new EntityDescriptor { UniqueId = NetworkId, Kind = EntityKind.Sensor, Name = "Appareils", DeviceClass = DeviceClasses.NetworkHosts, Commands = [SetTracked] }],
            },
        };

        // Deux parts indépendantes : la supervision de la connexion (classes standard, les mêmes pour toute box) et la
        // présence des appareils suivis (classe « presence », consommée par le cœur quelle que soit sa source).
        if (supervision)
        {
            appareils.Add(new DeviceDescriptor
            {
                UniqueId = BoxDeviceId, Name = "Livebox", Manufacturer = "Orange",
                Entities =
                [
                    new EntityDescriptor { UniqueId = ConnectionId, Kind = EntityKind.Sensor, Name = "Connexion", DeviceClass = DeviceClasses.InternetConnection },
                    new EntityDescriptor { UniqueId = "internet", Kind = EntityKind.BinarySensor, Name = "Internet", DeviceClass = DeviceClasses.Connectivity },
                    new EntityDescriptor { UniqueId = "ip", Kind = EntityKind.Sensor, Name = "IP publique", DeviceClass = DeviceClasses.PublicIp },
                    new EntityDescriptor { UniqueId = "descendant", Kind = EntityKind.Sensor, Name = "Débit descendant", DeviceClass = DeviceClasses.DataRate, Unit = Units.KilobitPerSecond, StateClass = StateClass.Measurement },
                    new EntityDescriptor { UniqueId = "montant", Kind = EntityKind.Sensor, Name = "Débit montant", DeviceClass = DeviceClasses.DataRate, Unit = Units.KilobitPerSecond, StateClass = StateClass.Measurement },
                ],
            });
        }

        lock (_verrou)
        {
            appareils.AddRange(_suivis.Select(s => new DeviceDescriptor
            {
                UniqueId = PresenceId(s.Mac), Name = s.Name is { Length: > 0 } n ? n : s.Mac,
                Entities = [new EntityDescriptor { UniqueId = PresenceId(s.Mac), Kind = EntityKind.BinarySensor, Name = "Présence", DeviceClass = DeviceClasses.Presence }],
            }));
        }

        return new Declare { Devices = appareils };
    }

    public IReadOnlyList<Message> Snapshot()
    {
        var messages = new List<Message> { Declaration() };
        lock (_verrou)
        {
            if (_valeurs.Count > 0)
                messages.Add(new StateBatch { States = [.. _valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value })] });
        }

        messages.Add(new Availability { Online = _disponible });
        return messages;
    }

    /// <summary><c>set_tracked</c> sur « Appareils » : suivre ou ne plus suivre un appareil, aussitôt pris en compte.</summary>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        if (request.EntityUniqueId != NetworkId || request.Command != SetTracked)
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = "la Livebox est en lecture seule ; seul le suivi des appareils se règle" };
        if (request.Args is not { ValueKind: JsonValueKind.Object } args
            || !args.TryGetProperty("mac", out var m) || m.ValueKind != JsonValueKind.String
            || !args.TryGetProperty("tracked", out var t) || t.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = "attendu : {\"mac\": \"AA:BB:…\", \"tracked\": true}" };

        var mac = LiveboxParser.Normaliser(m.GetString()!);
        var suivre = t.GetBoolean();
        List<EntityState> etats;
        lock (_verrou)
        {
            var deja = _suivis.FindIndex(s => s.Mac == mac);
            if (suivre && deja < 0) _suivis.Add(new TrackedDevice { Mac = mac, Name = _appareils.FirstOrDefault(a => a.Mac == mac)?.Name ?? mac });
            else if (!suivre && deja >= 0) _suivis.RemoveAt(deja);
            if (!suivre) _valeurs.Remove(PresenceId(mac));

            _stockage.Save(_suivis);
            etats = Presences(clock.GetUtcNow());
            etats.Add(Noter(NetworkId, Reseau()));
            foreach (var e in etats) _envoyes[e.EntityUniqueId] = e.Value.GetRawText();
        }

        Suivi(logger, mac, suivre ? "suivi" : "plus suivi");
        if (Coeur is { } coeur)
        {
            // Déclarer d'abord : un nouvel appareil suivi doit exister au registre avant que son état n'arrive.
            await coeur.SendAsync(Declaration(), ct);
            await coeur.SendAsync(new StateBatch { States = etats }, ct);
        }

        return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Accepted };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "La box voit {Nombre} appareil(s) : {Liste}")]
    private static partial void AppareilsVus(ILogger logger, int nombre, string liste);

    [LoggerMessage(Level = LogLevel.Information, Message = "Appareil {Mac} : {Etat}.")]
    private static partial void Suivi(ILogger logger, string mac, string etat);

    [LoggerMessage(Level = LogLevel.Error, Message = "Livebox : {Motif}.")]
    private static partial void AuthentificationRefusee(ILogger logger, string motif);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Livebox injoignable (relevé raté n° {Echecs}) : {Motif}")]
    private static partial void ReleveRate(ILogger logger, int echecs, string motif);

    [LoggerMessage(Level = LogLevel.Error, Message = "Livebox muette depuis {Echecs} relevés : présences indisponibles (le cœur ne conclura pas à une maison vide).")]
    private static partial void BoxMuette(ILogger logger, int echecs);
}
