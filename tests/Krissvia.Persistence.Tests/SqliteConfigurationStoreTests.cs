using Krissvia.Core.Configuration;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Persistence.Tests;

public sealed class SqliteConfigurationStoreTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Chaque appel recrée tout : on simule un redémarrage.</summary>
    private async Task<SqliteConfigurationStore> OuvrirAsync()
    {
        var db = new SqliteDatabase($"Data Source={_fichier};Pooling=False");
        await db.InitializeAsync();
        return new SqliteConfigurationStore(db);
    }

    [Fact]
    public async Task Un_document_survit_a_un_redemarrage_et_se_remplace()
    {
        var avant = await OuvrirAsync();
        await avant.SaveAsync("thermostats", new ConfigurationDocument("salon", """{"v":1}"""));
        await avant.SaveAsync("thermostats", new ConfigurationDocument("salon", """{"v":2}"""));

        var apres = await OuvrirAsync();

        Assert.Equal("""{"v":2}""", Assert.Single(await apres.ListAsync("thermostats")).Json);
    }

    [Fact]
    public async Task Les_collections_sont_separees()
    {
        var store = await OuvrirAsync();
        await store.SaveAsync("thermostats", new ConfigurationDocument("a", "{}"));
        await store.SaveAsync("delestage", new ConfigurationDocument("a", "{}"));

        Assert.True(await store.DeleteAsync("thermostats", "a"));
        Assert.False(await store.DeleteAsync("thermostats", "a"));
        Assert.Empty(await store.ListAsync("thermostats"));
        Assert.Single(await store.ListAsync("delestage"));
    }
}
