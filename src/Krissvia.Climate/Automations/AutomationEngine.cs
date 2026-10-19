using System.Collections.Concurrent;
using Krissvia.Climate.Presence;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.Automations;

public enum RunOutcome
{
    /// <summary>Toutes les actions ont abouti.</summary>
    Done,

    /// <summary>Une condition n'était pas remplie : rien n'a été fait.</summary>
    ConditionsNotMet,

    /// <summary>Au moins une action a échoué ; les suivantes ont quand même été tentées.</summary>
    Failed,

    /// <summary>Trop de déclenchements en une minute : sans doute une boucle (une action qui redéclenche).</summary>
    Throttled,
}

/// <summary>Une exécution, pour le journal de l'écran Automatismes.</summary>
public sealed record AutomationRun(string AutomationId, string Name, DateTimeOffset At, string Cause, RunOutcome Outcome, string? Detail);

/// <summary>
/// Le moteur des automatismes : reconnaît les déclencheurs, évalue les conditions, exécute les actions dans l'ordre.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Les ordres passent par le bus de commandes, signés <c>automatisme:{id}</c> : une entité inconnue ou une
/// commande qu'elle n'a pas déclarée échoue bruyamment, au lieu du « succès » silencieux de DomoMud vers un Shelly.</item>
/// <item>Une action qui échoue n'arrête pas les suivantes (une notification ntfy désactivée ne doit pas empêcher
/// d'éteindre la lampe) ; l'exécution est notée en échec, avec la raison.</item>
/// <item>Une exécution tourne à part : un « éteindre dans 10 min » ne bloque ni les autres automatismes ni le bus.</item>
/// <item>Plus de <see cref="MaxRunsPerMinute"/> déclenchements en une minute : on s'arrête là. Un automatisme sur
/// « la lampe s'allume » qui bascule cette même lampe tournerait sans fin.</item>
/// </list>
/// </remarks>
public sealed class AutomationEngine(
    ICommandBus commands,
    IEntityStateReader states,
    IEntityRegistry registry,
    ThermostatService thermostats,
    ThermostatEngine thermostatEngine,
    NotificationService notifications,
    IHomeAbsence absence,
    TimeProvider clock)
{
    public const int MaxRunsPerMinute = 10;
    private const int JournalSize = 50;

    private volatile IReadOnlyList<Automation> _automatismes = [];
    private readonly ConcurrentDictionary<string, string> _derniereMinute = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _enCours = new();
    private readonly Lock _verrouJournal = new();
    private readonly LinkedList<AutomationRun> _journal = new();

    /// <summary>Une exécution vient de se terminer (pour le journal du serveur et l'écran).</summary>
    public event Action<AutomationRun>? Ran;

    /// <summary>Les dernières exécutions, de la plus récente à la plus ancienne.</summary>
    public IReadOnlyList<AutomationRun> Journal
    {
        get { lock (_verrouJournal) return [.. _journal]; }
    }

    /// <summary>La dernière exécution d'un automatisme, s'il y en a une dans le journal.</summary>
    public AutomationRun? LastRun(string id)
    {
        lock (_verrouJournal) return _journal.FirstOrDefault(r => r.AutomationId == id);
    }

    public void Configure(IReadOnlyList<Automation> automatismes) => _automatismes = automatismes.ToList();

    /// <summary>Un changement d'état : lance les automatismes actifs qu'il déclenche.</summary>
    public void OnStateChanged(EntityStateChanged e)
    {
        var maintenant = clock.GetUtcNow();
        foreach (var a in _automatismes.Where(a => a.Enabled && AutomationRules.Fires(a.Trigger, e, maintenant)))
            Lancer(a, Cause(a.Trigger, e));
    }

    /// <summary>Le passage de l'horloge (toutes les 15 s environ) : une seule fois par minute et par automatisme.</summary>
    public void Tick()
    {
        var local = clock.GetLocalNow().DateTime;
        var minute = local.ToString("yyyy-MM-dd HH:mm");
        foreach (var a in _automatismes.Where(a => a.Enabled && AutomationRules.TimeMatches(a.Trigger, local)))
        {
            if (_derniereMinute.TryGetValue(a.Id, out var deja) && deja == minute) continue;
            _derniereMinute[a.Id] = minute;
            Lancer(a, $"horaire {a.Trigger.Time:hh\\:mm}");
        }
    }

    /// <summary>Exécution à la main (bouton « Exécuter ») : les conditions sont évaluées comme pour un vrai déclenchement.</summary>
    public Task<AutomationRun> RunNowAsync(Automation a, CancellationToken ct = default) => ExecuterAsync(a, "à la main", ct);

    /// <summary>Attend la fin des exécutions en cours (les tests, l'arrêt du serveur).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_enCours.Keys);

    private void Lancer(Automation a, string cause)
    {
        if (!Autorise(a.Id))
        {
            Noter(new AutomationRun(a.Id, Nom(a), clock.GetUtcNow(), cause, RunOutcome.Throttled,
                $"plus de {MaxRunsPerMinute} déclenchements en une minute : une action redéclenche-t-elle l'automatisme ?"));
            return;
        }

        var tache = Task.Run(() => ExecuterAsync(a, cause, CancellationToken.None));
        _enCours[tache] = 0;
        _ = tache.ContinueWith(t => _enCours.TryRemove(t, out _), TaskScheduler.Default);
    }

    private bool Autorise(string id)
    {
        var maintenant = clock.GetUtcNow();
        var file = _recents.GetOrAdd(id, _ => new Queue<DateTimeOffset>());
        lock (file)
        {
            while (file.Count > 0 && maintenant - file.Peek() >= TimeSpan.FromMinutes(1)) file.Dequeue();
            if (file.Count >= MaxRunsPerMinute) return false;
            file.Enqueue(maintenant);
            return true;
        }
    }

    private async Task<AutomationRun> ExecuterAsync(Automation a, string cause, CancellationToken ct)
    {
        var debut = clock.GetUtcNow();
        AutomationRun run;
        try
        {
            var contexte = new ConditionContext(clock.GetLocalNow().DateTime, absence.Current,
                id => thermostatEngine.GetSnapshot(id)?.Mode, states);
            if (AutomationRules.FirstUnmet(a.Conditions, contexte) is { } nonRemplie)
            {
                run = new AutomationRun(a.Id, Nom(a), debut, cause, RunOutcome.ConditionsNotMet, $"condition non remplie : {AutomationRules.Describe(nonRemplie)}");
            }
            else
            {
                var erreurs = new List<string>();
                foreach (var action in a.Actions)
                {
                    if (action.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(action.DelaySeconds), clock, ct);
                    if (await AgirAsync(a, action, ct) is { } erreur) erreurs.Add(erreur);
                }

                run = new AutomationRun(a.Id, Nom(a), debut, cause, erreurs.Count == 0 ? RunOutcome.Done : RunOutcome.Failed,
                    erreurs.Count == 0 ? null : string.Join(" ; ", erreurs));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run = new AutomationRun(a.Id, Nom(a), debut, cause, RunOutcome.Failed, ex.Message);
        }

        Noter(run);
        return run;
    }

    /// <summary>Exécute une action ; null si elle a abouti, sinon la raison de l'échec.</summary>
    private async Task<string?> AgirAsync(Automation a, AutomationAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case ActionType.TurnOn or ActionType.TurnOff or ActionType.Toggle:
                var id = action.EntityId!;
                var commande = action.Type switch
                {
                    ActionType.TurnOn => StandardCommands.TurnOn,
                    ActionType.TurnOff => StandardCommands.TurnOff,
                    _ => await BasculeAsync(id, ct),
                };
                if (commande is null) return $"{id} : état inconnu, impossible de basculer";
                var issue = await commands.SendAsync(new EntityCommand(id, commande) { Origin = $"automatisme:{a.Id}" }, ct);
                return issue == CommandOutcome.Accepted ? null : $"{id} : {commande} {issue}";

            case ActionType.ThermostatMode:
                return await thermostats.SetModeAsync(action.ThermostatId!, action.Mode, ct) is null
                    ? $"thermostat « {action.ThermostatId} » inconnu"
                    : null;

            case ActionType.Notify:
                var envoi = await notifications.SendAsync(new Notification(action.Title ?? Nom(a), action.Message ?? ""), ct: ct);
                return envoi.Sent ? null : $"notification non envoyée ({envoi.Outcome}{(envoi.Detail is { } d ? $" : {d}" : "")})";

            default:
                return $"action inconnue {action.Type}";
        }
    }

    /// <summary>
    /// La commande de bascule : <c>toggle</c> si l'entité la déclare (l'appareil bascule lui-même, plus sûr que notre
    /// dernier état connu), sinon le contraire de l'état publié. Null si cet état est inconnu.
    /// </summary>
    private async Task<string?> BasculeAsync(string id, CancellationToken ct)
    {
        if (await registry.GetAsync(id, ct) is { } e && e.Commands.Contains(StandardCommands.Toggle)) return StandardCommands.Toggle;
        return AutomationRules.Active(states.Get(id)) switch
        {
            true => StandardCommands.TurnOff,
            false => StandardCommands.TurnOn,
            null => null,
        };
    }

    private void Noter(AutomationRun run)
    {
        lock (_verrouJournal)
        {
            _journal.AddFirst(run);
            while (_journal.Count > JournalSize) _journal.RemoveLast();
        }

        Ran?.Invoke(run);
    }

    private static string Nom(Automation a) => a.Name is { Length: > 0 } n ? n : a.Id;

    private static string Cause(AutomationTrigger t, EntityStateChanged e) => t.Type == TriggerType.Button
        ? $"appui « {e.Current.Value} » sur {e.EntityId}"
        : $"{e.EntityId} {(AutomationRules.Active(e.Current) == true ? "actif" : "au repos")}";
}
