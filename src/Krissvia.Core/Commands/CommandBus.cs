using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Events;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Commands;

/// <summary>Un ordre complet, tel qu'il entre dans le bus.</summary>
public sealed record EntityCommand(string EntityId, string Command)
{
    public JsonElement? Args { get; init; }
    public CommandPriority Priority { get; init; } = CommandPriority.Normal;
    public int? TtlSeconds { get; init; }

    /// <summary>Qui donne l'ordre (« delestage », « api », « automatisme:… ») : pour le journal.</summary>
    public string Origin { get; init; } = "coeur";
}

/// <summary>Transport d'un ordre jusqu'au plugin qui porte l'entité (PluginHost en production).</summary>
public interface ICommandDispatcher
{
    Task<CommandOutcome> DispatchAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct);
}

/// <summary>Ce qu'il est advenu d'un ordre, pour le journal et le bus d'événements.</summary>
public sealed record CommandRecord(
    EntityCommand Command,
    CommandOutcome Outcome,
    string? Detail,
    DateTimeOffset SentAt,
    TimeSpan Duration,
    CommandVerification Verification);

public enum CommandVerification
{
    /// <summary>Ordre simple : pas de vérification demandée.</summary>
    NotRequested,

    /// <summary>L'ordre n'a pas d'effet vérifiable sur l'état (bascule, luminosité, volet…).</summary>
    NotApplicable,

    /// <summary>L'ordre n'est pas parti, ou n'a pas été accepté : rien à vérifier.</summary>
    NotSent,

    /// <summary>L'entité a publié, après l'ordre, l'état attendu.</summary>
    Confirmed,

    /// <summary>Accepté, mais aucun état attendu n'est arrivé dans le délai : l'effet n'est PAS acquis.</summary>
    NotConfirmed,
}

/// <param name="Detail">Motif d'un refus ou d'un échec, lisible tel quel (« thermostat arrêté… »).</param>
public sealed record VerifiedCommandResult(CommandOutcome Outcome, CommandVerification Verification, string? Detail = null)
{
    /// <summary>Vrai seulement si l'effet est constaté. C'est la seule réussite d'un ordre de sécurité.</summary>
    public bool Confirmed => Verification == CommandVerification.Confirmed;
}

public interface ICommandBus : IEntityCommander
{
    Task<CommandOutcome> SendAsync(EntityCommand command, CancellationToken ct = default);

    /// <summary>
    /// Envoie, puis attend que l'entité publie l'état attendu (ARCHITECTURE.md §7.3) : « accepté » dit seulement
    /// que le plugin a pris l'ordre, pas que le relais a obéi.
    /// </summary>
    Task<VerifiedCommandResult> SendVerifiedAsync(EntityCommand command, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Derniers ordres, du plus récent au plus ancien.</summary>
    IReadOnlyList<CommandRecord> RecentCommands { get; }
}

/// <summary>
/// Passage unique de tous les ordres du cœur (moteurs, API, automatismes).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Une entité inconnue, ou une commande qu'elle n'a pas déclarée, échoue ici, bruyamment : c'est le
/// radiateur « Chbre » de DomoMud, dont l'ordre partait dans le vide en annonçant un succès.</item>
/// <item>Une entité désactivée refuse les ordres normaux, jamais un ordre de SÉCURITÉ : désactiver un radiateur
/// dans l'interface ne doit pas empêcher le délestage de le couper.</item>
/// <item>La vérification n'accepte qu'un état publié APRÈS l'ordre. L'état déjà connu ne prouve rien : c'est
/// la croyance du cœur, celle qui laissait un Shelly alimenté en juillet 2026 pendant que l'écran disait
/// « délesté ».</item>
/// <item>Une exception du transport devient un échec, jamais une exception pour le moteur qui a donné
/// l'ordre.</item>
/// </list>
/// </remarks>
public sealed class CommandBus : ICommandBus, IDisposable
{
    public static readonly TimeSpan DefaultVerificationTimeout = TimeSpan.FromSeconds(10);
    private const int TailleJournal = 200;

    private readonly IEntityRegistry _registry;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IEventBus _events;
    private readonly TimeProvider _clock;
    private readonly IDisposable _abonnement;

    private readonly ConcurrentDictionary<Guid, Attente> _attentes = new();
    private readonly LinkedList<CommandRecord> _journal = new();
    private readonly Lock _verrouJournal = new();

    private sealed record Attente(string EntityId, Func<EntityStateSnapshot, bool> Attendu, TaskCompletionSource Arrivee);

    public CommandBus(IEntityRegistry registry, ICommandDispatcher dispatcher, IEventBus events, TimeProvider clock)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _events = events;
        _clock = clock;
        _abonnement = events.Subscribe<EntityStateChanged>("bus-commandes:verification", SurEtatAsync);
    }

    public IReadOnlyList<CommandRecord> RecentCommands
    {
        get
        {
            lock (_verrouJournal) return _journal.ToList();
        }
    }

    public Task<CommandOutcome> SendAsync(
        string entityId,
        string command,
        CommandPriority priority = CommandPriority.Normal,
        int? ttlSeconds = null,
        CancellationToken ct = default) =>
        SendAsync(new EntityCommand(entityId, command) { Priority = priority, TtlSeconds = ttlSeconds }, ct);

    public async Task<CommandOutcome> SendAsync(EntityCommand command, CancellationToken ct = default) =>
        (await ExecuterAsync(command, verifier: false, timeout: null, ct)).Outcome;

    public Task<VerifiedCommandResult> SendVerifiedAsync(EntityCommand command, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ExecuterAsync(command, verifier: true, timeout, ct);

    private async Task<VerifiedCommandResult> ExecuterAsync(EntityCommand ordre, bool verifier, TimeSpan? timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ordre);
        var debut = _clock.GetUtcNow();

        var entite = await _registry.GetAsync(ordre.EntityId, ct);
        var refus = entite is null ? "entité inconnue"
            : !entite.Commands.Contains(ordre.Command, StringComparer.Ordinal) ? $"commande « {ordre.Command} » non déclarée par l'entité"
            : !entite.Enabled && ordre.Priority != CommandPriority.Safety ? "entité désactivée"
            : null;
        if (refus is not null)
            return Journaliser(ordre, CommandOutcome.Failed, refus, debut, verifier ? CommandVerification.NotSent : CommandVerification.NotRequested);

        // L'attente est posée AVANT l'envoi : l'état peut revenir avant même que le plugin ait répondu.
        var attendu = verifier ? EtatAttendu(entite!.Kind, ordre.Command) : null;
        Attente? attente = null;
        var cle = Guid.NewGuid();
        if (attendu is not null)
        {
            attente = new Attente(ordre.EntityId, attendu, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _attentes[cle] = attente;
        }

        try
        {
            CommandOutcome issue;
            string? detail = null;
            try
            {
                issue = await _dispatcher.DispatchAsync(entite!, ordre, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issue = CommandOutcome.Failed;
                detail = ex.Message;
            }

            if (!verifier) return Journaliser(ordre, issue, detail, debut, CommandVerification.NotRequested);
            if (issue != CommandOutcome.Accepted) return Journaliser(ordre, issue, detail, debut, CommandVerification.NotSent);
            if (attente is null) return Journaliser(ordre, issue, detail, debut, CommandVerification.NotApplicable);

            try
            {
                await attente.Arrivee.Task.WaitAsync(timeout ?? DefaultVerificationTimeout, _clock, ct);
                return Journaliser(ordre, issue, detail, debut, CommandVerification.Confirmed);
            }
            catch (TimeoutException)
            {
                return Journaliser(ordre, issue, "aucun état confirmant l'ordre dans le délai", debut, CommandVerification.NotConfirmed);
            }
        }
        finally
        {
            _attentes.TryRemove(cle, out _);
        }
    }

    /// <summary>
    /// L'état qui prouve l'effet d'un ordre, quand il en existe un : marche/arrêt d'un relais ou d'une lumière.
    /// Une bascule n'en a pas (on ne sait pas d'où l'on part), ni un réglage de luminosité ou de volet.
    /// Une entité indisponible ne confirme jamais rien.
    /// </summary>
    private static Func<EntityStateSnapshot, bool>? EtatAttendu(EntityKind kind, string commande)
    {
        bool? allume = commande switch
        {
            StandardCommands.TurnOn => true,
            StandardCommands.TurnOff => false,
            _ => null,
        };
        if (allume is not { } voulu) return null;

        return kind switch
        {
            EntityKind.Switch => s => s.TryGetBool(out var b) && b == voulu,
            EntityKind.Light => s => s.Available && s.Value.ValueKind == JsonValueKind.Object &&
                                     s.Value.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                     on.GetBoolean() == voulu,
            _ => null,
        };
    }

    private Task SurEtatAsync(EntityStateChanged e, CancellationToken ct)
    {
        foreach (var attente in _attentes.Values)
            if (attente.EntityId == e.EntityId && attente.Attendu(e.Current)) attente.Arrivee.TrySetResult();
        return Task.CompletedTask;
    }

    private VerifiedCommandResult Journaliser(
        EntityCommand ordre, CommandOutcome issue, string? detail, DateTimeOffset debut, CommandVerification verification)
    {
        var trace = new CommandRecord(ordre, issue, detail, debut, _clock.GetUtcNow() - debut, verification);
        lock (_verrouJournal)
        {
            _journal.AddFirst(trace);
            if (_journal.Count > TailleJournal) _journal.RemoveLast();
        }

        _events.Publish(new CommandCompleted(trace));
        return new VerifiedCommandResult(issue, verification, detail);
    }

    public void Dispose() => _abonnement.Dispose();
}
