using Krissvia.Core.History;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Persistence.Tests;

/// <summary>L'historique sur une vraie base SQLite jetable : seul un aller-retour attrape les surprises de Dapper.</summary>
public sealed class SqliteHistoryRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private async Task<SqliteHistoryRepository> OuvrirAsync()
    {
        var db = new SqliteDatabase($"Data Source={_fichier};Pooling=False");
        await db.InitializeAsync();
        return new SqliteHistoryRepository(db);
    }

    private static HistoryPoint P(string serie, double minutes, double? v) => new(serie, T0.AddMinutes(minutes), v);

    [Fact]
    public async Task Une_courbe_se_relit_apres_redemarrage_avec_la_valeur_en_vigueur_au_debut()
    {
        var depot = await OuvrirAsync();
        await depot.AppendAsync([P("sensor.salon_temperature", 0, 19), P("sensor.salon_temperature", 30, 19.5), P("sensor.salon_temperature", 50, null), P("sensor.salon_xtemperature", 40, 99)]);

        var points = await (await OuvrirAsync()).ReadAsync("sensor.salon_temperature", T0.AddMinutes(20), T0.AddMinutes(60));

        Assert.Equal([P("sensor.salon_temperature", 0, 19), P("sensor.salon_temperature", 30, 19.5), P("sensor.salon_temperature", 50, null)], points);
    }

    [Fact]
    public async Task Les_series_d_une_entite_sont_elle_et_ses_champs_sans_que_le_tiret_bas_serve_de_joker()
    {
        var depot = await OuvrirAsync();
        await depot.AppendAsync([P("climate.salon:temperature", 0, 19), P("climate.salon:target", 0, 20), P("climate.salonx:target", 0, 1), P("climate.saloo", 0, 1)]);

        Assert.Equal(["climate.salon:target", "climate.salon:temperature"], await depot.SeriesAsync("climate.salon"));
        Assert.Empty(await depot.SeriesAsync("climate.sal_n"));
    }

    [Fact]
    public async Task Les_heures_consolidees_survivent_a_la_purge_des_points_bruts()
    {
        var depot = await OuvrirAsync();
        // Radiateur : allumé de 10:10 à 10:25, puis de 11:00 à 11:30.
        await depot.AppendAsync([P("switch.r", 0, 0), P("switch.r", 10, 1), P("switch.r", 25, 0), P("switch.r", 60, 1), P("switch.r", 90, 0)]);

        await depot.RollupAsync(T0, T0.AddHours(2));
        Assert.Equal(T0.AddHours(1), await depot.LastRolledHourAsync());
        Assert.Equal(5, await depot.PurgeAsync(T0.AddHours(3)));

        var heures = await (await OuvrirAsync()).ReadHourlyAsync("switch.r", T0, T0.AddHours(2));
        Assert.Equal([T0, T0.AddHours(1)], heures.Select(h => h.Hour));
        Assert.Equal(0.25, heures[0].Average!.Value, 6);
        Assert.Equal(0.5, heures[1].Average!.Value, 6);
        Assert.Empty(await depot.ReadAsync("switch.r", T0, T0.AddHours(2)));
    }

    [Fact]
    public async Task Une_heure_reconsolidee_tient_compte_de_la_valeur_d_avant_la_fenetre()
    {
        var depot = await OuvrirAsync();
        await depot.AppendAsync([P("sensor.t", 50, 10), P("sensor.t", 70, 20)]);

        // Fenêtre qui commence à 11:00 : de 11:00 à 11:10, c'est encore 10 °C (point de 10:50).
        await depot.RollupAsync(T0.AddHours(1), T0.AddHours(2));

        var heure = Assert.Single(await depot.ReadHourlyAsync("sensor.t", T0.AddHours(1), T0.AddHours(2)));
        Assert.Equal((10 * 10 + 20 * 50) / 60.0, heure.Average!.Value, 6);
        Assert.Equal(10, heure.Min);
    }
}
