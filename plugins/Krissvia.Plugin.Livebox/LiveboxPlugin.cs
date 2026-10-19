using System.Text.Json;
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
/// Présence par la Livebox (DomoMud) : un capteur « présence » par appareil suivi, et l'état de la connexion Internet.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Un appareil suivi est présent s'il est connecté, ou s'il l'a été il y a moins du délai de grâce : un téléphone
/// en veille profonde quitte le Wi-Fi sans quitter la maison. Son retour compte aussitôt.</item>
/// <item>Trois relevés ratés de suite : toutes les entités deviennent indisponibles. Le cœur ne prend jamais une box
/// muette pour une maison vide (DomoMud, 02/10/2026 : la dernière valeur restait « occupée » en vert).</item>
/// <item>Le journal liste les appareils que voit la box, à chaque changement : c'est là qu'on trouve l'adresse MAC des
/// téléphones à suivre.</item>
/// <item>Lecture seule : rien n'est jamais modifié sur la box.</item>
/// </list>
/// </remarks>
internal sealed partial class LiveboxPlugin(
    IReadOnlyList<TrackedDevice> suivis,
    ILiveboxApi box,
    TimeProvider clock,
    ILogger<LiveboxPlugin> logger,
    TimeSpan grace,
    TimeSpan? periode = null,
    bool supervision = true) : ICorePlugin
{
    public const string BoxDeviceId = "livebox";
    public const int EchecsAvantIndisponible = 3;
    private static readonly TimeSpan PeriodeWan = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _periode = periode ?? TimeSpan.FromSeconds(60);
    private readonly IReadOnlyList<TrackedDevice> _suivis = [.. suivis.Select(s => s with { Mac = LiveboxParser.Normaliser(s.Mac) })];
    private readonly Lock _verrou = new();
    private readonly Dictionary<string, DateTimeOffset> _vusLe = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
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

        var maintenant = clock.GetUtcNow();
        var etats = new List<EntityState>();
        lock (_verrou)
        {
            _echecs = 0;
            foreach (var a in appareils.Where(a => a.Active)) _vusLe[a.Mac] = maintenant;

            foreach (var s in _suivis)
            {
                var present = _vusLe.TryGetValue(s.Mac, out var vu) && maintenant - vu < grace;
                etats.Add(Noter(PresenceId(s.Mac), present));
            }

            if (wan is not null)
            {
                if (wan.Up is { } up) etats.Add(Noter("internet", up));
                if (wan.PublicIp is { } ip) etats.Add(Noter("ip", ip));
                if (wan.DownKbps is { } d) etats.Add(Noter("descendant", d));
                if (wan.UpKbps is { } m) etats.Add(Noter("montant", m));
            }
        }

        JournaliserListe(appareils);
        // La disponibilité se tient même hors session : la resynchronisation suivante la dira telle qu'elle est.
        var retour = !_disponible;
        _disponible = true;
        if (Coeur is not { } coeur) return;
        await coeur.SendAsync(new StateBatch { States = etats }, ct);
        if (retour) await coeur.SendAsync(new Availability { Online = true }, ct);
    }

    private EntityState Noter(string id, object valeur)
    {
        var json = JsonSerializer.SerializeToElement(valeur);
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
        BoxMuette(logger, echecs);
        if (Coeur is { } coeur) await coeur.SendAsync(new Availability { Online = false }, ct);
    }

    /// <summary>La liste des appareils vus, dans le journal, seulement quand elle change.</summary>
    private void JournaliserListe(IReadOnlyList<LiveboxHost> appareils)
    {
        var suivis = _suivis.Select(s => s.Mac).ToHashSet(StringComparer.Ordinal);
        var liste = string.Join(", ", appareils.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => $"{a.Name} ({a.Mac}{(a.Active ? "" : ", déconnecté")}{(suivis.Contains(a.Mac) ? ", suivi" : "")})"));
        if (liste == _derniereListe) return;
        _derniereListe = liste;
        AppareilsVus(logger, appareils.Count, liste);
    }

    public IReadOnlyList<Message> Snapshot()
    {
        // Deux parts indépendantes : la supervision de la connexion (classes standard, les mêmes pour toute box) et la
        // présence des appareils suivis (classe « presence », consommée par le cœur quelle que soit sa source).
        var appareils = new List<DeviceDescriptor>();
        if (supervision)
        {
            appareils.Add(new DeviceDescriptor
            {
                UniqueId = BoxDeviceId, Name = "Livebox", Manufacturer = "Orange",
                Entities =
                [
                    new EntityDescriptor { UniqueId = "internet", Kind = EntityKind.BinarySensor, Name = "Internet", DeviceClass = DeviceClasses.Connectivity },
                    new EntityDescriptor { UniqueId = "ip", Kind = EntityKind.Sensor, Name = "IP publique", DeviceClass = DeviceClasses.PublicIp },
                    new EntityDescriptor { UniqueId = "descendant", Kind = EntityKind.Sensor, Name = "Débit descendant", DeviceClass = DeviceClasses.DataRate, Unit = Units.KilobitPerSecond, StateClass = StateClass.Measurement },
                    new EntityDescriptor { UniqueId = "montant", Kind = EntityKind.Sensor, Name = "Débit montant", DeviceClass = DeviceClasses.DataRate, Unit = Units.KilobitPerSecond, StateClass = StateClass.Measurement },
                ],
            });
        }

        appareils.AddRange(_suivis.Select(s => new DeviceDescriptor
        {
            UniqueId = PresenceId(s.Mac), Name = s.Name is { Length: > 0 } n ? n : s.Mac,
            Entities = [new EntityDescriptor { UniqueId = PresenceId(s.Mac), Kind = EntityKind.BinarySensor, Name = "Présence", DeviceClass = DeviceClasses.Presence }],
        }));

        var messages = new List<Message> { new Declare { Devices = appareils } };
        lock (_verrou)
        {
            if (_valeurs.Count > 0)
                messages.Add(new StateBatch { States = [.. _valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value })] });
        }

        messages.Add(new Availability { Online = _disponible });
        return messages;
    }

    public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct) =>
        Task.FromResult(new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = "la Livebox est en lecture seule" });

    [LoggerMessage(Level = LogLevel.Information, Message = "La box voit {Nombre} appareil(s) : {Liste}")]
    private static partial void AppareilsVus(ILogger logger, int nombre, string liste);

    [LoggerMessage(Level = LogLevel.Error, Message = "Livebox : {Motif}.")]
    private static partial void AuthentificationRefusee(ILogger logger, string motif);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Livebox injoignable (relevé raté n° {Echecs}) : {Motif}")]
    private static partial void ReleveRate(ILogger logger, int echecs, string motif);

    [LoggerMessage(Level = LogLevel.Error, Message = "Livebox muette depuis {Echecs} relevés : présences indisponibles (le cœur ne conclura pas à une maison vide).")]
    private static partial void BoxMuette(ILogger logger, int echecs);
}
