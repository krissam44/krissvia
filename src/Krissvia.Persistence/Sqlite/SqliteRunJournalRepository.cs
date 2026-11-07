using System.Text.Json;
using Dapper;
using Krissvia.Core.Journal;

namespace Krissvia.Persistence.Sqlite;

/// <summary>Journal durable des exécutions (table <c>run_journal</c>). Instants en millisecondes Unix UTC.</summary>
public sealed class SqliteRunJournalRepository(SqliteDatabase db) : IRunJournalRepository
{
    public async Task AppendAsync(RunJournalEntry entry, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        await connexion.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO run_journal (source, subject_id, subject_name, at_ms, cause, outcome, detail, steps_json)
            VALUES (@Source, @SubjectId, @SubjectName, @AtMs, @Cause, @Outcome, @Detail, @StepsJson);
            """,
            new { entry.Source, entry.SubjectId, entry.SubjectName, AtMs = entry.At.ToUnixTimeMilliseconds(), entry.Cause, entry.Outcome, entry.Detail,
                StepsJson = Ecrire(entry.Steps) },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RunJournalEntry>> ReadAsync(
        string source, string? subjectId, DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        var lignes = await connexion.QueryAsync<Ligne>(new CommandDefinition(
            """
            SELECT source AS Source, subject_id AS SubjectId, subject_name AS SubjectName, at_ms AS AtMs,
                   cause AS Cause, outcome AS Outcome, detail AS Detail, steps_json AS StepsJson
            FROM run_journal
            WHERE source = @source AND (@subjectId IS NULL OR subject_id = @subjectId) AND at_ms >= @depuis
            ORDER BY at_ms DESC, id DESC
            LIMIT @limit;
            """,
            new { source, subjectId, depuis = since.ToUnixTimeMilliseconds(), limit },
            cancellationToken: ct));

        return [.. lignes.Select(l => new RunJournalEntry(
            l.Source, l.SubjectId, l.SubjectName, DateTimeOffset.FromUnixTimeMilliseconds(l.AtMs), l.Cause, l.Outcome, l.Detail, Lire(l.StepsJson)))];
    }

    public async Task<int> PurgeAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        return await connexion.ExecuteAsync(new CommandDefinition(
            "DELETE FROM run_journal WHERE at_ms < @avant;", new { avant = before.ToUnixTimeMilliseconds() }, cancellationToken: ct));
    }

    private static string? Ecrire(IReadOnlyList<RunJournalStep>? etapes) => etapes is null or { Count: 0 }
        ? null
        : JsonSerializer.Serialize(etapes.Select(e => new Etape(e.At.ToUnixTimeMilliseconds(), e.Label, e.Error)));

    private static IReadOnlyList<RunJournalStep>? Lire(string? json) => json is null
        ? null
        : JsonSerializer.Deserialize<List<Etape>>(json)?.Select(e => new RunJournalStep(DateTimeOffset.FromUnixTimeMilliseconds(e.AtMs), e.Label, e.Error)).ToList();

    private sealed record Etape(long AtMs, string Label, string? Error);

    // Dapper ne lit pas un long dans un DateTimeOffset : une classe de lecture.
    private sealed class Ligne
    {
        public string Source { get; init; } = "";
        public string SubjectId { get; init; } = "";
        public string SubjectName { get; init; } = "";
        public long AtMs { get; init; }
        public string Cause { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string? Detail { get; init; }
        public string? StepsJson { get; init; }
    }
}
