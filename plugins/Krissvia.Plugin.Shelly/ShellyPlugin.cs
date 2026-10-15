using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.Shelly;

/// <summary>Un appareil à suivre, tel que configuré : son adresse et le nom proposé au registre.</summary>
public sealed record ShellyDeviceConfig
{
    public string Host { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>
/// Interroge les Shelly en HTTP, publie leurs mesures et l'état de leurs relais, exécute les ordres.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Interrogation, pas MQTT : un Shelly de génération 1 ne publie ses mesures en MQTT que toutes les 30 s
/// environ, trop lentement pour le délestage (mesure fraîche de moins de 10 s).</item>
/// <item>Les appareils sont interrogés en parallèle : un appareil injoignable (3 s d'attente) ne retarde pas les
/// autres.</item>
/// <item>Après un ordre, l'état est relu et publié AVANT de répondre au cœur : c'est l'état publié après l'ordre
/// qui confirme une coupure (envoi vérifié, ARCHITECTURE.md §7.3).</item>
/// <item>Trois lectures ratées de suite : l'appareil passe hors ligne ; il revient à la première réponse.</item>
/// <item>Un appareil injoignable au démarrage n'est déclaré qu'à sa première réponse.</item>
/// </list>
/// </remarks>
internal sealed partial class ShellyPlugin(
    IReadOnlyList<ShellyDeviceConfig> appareils,
    IShellyHttp http,
    TimeProvider clock,
    ILogger<ShellyPlugin> logger,
    TimeSpan? periode = null) : ICorePlugin
{
    public const int EchecsAvantHorsLigne = 3;

    private readonly TimeSpan _periode = periode ?? TimeSpan.FromSeconds(2);
    private readonly List<Suivi> _suivis = appareils.Select(a => new Suivi(a)).ToList();

    private sealed class Suivi(ShellyDeviceConfig config)
    {
        public ShellyDeviceConfig Config { get; } = config;
        public Lock Verrou { get; } = new();
        public ShellyIdentity? Identite;
        public int Echecs;
        public bool HorsLigne;
        public Dictionary<string, JsonElement> Valeurs { get; } = new(StringComparer.Ordinal);
    }

    public CoreClient? Coeur { get; set; }

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

    /// <summary>Un cycle pour un appareil : identification si besoin, puis lecture de l'état.</summary>
    private async Task CycleAsync(Suivi suivi, CancellationToken ct)
    {
        if (suivi.Identite is null && !await IdentifierAsync(suivi, ct)) return;
        await LireAsync(suivi, ct);
    }

    /// <summary>Un cycle complet de tous les appareils (les tests le déclenchent sans attendre la minuterie).</summary>
    internal Task CycleAsync(CancellationToken ct) => Task.WhenAll(_suivis.Select(s => CycleAsync(s, ct)));

    private async Task<bool> IdentifierAsync(Suivi suivi, CancellationToken ct)
    {
        if (await http.GetAsync(suivi.Config.Host, "/shelly", ct) is not { } shelly) return false;

        var gen2 = shelly.TryGetProperty("gen", out var g) && g.TryGetInt32(out var n) && n >= 2;
        var statut2 = gen2 ? await http.GetAsync(suivi.Config.Host, "/rpc/Shelly.GetStatus", ct) : null;
        if (ShellyTranslator.Identify(shelly, statut2) is not { } identite) return false;

        lock (suivi.Verrou) suivi.Identite = identite;
        Identifie(logger, suivi.Config.Host, identite.Model, identite.Generation, identite.Mac);
        await EnvoyerAsync([Declaration()]);
        return true;
    }

    private async Task LireAsync(Suivi suivi, CancellationToken ct)
    {
        var identite = suivi.Identite!;
        var statut = await http.GetAsync(suivi.Config.Host, ShellyTranslator.StatusPath(identite), ct);
        var messages = new List<Message>();

        lock (suivi.Verrou)
        {
            if (statut is null)
            {
                if (++suivi.Echecs == EchecsAvantHorsLigne)
                {
                    suivi.HorsLigne = true;
                    messages.Add(new Availability { DeviceUniqueId = identite.Mac, Online = false });
                }
            }
            else
            {
                suivi.Echecs = 0;
                if (suivi.HorsLigne)
                {
                    suivi.HorsLigne = false;
                    messages.Add(new Availability { DeviceUniqueId = identite.Mac, Online = true });
                }

                var etats = ShellyTranslator.ReadStatus(identite, statut.Value);
                foreach (var e in etats) suivi.Valeurs[e.EntityUniqueId] = e.Value;
                if (etats.Count > 0) messages.Add(new StateBatch { States = etats });
            }
        }

        if (statut is null && messages.Count > 0) HorsLigneSignale(logger, suivi.Config.Host);
        await EnvoyerAsync(messages);
    }

    private Declare Declaration() => new()
    {
        Devices = _suivis
            .Where(s => s.Identite is not null)
            .Select(s => ShellyTranslator.Describe(s.Identite!, s.Config.Name is { Length: > 0 } nom ? nom : s.Identite!.Model))
            .ToList(),
    };

    public IReadOnlyList<Message> Snapshot()
    {
        var messages = new List<Message> { Declaration() };
        foreach (var s in _suivis.Where(s => s.Identite is not null))
        {
            lock (s.Verrou)
            {
                if (s.Valeurs.Count > 0)
                    messages.Add(new StateBatch
                    {
                        States = s.Valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value }).ToList(),
                    });
                if (s.HorsLigne) messages.Add(new Availability { DeviceUniqueId = s.Identite!.Mac, Online = false });
            }
        }

        messages.Add(new Availability { Online = true });
        return messages;
    }

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        var suivi = _suivis.FirstOrDefault(s => s.Identite is { } id && request.EntityUniqueId.StartsWith(id.Mac + ":", StringComparison.Ordinal));
        if (suivi?.Identite is not { } identite || ShellyTranslator.CommandPath(identite, request.EntityUniqueId, request.Command) is not { } chemin)
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = "entité ou commande inconnue de ce plugin" };

        if (await http.GetAsync(suivi.Config.Host, chemin, ct) is null)
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Unreachable, Detail = $"Shelly {suivi.Config.Host} injoignable" };

        OrdreExecute(logger, suivi.Config.Host, chemin);
        await LireAsync(suivi, ct);   // l'état résultant part avant la réponse : il confirme l'ordre
        return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Accepted };
    }

    /// <summary>Capte les messages à la place du cœur (tests).</summary>
    internal Func<Message, Task>? Sortie { get; set; }

    private async Task EnvoyerAsync(IReadOnlyList<Message> messages)
    {
        foreach (var m in messages)
        {
            if (Sortie is { } sortie) await sortie(m);
            else if (Coeur is { } coeur) await coeur.SendAsync(m);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelly {Host} : {Model}, génération {Generation}, MAC {Mac}.")]
    private static partial void Identifie(ILogger logger, string host, string model, int generation, string mac);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shelly {Host} injoignable : hors ligne.")]
    private static partial void HorsLigneSignale(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelly {Host} : {Chemin}.")]
    private static partial void OrdreExecute(ILogger logger, string host, string chemin);
}
