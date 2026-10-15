using Dapper;
using Krissvia.Core.States;

namespace Krissvia.Persistence.Sqlite;

/// <summary>Dernier état de chaque entité (table <c>entity_states</c>). L'instant est stocké en ticks UTC.</summary>
public sealed class SqliteEntityStateRepository(SqliteDatabase db) : IEntityStateRepository
{
    public async Task<IReadOnlyList<PersistedEntityState>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        var lignes = await connexion.QueryAsync<Ligne>(new CommandDefinition(
            "SELECT entity_id AS EntityId, value_json AS ValueJson, measured_at_ticks AS Ticks FROM entity_states;",
            cancellationToken: ct));

        return lignes
            .Select(l => new PersistedEntityState(l.EntityId, l.ValueJson, new DateTimeOffset(l.Ticks, TimeSpan.Zero)))
            .ToList();
    }

    public async Task SaveAsync(IReadOnlyList<PersistedEntityState> states, CancellationToken ct = default)
    {
        if (states.Count == 0) return;

        await using var connexion = await db.OpenAsync(ct);
        await using var transaction = await connexion.BeginTransactionAsync(ct);
        await connexion.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entity_states (entity_id, value_json, measured_at_ticks) VALUES (@EntityId, @ValueJson, @Ticks)
            ON CONFLICT(entity_id) DO UPDATE SET value_json = excluded.value_json, measured_at_ticks = excluded.measured_at_ticks;
            """,
            states.Select(s => new { s.EntityId, s.ValueJson, Ticks = s.MeasuredAt.UtcTicks }),
            transaction,
            cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    // Dapper ne lit pas un ValueTuple par position, ni un long dans un DateTimeOffset : une classe de lecture.
    private sealed class Ligne
    {
        public string EntityId { get; init; } = "";
        public string ValueJson { get; init; } = "";
        public long Ticks { get; init; }
    }
}
