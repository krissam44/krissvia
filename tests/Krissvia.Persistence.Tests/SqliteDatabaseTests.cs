using Dapper;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Persistence.Tests;

/// <summary>Les réglages de chaque connexion : ceux dont dépend la survie de la base sur le Pi.</summary>
public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <remarks>
    /// Le SQLite embarqué (e_sqlite3) est déjà compilé avec FULL par défaut : ce test vérifie le résultat effectif, et
    /// tomberait si un autre fournisseur ou une autre compilation ramenait NORMAL ; retirer le PRAGMA seul ne le fait pas
    /// échouer tant que le défaut reste FULL (vérifié le 23/10/2026).
    /// </remarks>
    [Fact]
    public async Task Chaque_connexion_ecrit_en_wal_avec_synchronous_full()
    {
        var db = new SqliteDatabase($"Data Source={_fichier};Pooling=False");
        await db.InitializeAsync();

        // Une connexion neuve, après l'initialisation : le réglage doit valoir pour toutes, pas pour la première.
        await using var connexion = await db.OpenAsync();

        Assert.Equal("wal", await connexion.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.Equal(2, await connexion.ExecuteScalarAsync<long>("PRAGMA synchronous;"));   // 2 = FULL (1 = NORMAL)
        Assert.Equal(1, await connexion.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
    }
}
