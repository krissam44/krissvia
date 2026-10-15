using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Krissvia.Contracts;
using Krissvia.Core.Events;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost;

/// <summary>
/// Point d'entrée des plugins : ouvre chaque session (hello → welcome ou rejected) et tient la liste des
/// sessions vivantes, une par plugin.
/// </summary>
/// <remarks>
/// Un nouveau <c>hello</c> d'un plugin déjà connecté REMPLACE l'ancienne session au lieu d'être refusé : un
/// plugin qui redémarre après un plantage revient avant que le cœur ait constaté la mort de l'ancienne
/// connexion (jusqu'à 45 s), et le refuser laisserait ses entités sans mesures pendant tout ce temps. Le
/// jeton empêche un tiers d'en profiter.
/// </remarks>
public sealed partial class PluginHub(
    IEntityRegistry registry,
    IEntityStateWriter states,
    PluginHostOptions options,
    TimeProvider clock,
    ILogger<PluginHub> logger,
    IEventPublisher? events = null)
{
    private readonly ConcurrentDictionary<string, PluginSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Plugins connectés en ce moment.</summary>
    public IReadOnlyCollection<string> ConnectedPlugins => _sessions.Keys.ToList();

    /// <summary>
    /// Fait vivre une session sur <paramref name="channel"/> jusqu'à sa fin (fermeture, silence, remplacement,
    /// arrêt du cœur). Ne lève pas d'exception pour une faute du plugin : elle est journalisée.
    /// </summary>
    public async Task RunAsync(IMessageChannel channel, CancellationToken ct)
    {
        var hello = await AttendreHelloAsync(channel, ct);
        if (hello is null) return;

        var session = new PluginSession(hello.PluginId, channel, registry, states, options, clock, logger);
        PluginSession? remplacee = null;
        _sessions.AddOrUpdate(hello.PluginId, session, (_, ancienne) => { remplacee = ancienne; return session; });

        if (remplacee is not null)
        {
            Log.SessionRemplacee(logger, hello.PluginId);
            remplacee.Remplacer();
        }

        Log.SessionOuverte(logger, hello.PluginId, hello.PluginVersion, hello.ContractVersion);
        events?.Publish(new PluginConnectionChanged(hello.PluginId, Connected: true));

        try
        {
            await session.RunAsync(ct);
        }
        finally
        {
            // Seule la session encore inscrite rend ses entités indisponibles : une session remplacée ne doit pas
            // éteindre celles que sa remplaçante vient d'allumer.
            var derniere = _sessions.TryRemove(new KeyValuePair<string, PluginSession>(hello.PluginId, session));
            await session.TerminerAsync(rendreIndisponible: derniere);
            Log.SessionFermee(logger, hello.PluginId);
            // Une session remplacée n'annonce pas de déconnexion : le plugin, lui, est toujours là.
            if (derniere) events?.Publish(new PluginConnectionChanged(hello.PluginId, Connected: false));
        }
    }

    internal bool TryGetSession(string pluginId, out PluginSession session) =>
        _sessions.TryGetValue(pluginId, out session!);

    // ── Ouverture ────────────────────────────────────────────────────────────

    private async Task<Hello?> AttendreHelloAsync(IMessageChannel channel, CancellationToken ct)
    {
        string? trame;
        try
        {
            trame = await channel.ReceiveAsync(ct).AsTask().WaitAsync(options.HelloTimeout, clock, ct);
        }
        catch (TimeoutException)
        {
            await RefuserAsync(channel, RejectReason.ProtocolError, $"pas de hello en {options.HelloTimeout.TotalSeconds:0} s", ct);
            return null;
        }

        if (trame is null) return null;

        if (ContractJson.Decode(trame).Message is not Hello hello)
        {
            await RefuserAsync(channel, RejectReason.ProtocolError, "la première trame doit être un hello valide", ct);
            return null;
        }

        if (!IdentifiantValide().IsMatch(hello.PluginId))
        {
            await RefuserAsync(channel, RejectReason.ProtocolError, "plugin_id : minuscules, chiffres, « _ » et « - », 64 caractères au plus", ct);
            return null;
        }

        if (!ContractInfo.IsCompatible(hello.ContractVersion))
        {
            await RefuserAsync(channel, RejectReason.IncompatibleContract,
                $"le cœur parle le contrat {ContractInfo.Version}, le plugin {hello.ContractVersion}", ct, hello.PluginId);
            return null;
        }

        if (options.Tokens.TryGetValue(hello.PluginId, out var attendu))
        {
            if (!JetonsEgaux(attendu, hello.Token))
            {
                await RefuserAsync(channel, RejectReason.InvalidToken, "jeton absent ou faux", ct, hello.PluginId);
                return null;
            }
        }
        else if (!options.AllowUnregisteredPlugins)
        {
            await RefuserAsync(channel, RejectReason.UnknownPlugin, "plugin non déclaré dans la configuration du cœur", ct, hello.PluginId);
            return null;
        }

        try
        {
            await channel.SendAsync(ContractJson.Encode(new Welcome
            {
                ContractVersion = ContractInfo.Version,
                HeartbeatSeconds = (int)options.Heartbeat.TotalSeconds,
                TimeoutSeconds = (int)options.Timeout.TotalSeconds,
            }), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.EnvoiImpossible(logger, hello.PluginId, ex);
            return null;
        }

        return hello;
    }

    private async Task RefuserAsync(IMessageChannel channel, RejectReason motif, string detail, CancellationToken ct, string? pluginId = null)
    {
        Log.SessionRefusee(logger, pluginId ?? "?", motif, detail);
        try
        {
            await channel.SendAsync(ContractJson.Encode(new Rejected { Reason = motif, Detail = detail }), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Le plugin ne lira peut-être jamais le motif ; il est dans le journal du cœur.
        }

        await channel.CloseAsync(detail, ct);
    }

    /// <summary>Comparaison en temps constant : la durée ne doit rien dire du nombre de caractères justes.</summary>
    private static bool JetonsEgaux(string attendu, string? recu) =>
        recu is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(attendu), Encoding.UTF8.GetBytes(recu));

    /// <summary>Le plugin_id devient l'integration_id du registre : il reste court et sans surprise.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdentifiantValide();
}
