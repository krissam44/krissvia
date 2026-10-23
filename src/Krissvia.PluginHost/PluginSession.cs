using System.Collections.Concurrent;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost;

/// <summary>
/// Une session ouverte avec un plugin : réception des messages, battement de cœur, commandes en attente.
/// </summary>
/// <remarks>
/// Une faute du plugin (trame invalide, entité inconnue, déclaration refusée) est journalisée et la session
/// continue : un message raté ne doit pas couper les mesures de toutes les autres entités du plugin.
/// </remarks>
internal sealed class PluginSession(
    string pluginId,
    IMessageChannel channel,
    IEntityRegistry registry,
    IEntityStateWriter states,
    PluginHostOptions options,
    TimeProvider clock,
    ILogger logger,
    IPluginGateway? gateway = null)
{
    private readonly CancellationTokenSource _fin = new();

    /// <summary>
    /// Contrat 0.3 : l'état des liens du plugin (<c>mqtt</c>), le temps de la session — un plugin déconnecté ne répond
    /// plus de son broker.
    /// </summary>
    public ConcurrentDictionary<string, LinkState> Links { get; } = new(StringComparer.Ordinal);

    /// <summary>Un lien a changé d'état.</summary>
    public Action? LinksChanged { get; set; }
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandOutcome>> _enAttente = new(StringComparer.Ordinal);

    // Entités dont une valeur a été reçue pendant CETTE session : seules elles redeviennent disponibles sur
    // « online ». Une valeur restaurée au démarrage n'est pas une mesure.
    private readonly ConcurrentDictionary<string, byte> _mesurees = new(StringComparer.Ordinal);

    // Identifiants plugin déjà signalés comme inconnus : un capteur non déclaré qui publie toutes les 2 s ne
    // doit pas noyer le journal.
    private readonly ConcurrentDictionary<string, byte> _inconnusSignales = new(StringComparer.Ordinal);

    // Appareil → ses entités, d'après le dernier « declare ».
    private volatile IReadOnlyDictionary<string, string[]> _appareils = new Dictionary<string, string[]>();

    private long _derniereReceptionTicks;
    private volatile bool _terminee;

    // Contrat 0.3 : les entités suivies par le plugin, et l'écoute des changements (ouverte au premier abonnement).
    private volatile HashSet<string> _abonnements = new(StringComparer.Ordinal);
    private IDisposable? _ecoute;
    private readonly Lock _verrouEcoute = new();

    public string PluginId => pluginId;

    /// <summary>Version annoncée au hello.</summary>
    public string? Version { get; init; }

    public async Task RunAsync(CancellationToken ct)
    {
        using var lie = CancellationTokenSource.CreateLinkedTokenSource(ct, _fin.Token);
        Interlocked.Exchange(ref _derniereReceptionTicks, clock.GetUtcNow().UtcTicks);

        var battement = BattementAsync(lie.Token);
        try
        {
            await RecevoirAsync(lie.Token);
        }
        catch (OperationCanceledException) when (lie.IsCancellationRequested)
        {
            // Fin demandée : arrêt du cœur, silence du plugin ou remplacement.
        }
        finally
        {
            await lie.CancelAsync();
            await battement;
        }
    }

    /// <summary>Un nouveau hello du même plugin a pris la place de cette session.</summary>
    public void Remplacer() => _fin.Cancel();

    /// <summary>
    /// Fin de session : les commandes en attente répondent <see cref="CommandOutcome.Unreachable"/>, le canal est
    /// fermé et, si demandé, toutes les entités du plugin deviennent indisponibles.
    /// </summary>
    public async Task TerminerAsync(bool rendreIndisponible)
    {
        _terminee = true;
        foreach (var attente in _enAttente.Values) attente.TrySetResult(CommandOutcome.Unreachable);
        lock (_verrouEcoute) _ecoute?.Dispose();

        using (var delai = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            await channel.CloseAsync("fin de session", delai.Token);

        if (!rendreIndisponible) return;

        foreach (var entite in await EntitesDuPluginAsync(CancellationToken.None))
            states.SetAvailability(entite.EntityId, false);
    }

    // ── Commandes ────────────────────────────────────────────────────────────

    /// <summary>
    /// Envoie un ordre et attend son <c>command_result</c>. Sans réponse dans le délai, l'issue est
    /// <see cref="CommandOutcome.Unreachable"/> : le cœur ne suppose jamais qu'un ordre est passé.
    /// </summary>
    public async Task<CommandOutcome> EnvoyerCommandeAsync(
        string entityUniqueId, string command, System.Text.Json.JsonElement? args, CommandPriority priority, int? ttlSeconds, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var attente = new TaskCompletionSource<CommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _enAttente[id] = attente;
        try
        {
            // Inscrite AVANT le test : une fin de session concurrente la verra forcément et y répondra.
            if (_terminee) return CommandOutcome.Unreachable;

            await channel.SendAsync(ContractJson.Encode(new CommandRequest
            {
                Id = id,
                EntityUniqueId = entityUniqueId,
                Command = command,
                Args = args,
                Priority = priority,
                TtlSeconds = ttlSeconds,
            }), ct);

            var delai = ttlSeconds is > 0 ? TimeSpan.FromSeconds(ttlSeconds.Value) : options.CommandTimeout;
            return await attente.Task.WaitAsync(delai, clock, ct);
        }
        catch (TimeoutException)
        {
            Log.CommandeSansReponse(logger, pluginId, command, entityUniqueId);
            return CommandOutcome.Unreachable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.EnvoiImpossible(logger, pluginId, ex);
            return CommandOutcome.Unreachable;
        }
        finally
        {
            _enAttente.TryRemove(id, out _);
        }
    }

    // ── Réception ────────────────────────────────────────────────────────────

    private async Task RecevoirAsync(CancellationToken ct)
    {
        while (await channel.ReceiveAsync(ct) is { } trame)
        {
            Interlocked.Exchange(ref _derniereReceptionTicks, clock.GetUtcNow().UtcTicks);

            var lu = ContractJson.Decode(trame);
            if (lu.UnknownType is { } type)
            {
                Log.TypeInconnu(logger, pluginId, type);
                continue;
            }

            if (lu.Message is not { } message)
            {
                Log.TrameInvalide(logger, pluginId, lu.Error);
                continue;
            }

            if (message is Shutdown fin)
            {
                Log.ArretAnnonce(logger, pluginId, fin.Reason);
                return;
            }

            try
            {
                await TraiterAsync(message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.MessageEnEchec(logger, pluginId, message.GetType().Name, ex);
            }
        }
    }

    private async Task TraiterAsync(Message message, CancellationToken ct)
    {
        switch (message)
        {
            case Declare declaration:
                await DeclarerAsync(declaration, ct);
                break;
            case StateBatch lot:
                await EnregistrerEtatsAsync(lot, ct);
                break;
            case Availability joignabilite:
                await AppliquerJoignabiliteAsync(joignabilite, ct);
                break;
            case LinkState lien when !string.IsNullOrWhiteSpace(lien.Link):
                Links[lien.Link] = lien;
                LinksChanged?.Invoke();
                break;
            case CommandResult resultat:
                if (_enAttente.TryGetValue(resultat.Id, out var attente)) attente.TrySetResult(resultat.Outcome);
                else Log.ResultatOrphelin(logger, pluginId, resultat.Id);
                break;
            case ConfigureResult configuration:
                if (configuration.Ok) Log.ConfigurationAcceptee(logger, pluginId);
                else Log.ConfigurationRefusee(logger, pluginId, configuration.Error);
                break;
            case Discovered decouverte:
                Log.Decouverte(logger, pluginId, decouverte.Candidates.Count);   // auto-import : pas encore branché
                break;
            case Subscribe abonnement:
                await AbonnerAsync(abonnement, ct);
                break;
            case EntityCommandRequest ordre:
                // Hors de la boucle de réception : un ordre vérifié attend jusqu'à 10 s l'état qui le confirme.
                _ = Task.Run(() => CommanderAsync(ordre, ct), CancellationToken.None);
                break;
            case Ping:
                break;
            default:
                Log.MessageInattendu(logger, pluginId, message.GetType().Name);
                break;
        }
    }

    private async Task DeclarerAsync(Declare declaration, CancellationToken ct)
    {
        var resultat = await registry.RegisterAsync(pluginId, declaration.Devices, ct);

        var appareils = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var appareil in declaration.Devices)
            appareils[appareil.UniqueId] = appareil.Entities.Select(e => e.UniqueId).ToArray();
        _appareils = appareils;

        // Une entité que le plugin ne déclare plus ne recevra plus de mesure : sa dernière valeur n'est plus fiable.
        foreach (var entityId in resultat.NowMissing) states.SetAvailability(entityId, false);

        Log.Declaration(logger, pluginId, declaration.Devices.Count, resultat.Created.Count, resultat.NowMissing.Count);
    }

    private async Task EnregistrerEtatsAsync(StateBatch lot, CancellationToken ct)
    {
        foreach (var etat in lot.States)
        {
            var entite = await registry.FindByUniqueIdAsync(pluginId, etat.EntityUniqueId, ct);
            if (entite is null)
            {
                if (_inconnusSignales.TryAdd(etat.EntityUniqueId, 0)) Log.EntiteInconnue(logger, pluginId, etat.EntityUniqueId);
                continue;
            }

            if (etat.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                Log.ValeurAbsente(logger, pluginId, etat.EntityUniqueId);
                continue;
            }

            states.Update(entite.EntityId, etat.Value, etat.Timestamp);
            _mesurees[entite.EntityId] = 0;
        }
    }

    private async Task AppliquerJoignabiliteAsync(Availability joignabilite, CancellationToken ct)
    {
        IEnumerable<string> entityIds;

        if (joignabilite.EntityUniqueId is { } entityUniqueId)
        {
            var entite = await registry.FindByUniqueIdAsync(pluginId, entityUniqueId, ct);
            entityIds = entite is null ? [] : [entite.EntityId];
        }
        else if (joignabilite.DeviceUniqueId is { } deviceUniqueId)
        {
            if (!_appareils.TryGetValue(deviceUniqueId, out var uniqueIds))
            {
                Log.AppareilInconnu(logger, pluginId, deviceUniqueId);
                return;
            }

            var liste = new List<string>();
            foreach (var uniqueId in uniqueIds)
                if (await registry.FindByUniqueIdAsync(pluginId, uniqueId, ct) is { } e) liste.Add(e.EntityId);
            entityIds = liste;
        }
        else
        {
            // Le plugin lui-même (broker perdu, passerelle débranchée) : toutes ses entités.
            entityIds = (await EntitesDuPluginAsync(ct)).Select(e => e.EntityId);
        }

        foreach (var entityId in entityIds)
        {
            if (!joignabilite.Online) states.SetAvailability(entityId, false);
            else if (_mesurees.ContainsKey(entityId)) states.SetAvailability(entityId, true);
        }
    }

    // ── Contrat 0.3 : lire et commander d'autres entités ────────────────────

    /// <summary>
    /// Remplace la liste suivie, répond <c>subscribed</c>, puis envoie l'état de chaque entité accordée. Le droit se
    /// vérifie AVANT l'existence : un plugin n'apprend rien d'une entité qu'on ne lui a pas confiée.
    /// </summary>
    private async Task AbonnerAsync(Subscribe abonnement, CancellationToken ct)
    {
        var accordees = new List<EntityStateUpdate>();
        var refusees = new List<DeniedEntity>();
        foreach (var id in abonnement.EntityIds.Distinct(StringComparer.Ordinal))
        {
            if (gateway is null || gateway.AccessFor(pluginId, id) == Services.EntityAccess.None)
                refusees.Add(new DeniedEntity(id, SubscriptionDenial.NotAllowed));
            else if (await gateway.ReadAsync(id, ct) is { } etat)
                accordees.Add(etat);
            else
                refusees.Add(new DeniedEntity(id, SubscriptionDenial.Unknown));
        }

        _abonnements = accordees.Select(e => e.EntityId).ToHashSet(StringComparer.Ordinal);
        if (accordees.Count > 0 && gateway is not null)
            lock (_verrouEcoute) _ecoute ??= gateway.Watch($"plugin-{pluginId}-{Guid.NewGuid():N}", PousserAsync);

        foreach (var r in refusees) Log.AbonnementRefuse(logger, pluginId, r.EntityId, r.Reason);
        await channel.SendAsync(ContractJson.Encode(new Subscribed { Granted = [.. accordees.Select(e => e.EntityId)], Denied = refusees }), ct);
        foreach (var etat in accordees) await channel.SendAsync(ContractJson.Encode(etat), ct);
    }

    /// <summary>
    /// Un changement d'état : poussé si l'entité est suivie ET toujours autorisée (des réglages modifiés retirent le
    /// droit aussitôt, sans attendre un nouvel abonnement).
    /// </summary>
    private async Task PousserAsync(string entityId, CancellationToken ct)
    {
        if (_terminee || gateway is null || !_abonnements.Contains(entityId)) return;
        if (gateway.AccessFor(pluginId, entityId) == Services.EntityAccess.None) return;
        if (await gateway.ReadAsync(entityId, ct) is not { } etat) return;
        try
        {
            await channel.SendAsync(ContractJson.Encode(etat), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Canal fermé entre-temps : la fin de session s'en occupe.
        }
    }

    private async Task CommanderAsync(EntityCommandRequest ordre, CancellationToken ct)
    {
        EntityCommandResponse reponse;
        if (gateway is null || gateway.AccessFor(pluginId, ordre.EntityId) != Services.EntityAccess.Command)
        {
            Log.OrdreRefuse(logger, pluginId, ordre.Command, ordre.EntityId);
            reponse = new EntityCommandResponse
            {
                Id = ordre.Id, Outcome = CommandOutcome.Failed, Verification = "not_sent",
                Detail = "entité non autorisée pour ce plugin (à choisir dans ses réglages, page Services)",
            };
        }
        else
        {
            try
            {
                reponse = await gateway.CommandAsync(pluginId, ordre, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reponse = new EntityCommandResponse { Id = ordre.Id, Outcome = CommandOutcome.Failed, Detail = ex.Message };
            }
        }

        try
        {
            await channel.SendAsync(ContractJson.Encode(reponse), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.EnvoiImpossible(logger, pluginId, ex);
        }
    }

    private async Task<IEnumerable<RegisteredEntity>> EntitesDuPluginAsync(CancellationToken ct) =>
        (await registry.ListAsync(ct)).Where(e => e.IntegrationId == pluginId);

    // ── Battement de cœur ────────────────────────────────────────────────────

    /// <summary>
    /// Un ping toutes les <see cref="PluginHostOptions.Heartbeat"/>, et fin de session après
    /// <see cref="PluginHostOptions.Timeout"/> de silence. Le contrôle suit le rythme des pings : la mort est
    /// constatée entre Timeout et Timeout + Heartbeat.
    /// </summary>
    private async Task BattementAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.Heartbeat, clock, ct);

                var silence = clock.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _derniereReceptionTicks), TimeSpan.Zero);
                if (silence > options.Timeout)
                {
                    Log.PluginMuet(logger, pluginId, (int)silence.TotalSeconds);
                    await _fin.CancelAsync();
                    return;
                }

                await channel.SendAsync(ContractJson.Encode(new Ping()), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Envoi impossible : la connexion est morte, la réception le constatera aussi.
            Log.EnvoiImpossible(logger, pluginId, ex);
            await _fin.CancelAsync();
        }
    }
}
