using Krissvia.Core.Journal;

namespace Krissvia.Climate.Automations;

/// <summary>
/// Les exécutions des automatismes, gardées <see cref="Retention"/> en base : le journal du moteur, en mémoire, ne
/// survit pas à un redémarrage, et c'est sur plusieurs semaines qu'on vérifie qu'un éclairage s'allume chaque soir.
/// </summary>
public sealed class AutomationHistory(IRunJournalRepository repository, TimeProvider clock)
{
    public const string Source = "automation";

    /// <summary>Durée de conservation : une saison, de quoi comparer novembre et février.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public Task RecordAsync(AutomationRun r, CancellationToken ct = default) =>
        repository.AppendAsync(new RunJournalEntry(Source, r.AutomationId, r.Name, r.At, r.Cause, r.Outcome.ToString(), r.Detail,
            r.Steps?.Select(e => new RunJournalStep(e.At, e.Label, e.Error)).ToList()), ct);

    /// <summary>
    /// Les exécutions des <paramref name="period"/> passés, de la plus récente à la plus ancienne ;
    /// <paramref name="automationId"/> null : tous les automatismes.
    /// </summary>
    public async Task<IReadOnlyList<AutomationRun>> ReadAsync(string? automationId, TimeSpan period, int limit = 1000, CancellationToken ct = default)
    {
        var lignes = await repository.ReadAsync(Source, automationId, clock.GetUtcNow() - period, limit, ct);
        return
        [
            .. lignes.Select(l => new AutomationRun(l.SubjectId, l.SubjectName, l.At, l.Cause,
                // Une issue inconnue (version plus récente relue par une plus ancienne) se lit comme un échec : jamais un succès.
                Enum.TryParse<RunOutcome>(l.Outcome, out var issue) ? issue : RunOutcome.Failed, l.Detail,
                l.Steps?.Select(e => new RunStep(e.At, e.Label, e.Error)).ToList())),
        ];
    }

    /// <summary>Efface ce qui dépasse <see cref="Retention"/>. Renvoie le nombre d'exécutions effacées.</summary>
    public Task<int> PurgeAsync(CancellationToken ct = default) => repository.PurgeAsync(clock.GetUtcNow() - Retention, ct);
}
