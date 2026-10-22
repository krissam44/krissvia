using System.Collections.Concurrent;
using Dapper;
using Krissvia.Core.History;
using Microsoft.Data.Sqlite;

namespace Krissvia.Persistence.Sqlite;

/// <summary>
/// Historique (tables <c>history_series</c>, <c>history_points</c>, <c>history_hourly</c>). Instants en secondes Unix
/// UTC ; une série est désignée par un entier, la clé texte (<c>climate.salon:temperature</c>) n'étant écrite qu'une fois.
/// </summary>
public sealed class SqliteHistoryRepository(SqliteDatabase db) : IHistoryRepository
{
    private readonly ConcurrentDictionary<string, long> _identifiants = new(StringComparer.Ordinal);

    public async Task AppendAsync(IReadOnlyList<HistoryPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        await using var connexion = await db.OpenAsync(ct);
        await using var transaction = await connexion.BeginTransactionAsync(ct);
        var lignes = new List<object>(points.Count);
        var nouveaux = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in points)
        {
            if (!_identifiants.TryGetValue(p.Series, out var id) && !nouveaux.TryGetValue(p.Series, out id))
                nouveaux[p.Series] = id = await IdentifiantAsync(connexion, transaction, p.Series, ct);
            lignes.Add(new { Serie = id, Ts = p.At.ToUnixTimeSeconds(), p.Value });
        }

        // Deux points dans la même seconde : le dernier gagne.
        await connexion.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO history_points (series_id, ts, value) VALUES (@Serie, @Ts, @Value);", lignes, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        // Retenus seulement une fois validés : un lot annulé ne laisse pas d'identifiant fantôme en cache.
        foreach (var (cle, id) in nouveaux) _identifiants[cle] = id;
    }

    public async Task<IReadOnlyList<HistoryPoint>> ReadAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        if (await TrouverAsync(connexion, series, ct) is not { } id) return [];
        var lignes = await connexion.QueryAsync<Point>(new CommandDefinition(
            """
            SELECT ts AS Ts, value AS Value FROM (
                SELECT ts, value FROM history_points WHERE series_id = @id AND ts < @de ORDER BY ts DESC LIMIT 1)
            UNION ALL
            SELECT ts, value FROM history_points WHERE series_id = @id AND ts >= @de AND ts <= @a
            ORDER BY Ts;
            """,
            new { id, de = from.ToUnixTimeSeconds(), a = to.ToUnixTimeSeconds() }, cancellationToken: ct));
        return [.. lignes.Select(l => new HistoryPoint(series, DateTimeOffset.FromUnixTimeSeconds(l.Ts), l.Value))];
    }

    public async Task<IReadOnlyList<HistoryBucket>> ReadHourlyAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        if (await TrouverAsync(connexion, series, ct) is not { } id) return [];
        var lignes = await connexion.QueryAsync<Heure>(new CommandDefinition(
            """
            SELECT hour AS Hour, min AS Min, max AS Max, avg AS Avg, last AS Last, coverage AS Coverage
            FROM history_hourly WHERE series_id = @id AND hour >= @de AND hour <= @a ORDER BY hour;
            """,
            new { id, de = HourlyRollup.Debut(from).ToUnixTimeSeconds(), a = to.ToUnixTimeSeconds() }, cancellationToken: ct));
        return [.. lignes.Select(l => new HistoryBucket(DateTimeOffset.FromUnixTimeSeconds(l.Hour), l.Min, l.Max, l.Avg, l.Last, l.Coverage))];
    }

    public async Task<IReadOnlyList<string>> SeriesAsync(string entityId, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        // Pas de LIKE : « _ » y est un joker, et les identifiants d'entités en sont pleins.
        var cles = await connexion.QueryAsync<string>(new CommandDefinition(
            "SELECT key FROM history_series WHERE key = @id OR substr(key, 1, length(@prefixe)) = @prefixe ORDER BY key;",
            new { id = entityId, prefixe = entityId + ":" }, cancellationToken: ct));
        return [.. cles];
    }

    public async Task RollupAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var debut = HourlyRollup.Debut(from);
        if (to <= debut) return;

        await using var connexion = await db.OpenAsync(ct);
        var de = debut.ToUnixTimeSeconds();
        var points = (await connexion.QueryAsync<PointSerie>(new CommandDefinition(
            "SELECT series_id AS Serie, ts AS Ts, value AS Value FROM history_points WHERE ts >= @de AND ts < @a ORDER BY series_id, ts;",
            new { de, a = to.ToUnixTimeSeconds() }, cancellationToken: ct))).ToList();
        if (points.Count == 0) return;

        // La valeur en vigueur au début de la fenêtre : le dernier point avant, par série.
        var avant = (await connexion.QueryAsync<PointSerie>(new CommandDefinition(
            """
            SELECT p.series_id AS Serie, p.ts AS Ts, p.value AS Value FROM history_points p
            JOIN (SELECT series_id, MAX(ts) AS ts FROM history_points WHERE ts < @de GROUP BY series_id) d
              ON d.series_id = p.series_id AND d.ts = p.ts;
            """,
            new { de }, cancellationToken: ct))).ToDictionary(p => p.Serie, p => p.Value);

        var heures = new List<object>();
        foreach (var serie in points.GroupBy(p => p.Serie))
        {
            var enVigueur = avant.GetValueOrDefault(serie.Key);
            // Seules les heures qui ont des points : une série qui ne publie plus n'est pas prolongée à l'infini.
            foreach (var heure in serie.GroupBy(p => HourlyRollup.Debut(DateTimeOffset.FromUnixTimeSeconds(p.Ts))))
            {
                var fin = heure.Key + HourlyRollup.Heure < to ? heure.Key + HourlyRollup.Heure : to;
                var seau = HourlyRollup.Calculer(heure.Key, enVigueur,
                    [.. heure.Select(p => (DateTimeOffset.FromUnixTimeSeconds(p.Ts), p.Value))], fin);
                enVigueur = heure.Last().Value;
                if (seau is null) continue;
                heures.Add(new { Serie = serie.Key, Hour = seau.Hour.ToUnixTimeSeconds(), seau.Min, seau.Max, Avg = seau.Average, seau.Last, seau.Coverage });
            }
        }

        await using var transaction = await connexion.BeginTransactionAsync(ct);
        await connexion.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR REPLACE INTO history_hourly (series_id, hour, min, max, avg, last, coverage)
            VALUES (@Serie, @Hour, @Min, @Max, @Avg, @Last, @Coverage);
            """, heures, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<DateTimeOffset?> LastRolledHourAsync(CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        var heure = await connexion.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(hour) FROM history_hourly;", cancellationToken: ct));
        return heure is { } h ? DateTimeOffset.FromUnixTimeSeconds(h) : null;
    }

    public async Task<int> PurgeAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        return await connexion.ExecuteAsync(new CommandDefinition(
            "DELETE FROM history_points WHERE ts < @avant;", new { avant = before.ToUnixTimeSeconds() }, cancellationToken: ct));
    }

    private static async Task<long> IdentifiantAsync(SqliteConnection connexion, System.Data.Common.DbTransaction transaction, string cle, CancellationToken ct)
    {
        await connexion.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO history_series (key) VALUES (@cle);", new { cle }, transaction, cancellationToken: ct));
        return await connexion.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM history_series WHERE key = @cle;", new { cle }, transaction, cancellationToken: ct));
    }

    private async Task<long?> TrouverAsync(SqliteConnection connexion, string cle, CancellationToken ct)
    {
        if (_identifiants.TryGetValue(cle, out var id)) return id;
        var trouve = await connexion.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM history_series WHERE key = @cle;", new { cle }, cancellationToken: ct));
        if (trouve is { } t) _identifiants[cle] = t;
        return trouve;
    }

    // Dapper ne lit pas un ValueTuple par position : des classes de lecture.
    private sealed class Point
    {
        public long Ts { get; init; }
        public double? Value { get; init; }
    }

    private sealed class PointSerie
    {
        public long Serie { get; init; }
        public long Ts { get; init; }
        public double? Value { get; init; }
    }

    private sealed class Heure
    {
        public long Hour { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        public double? Avg { get; init; }
        public double? Last { get; init; }
        public double Coverage { get; init; }
    }
}
