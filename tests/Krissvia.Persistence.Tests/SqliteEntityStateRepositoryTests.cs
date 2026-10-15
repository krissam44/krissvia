using System.Text.Json;
using Krissvia.Core.States;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Persistence.Tests;

/// <summary>Le magasin d'états sur une vraie base SQLite jetable, redémarrage compris.</summary>
public sealed class SqliteEntityStateRepositoryTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Chaque appel recrée tous les objets : on simule un redémarrage de l'application.</summary>
    private async Task<EntityStateStore> OuvrirAsync()
    {
        var db = new SqliteDatabase($"Data Source={_fichier};Pooling=False");
        await db.InitializeAsync();
        var magasin = new EntityStateStore(new SqliteEntityStateRepository(db), TimeProvider.System);
        await magasin.LoadAsync();
        return magasin;
    }

    [Fact]
    public async Task Un_etat_survit_a_un_redemarrage_avec_son_instant_d_origine_et_indisponible()
    {
        var mesure = new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero);
        var avant = await OuvrirAsync();
        avant.Update("sensor.salon_temperature", JsonSerializer.SerializeToElement(19.5), mesure);
        Assert.Equal(1, await avant.FlushAsync());

        var apres = await OuvrirAsync();

        var etat = apres.Get("sensor.salon_temperature");
        Assert.NotNull(etat);
        Assert.Equal(19.5, etat.Value.GetDouble());
        Assert.Equal(mesure, etat.MeasuredAt);
        Assert.False(etat.Available);
    }

    [Fact]
    public async Task Une_nouvelle_valeur_remplace_l_ancienne()
    {
        var magasin = await OuvrirAsync();
        magasin.Update("switch.prise", JsonSerializer.SerializeToElement(true));
        await magasin.FlushAsync();
        magasin.Update("switch.prise", JsonSerializer.SerializeToElement(false));
        await magasin.FlushAsync();

        var apres = await OuvrirAsync();

        Assert.Equal(JsonValueKind.False, apres.Get("switch.prise")!.Value.ValueKind);
        Assert.Single(apres.GetAll());
    }
}
