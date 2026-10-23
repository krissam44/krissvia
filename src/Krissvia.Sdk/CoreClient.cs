using System.Net.WebSockets;
using Krissvia.Contracts;
using Microsoft.Extensions.Logging;

namespace Krissvia.Sdk;

/// <summary>Ce qu'un plugin fournit au <see cref="CoreClient"/>.</summary>
public interface ICorePlugin
{
    /// <summary>
    /// Tout ce que le cœur doit savoir à l'ouverture d'une session : <c>declare</c>, puis TOUS les états connus,
    /// puis les joignabilités. Le cœur a pu redémarrer : rien d'autre ne le resynchronise (contrat 0.2 §1).
    /// </summary>
    IReadOnlyList<Message> Snapshot();

    /// <summary>Exécute un ordre. Une exception devient un <see cref="CommandOutcome.Failed"/>.</summary>
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct);
}

public sealed class CoreClientOptions
{
    /// <summary>Adresse du point d'entrée des plugins, par exemple <c>ws://192.168.1.10:5000/plugins</c>.</summary>
    public Uri? Url { get; set; }

    public string PluginId { get; set; } = "";
    public string PluginVersion { get; set; } = "0.0.0";
    public string? Token { get; set; }

    /// <summary>Attentes successives avant de retenter une connexion ; la dernière se répète.</summary>
    public TimeSpan[] ReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// Attente après un refus (jeton faux, version incompatible). Un refus ne se corrige pas tout seul, mais la
    /// configuration du cœur peut changer : on réessaie, sans marteler.
    /// </summary>
    public TimeSpan RejectedDelay { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan WelcomeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// PID du cœur qui a lancé le plugin (<c>Krissvia__ParentPid</c>, posé par la page Services) : le plugin
    /// s'arrête quand ce processus disparaît (<see cref="ParentProcessWatch"/>). Absent pour un plugin lancé à
    /// la main, qui se comporte comme avant.
    /// </summary>
    public int? ParentPid { get; set; }

    /// <summary>
    /// Dossier où le plugin garde son propre état (<c>Krissvia__DataDirectory</c>, posé par la page Services). Absent
    /// pour un plugin lancé à la main : à lui de choisir (le dossier de l'exécutable, par exemple).
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Période de vérification du processus parent.</summary>
    public TimeSpan ParentCheckPeriod { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Connexion d'un plugin C# au cœur : handshake, resynchronisation, battement de cœur, ordres, et reconnexion
/// sans fin. Un plugin d'un autre langage refait la même chose ; docs/TRANSPORT.md en est la spécification.
/// </summary>
public sealed partial class CoreClient(
    CoreClientOptions options,
    ICorePlugin plugin,
    TimeProvider clock,
    ILogger<CoreClient> logger,
    Func<CancellationToken, Task<WebSocket>>? connect = null)
{
    private readonly SemaphoreSlim _envoi = new(1, 1);

    // Contrat 0.3 : les entités suivies (renvoyées à chaque session) et les ordres en attente de leur résultat.
    private volatile IReadOnlyList<string> _abonnements = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LinkState> _liens = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<EntityCommandResponse>> _ordres = new(StringComparer.Ordinal);

    /// <summary>Délai d'attente du résultat d'un <see cref="CommandEntityAsync"/> : la vérification du cœur peut prendre 10 s.</summary>
    public static readonly TimeSpan EntityCommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// L'état d'une entité suivie (<see cref="SetSubscriptionsAsync"/>) : une fois à l'abonnement, puis à chaque
    /// changement. Appelé sur la boucle de réception : rendre la main vite.
    /// </summary>
    public event Action<EntityStateUpdate>? EntityStateReceived;
    private WebSocket? _session;
    private long _derniereReceptionTicks;

    private enum FinDeSession { Echec, Refus, Normale }

    /// <summary>Vrai entre le welcome (resynchronisation envoyée) et la fin de la session.</summary>
    public bool IsConnected => Volatile.Read(ref _session) is { State: WebSocketState.Open };

    /// <summary>
    /// Envoie un message si une session est ouverte. Sinon il est abandonné, et c'est voulu : la
    /// resynchronisation de la prochaine session portera l'état à jour.
    /// </summary>
    public async Task<bool> SendAsync(Message message, CancellationToken ct = default)
    {
        await _envoi.WaitAsync(ct);
        try
        {
            if (_session is not { State: WebSocketState.Open } ws) return false;
            await TextFrames.SendAsync(ws, ContractJson.Encode(message), ct);
            return true;
        }
        catch (WebSocketException)
        {
            return false;   // la réception constatera la coupure et reconnectera
        }
        finally
        {
            _envoi.Release();
        }
    }

    /// <summary>Se connecte, et se reconnecte, jusqu'à l'annulation.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var tentative = 0;
        while (!ct.IsCancellationRequested)
        {
            var fin = FinDeSession.Echec;
            WebSocket? ws = null;
            try
            {
                ws = await (connect ?? ConnecterAsync)(ct);
                fin = await SessionAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnexionImpossible(logger, options.Url, ex.Message);
            }
            finally
            {
                ws?.Dispose();
            }

            tentative = fin == FinDeSession.Normale ? 0 : tentative + 1;
            var attente = fin == FinDeSession.Refus
                ? options.RejectedDelay
                : options.ReconnectDelays[Math.Min(Math.Max(tentative - 1, 0), options.ReconnectDelays.Length - 1)];

            try
            {
                await Task.Delay(attente, clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<WebSocket> ConnecterAsync(CancellationToken ct)
    {
        var url = options.Url ?? throw new InvalidOperationException("Adresse du cœur non configurée (Krissvia:Url).");
        var client = new ClientWebSocket();
        try
        {
            await client.ConnectAsync(url, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<FinDeSession> SessionAsync(WebSocket ws, CancellationToken ct)
    {
        await TextFrames.SendAsync(ws, ContractJson.Encode(new Hello
        {
            PluginId = options.PluginId,
            PluginVersion = options.PluginVersion,
            ContractVersion = ContractInfo.Version,
            Token = options.Token,
        }), ct);

        string? premiere;
        try
        {
            premiere = await TextFrames.ReceiveAsync(ws, ct).WaitAsync(options.WelcomeTimeout, clock, ct);
        }
        catch (TimeoutException)
        {
            PasDeReponse(logger);
            return FinDeSession.Echec;
        }

        switch (premiere is null ? null : ContractJson.Decode(premiere).Message)
        {
            case Rejected refus:
                SessionRefusee(logger, refus.Reason, refus.Detail);
                return FinDeSession.Refus;
            case Welcome accueil:
                await OuvrirAsync(ws, ct);
                SessionOuverte(logger, accueil.ContractVersion);
                try
                {
                    await RecevoirAsync(ws, accueil, ct);
                }
                finally
                {
                    await FermerAsync();
                    SessionFermee(logger);
                }
                return FinDeSession.Normale;
            default:
                ReponseInattendue(logger);
                return FinDeSession.Echec;
        }
    }

    /// <summary>
    /// La resynchronisation part sous le verrou d'envoi : un message en direct émis pendant ce temps attend, et
    /// arrive donc APRÈS l'instantané au lieu d'être écrasé par lui.
    /// </summary>
    private async Task OuvrirAsync(WebSocket ws, CancellationToken ct)
    {
        await _envoi.WaitAsync(ct);
        try
        {
            foreach (var message in plugin.Snapshot())
                await TextFrames.SendAsync(ws, ContractJson.Encode(message), ct);
            // Un abonnement ne survit pas à la session : il repart après la resynchronisation.
            if (_abonnements.Count > 0)
                await TextFrames.SendAsync(ws, ContractJson.Encode(new Subscribe { EntityIds = _abonnements }), ct);
            // L'état des liens aussi : le cœur l'oublie à chaque fin de session, et le broker a pu se connecter avant le cœur.
            foreach (var lien in _liens.Values)
                await TextFrames.SendAsync(ws, ContractJson.Encode(lien), ct);
            Volatile.Write(ref _session, ws);
        }
        finally
        {
            _envoi.Release();
        }
    }

    private async Task FermerAsync()
    {
        await _envoi.WaitAsync();
        try
        {
            Volatile.Write(ref _session, null);
        }
        finally
        {
            _envoi.Release();
        }

        // Un ordre dont la session s'est fermée n'aura jamais de résultat : on ne fait pas semblant.
        foreach (var attente in _ordres.Values)
            attente.TrySetResult(new EntityCommandResponse { Id = "", Outcome = CommandOutcome.Unreachable, Detail = "session avec le cœur fermée" });
    }

    // ── Contrat 0.3 : lire et commander d'autres entités ────────────────────

    /// <summary>
    /// Les entités à suivre (la liste COMPLÈTE) : seules celles choisies dans les réglages du plugin sont accordées
    /// (docs/CONTRACT-V0.3.md). Retenue et renvoyée à chaque (re)connexion ; envoyée tout de suite si une session est ouverte.
    /// </summary>
    /// <summary>
    /// Contrat 0.3 : l'état d'un lien du plugin (<c>mqtt</c>) — ce qui colore la pastille MQTT du cœur. Retenu et renvoyé
    /// à chaque session. Un même état sans motif garde le motif précédent : une connexion refusée puis la déconnexion qui
    /// suit ne doivent pas effacer « identifiants refusés » au profit d'un « coupé » sans explication.
    /// </summary>
    public Task<bool> ReportLinkAsync(string link, bool connected, string? detail = null, CancellationToken ct = default)
    {
        var etat = _liens.AddOrUpdate(link,
            _ => new LinkState { Link = link, Connected = connected, Detail = detail },
            (_, avant) => new LinkState
            {
                Link = link, Connected = connected,
                Detail = detail ?? (avant.Connected == connected ? avant.Detail : null),
            });
        return SendAsync(etat, ct);
    }

    public async Task SetSubscriptionsAsync(IEnumerable<string> entityIds, CancellationToken ct = default)
    {
        _abonnements = [.. entityIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal)];
        await SendAsync(new Subscribe { EntityIds = _abonnements }, ct);
    }

    /// <summary>
    /// Un ordre sur une entité (pas forcément la sienne), par le bus de commandes du cœur. Hors session, ou sans réponse
    /// dans <see cref="EntityCommandTimeout"/> : <see cref="CommandOutcome.Unreachable"/>, jamais un faux succès.
    /// </summary>
    public async Task<EntityCommandResponse> CommandEntityAsync(string entityId, string command, System.Text.Json.JsonElement? args = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var attente = new TaskCompletionSource<EntityCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ordres[id] = attente;
        try
        {
            if (!await SendAsync(new EntityCommandRequest { Id = id, EntityId = entityId, Command = command, Args = args }, ct))
                return new EntityCommandResponse { Id = id, Outcome = CommandOutcome.Unreachable, Detail = "pas de session avec le cœur" };
            return await attente.Task.WaitAsync(EntityCommandTimeout, clock, ct);
        }
        catch (TimeoutException)
        {
            return new EntityCommandResponse { Id = id, Outcome = CommandOutcome.Unreachable, Detail = "pas de réponse du cœur" };
        }
        finally
        {
            _ordres.TryRemove(id, out _);
        }
    }

    private async Task RecevoirAsync(WebSocket ws, Welcome accueil, CancellationToken ct)
    {
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref _derniereReceptionTicks, clock.GetUtcNow().UtcTicks);
        var battement = BattementAsync(ws, TimeSpan.FromSeconds(accueil.HeartbeatSeconds), TimeSpan.FromSeconds(accueil.TimeoutSeconds), fin.Token);

        try
        {
            while (await TextFrames.ReceiveAsync(ws, ct) is { } trame)
            {
                Interlocked.Exchange(ref _derniereReceptionTicks, clock.GetUtcNow().UtcTicks);

                switch (ContractJson.Decode(trame).Message)
                {
                    case CommandRequest ordre:
                        // Hors de la boucle de réception : un ordre lent ne doit pas retarder les suivants.
                        _ = Task.Run(() => ExecuterAsync(ordre, ct), CancellationToken.None);
                        break;
                    case EntityStateUpdate etat:
                        EntityStateReceived?.Invoke(etat);
                        break;
                    case EntityCommandResponse reponse:
                        if (_ordres.TryGetValue(reponse.Id, out var attente)) attente.TrySetResult(reponse);
                        break;
                    case Subscribed abonnement:
                        foreach (var refus in abonnement.Denied) AbonnementRefuse(logger, refus.EntityId, refus.Reason);
                        break;
                    case Configure:
                        await SendAsync(new ConfigureResult { Ok = false, Error = "configuration locale du plugin uniquement" }, ct);
                        break;
                    case Shutdown arret:
                        ArretDuCoeur(logger, arret.Reason);
                        return;
                }
            }
        }
        catch (WebSocketException)
        {
            // Coupure : la boucle de RunAsync reconnecte.
        }
        finally
        {
            await fin.CancelAsync();
            await battement;
        }
    }

    private async Task ExecuterAsync(CommandRequest ordre, CancellationToken ct)
    {
        CommandResult resultat;
        try
        {
            resultat = await plugin.ExecuteAsync(ordre, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OrdreEnEchec(logger, ordre.Command, ordre.EntityUniqueId, ex);
            resultat = new CommandResult { Id = ordre.Id, Outcome = CommandOutcome.Failed, Detail = ex.Message };
        }

        await SendAsync(resultat with { Id = ordre.Id }, ct);
    }

    private async Task BattementAsync(WebSocket ws, TimeSpan periode, TimeSpan delaiMort, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(periode, clock, ct);

                var silence = clock.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _derniereReceptionTicks), TimeSpan.Zero);
                if (silence > delaiMort)
                {
                    CoeurMuet(logger, (int)silence.TotalSeconds);
                    ws.Abort();   // la réception échoue aussitôt, et RunAsync reconnecte
                    return;
                }

                await SendAsync(new Ping(), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abonnement à {EntityId} refusé ({Raison}) : la choisir dans les réglages du plugin (page Services).")]
    private static partial void AbonnementRefuse(ILogger logger, string entityId, SubscriptionDenial raison);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecté au cœur Krissvia (contrat {ContractVersion}).")]
    private static partial void SessionOuverte(ILogger logger, string contractVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session avec le cœur fermée ; reconnexion.")]
    private static partial void SessionFermee(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Le cœur refuse la connexion : {Reason}, {Detail}. Vérifier le jeton et la version du plugin.")]
    private static partial void SessionRefusee(ILogger logger, RejectReason reason, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cœur injoignable ({Url}) : {Error}.")]
    private static partial void ConnexionImpossible(ILogger logger, Uri? url, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Le cœur n'a pas répondu au hello.")]
    private static partial void PasDeReponse(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Réponse inattendue du cœur au hello.")]
    private static partial void ReponseInattendue(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Le cœur ferme la session ({Reason}).")]
    private static partial void ArretDuCoeur(ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cœur muet depuis {Secondes} s : reconnexion.")]
    private static partial void CoeurMuet(ILogger logger, int secondes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ordre {Command} sur « {UniqueId} » en échec.")]
    private static partial void OrdreEnEchec(ILogger logger, string command, string uniqueId, Exception ex);
}
