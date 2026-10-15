using Dapper;
using Krissvia.Core.Configuration;

namespace Krissvia.Persistence.Sqlite;

/// <summary>Documents de configuration (table <c>config_documents</c>).</summary>
public sealed class SqliteConfigurationStore(SqliteDatabase db) : IConfigurationStore
{
    public async Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        var lignes = await connexion.QueryAsync<Ligne>(new CommandDefinition(
            "SELECT id AS Id, json AS Json FROM config_documents WHERE collection = @collection ORDER BY id;",
            new { collection }, cancellationToken: ct));
        return lignes.Select(l => new ConfigurationDocument(l.Id, l.Json)).ToList();
    }

    public async Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        await connexion.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO config_documents (collection, id, json) VALUES (@collection, @Id, @Json)
            ON CONFLICT(collection, id) DO UPDATE SET json = excluded.json;
            """,
            new { collection, document.Id, document.Json }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        return await connexion.ExecuteAsync(new CommandDefinition(
            "DELETE FROM config_documents WHERE collection = @collection AND id = @id;",
            new { collection, id }, cancellationToken: ct)) > 0;
    }

    // Dapper ne lit pas un ValueTuple par position : une classe de lecture.
    private sealed class Ligne
    {
        public string Id { get; init; } = "";
        public string Json { get; init; } = "";
    }
}
