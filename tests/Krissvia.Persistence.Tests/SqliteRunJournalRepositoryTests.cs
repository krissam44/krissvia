using Krissvia.Core.Journal;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Persistence.Tests;

/// <summary>Le journal durable des exécutions sur une vraie base SQLite jetable, redémarrage compris.</summary>
public sealed class SqliteRunJournalRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 17, 43, 0, TimeSpan.Zero);
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Chaque appel recrée la base et le dépôt : on simule un redémarrage.</summary>
    private async Task<SqliteRunJournalRepository> OuvrirAsync()
    {
        var db = new SqliteDatabase($"Data Source={_fichier};Pooling=False");
        await db.InitializeAsync();
        return new SqliteRunJournalRepository(db);
    }

    private static RunJournalEntry E(string sujet, double jours, string issue = "Done", string? detail = null) =>
        new("automation", sujet, $"Éclairage {sujet}", T0.AddDays(jours), "coucher du soleil − 10 min (17:43)", issue, detail);

    [Fact]
    public async Task Les_executions_se_relisent_apres_redemarrage_des_plus_recentes_aux_plus_anciennes()
    {
        var avant = await OuvrirAsync();
        await avant.AppendAsync(E("soir", -2));
        await avant.AppendAsync(E("soir", -1, "Failed", "switch.disjoncteur : injoignable"));
        await avant.AppendAsync(E("matin", -1));
        await avant.AppendAsync(E("soir", 0));

        var apres = await OuvrirAsync();
        var soir = await apres.ReadAsync("automation", "soir", T0.AddDays(-30), 100);

        Assert.Equal([T0, T0.AddDays(-1), T0.AddDays(-2)], soir.Select(e => e.At));
        Assert.Equal(E("soir", -1, "Failed", "switch.disjoncteur : injoignable"), soir[1]);
        Assert.Equal(4, (await apres.ReadAsync("automation", null, T0.AddDays(-30), 100)).Count);
        Assert.Empty(await apres.ReadAsync("alarme", null, T0.AddDays(-30), 100));
    }

    [Fact]
    public async Task La_periode_et_la_limite_bornent_la_lecture()
    {
        var depot = await OuvrirAsync();
        for (var j = -10; j <= 0; j++) await depot.AppendAsync(E("soir", j));

        Assert.Equal(8, (await depot.ReadAsync("automation", "soir", T0.AddDays(-7), 100)).Count);
        Assert.Equal([T0, T0.AddDays(-1)], (await depot.ReadAsync("automation", "soir", T0.AddDays(-30), 2)).Select(e => e.At));
    }

    [Fact]
    public async Task La_purge_n_efface_que_ce_qui_est_plus_ancien()
    {
        var depot = await OuvrirAsync();
        await depot.AppendAsync(E("soir", -100));
        await depot.AppendAsync(E("soir", -91));
        await depot.AppendAsync(E("soir", -89));

        Assert.Equal(2, await depot.PurgeAsync(T0.AddDays(-90)));
        Assert.Equal([T0.AddDays(-89)], (await depot.ReadAsync("automation", null, T0.AddDays(-365), 100)).Select(e => e.At));
    }
}
