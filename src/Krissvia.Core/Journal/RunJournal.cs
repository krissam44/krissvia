namespace Krissvia.Core.Journal;

/// <summary>
/// Une exécution notée au journal durable : un automatisme qui est parti, pourquoi, et comment ça s'est terminé.
/// </summary>
/// <param name="Source">Qui écrit (« automation ») : le même journal pourra servir à d'autres moteurs.</param>
/// <param name="SubjectId">Ce qui a tourné (l'identifiant de l'automatisme).</param>
/// <param name="SubjectName">Son nom au moment de l'exécution : il reste lisible même si on le renomme ou le supprime.</param>
/// <param name="Cause">Ce qui l'a déclenché (« coucher du soleil − 10 min (17:43) »).</param>
/// <param name="Outcome">L'issue, en texte (le nom de l'énumération du moteur).</param>
public sealed record RunJournalEntry(
    string Source, string SubjectId, string SubjectName, DateTimeOffset At, string Cause, string Outcome, string? Detail);

/// <summary>
/// Journal durable des exécutions (table <c>run_journal</c>). Le journal en mémoire des moteurs disparaît au
/// redémarrage ; celui-ci dit, des semaines plus tard, si l'éclairage extérieur s'est bien allumé chaque soir.
/// </summary>
public interface IRunJournalRepository
{
    Task AppendAsync(RunJournalEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Les exécutions de <paramref name="source"/> depuis <paramref name="since"/>, de la plus récente à la plus
    /// ancienne, <paramref name="limit"/> au plus ; <paramref name="subjectId"/> null : tous les sujets.
    /// </summary>
    Task<IReadOnlyList<RunJournalEntry>> ReadAsync(
        string source, string? subjectId, DateTimeOffset since, int limit, CancellationToken ct = default);

    /// <summary>Efface ce qui est antérieur à <paramref name="before"/>. Renvoie le nombre de lignes effacées.</summary>
    Task<int> PurgeAsync(DateTimeOffset before, CancellationToken ct = default);
}
