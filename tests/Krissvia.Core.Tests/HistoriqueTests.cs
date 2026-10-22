using System.Text.Json;
using Krissvia.Core.Events;
using Krissvia.Core.History;
using Krissvia.Core.States;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

/// <summary>L'historique : ce qu'on garde d'une valeur, à quelle densité, et ce que vaut une heure.</summary>
public class HistoriqueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    // ── Valeurs ──────────────────────────────────────────────────────────────

    [Fact]
    public void Un_nombre_un_booleen_et_une_lumiere_font_une_serie_un_thermostat_ses_champs_utiles_un_texte_rien()
    {
        Assert.Equal([("sensor.t", (double?)19.5)], HistoryValues.Extraire("sensor.t", J(19.5)));
        Assert.Equal([("switch.r", (double?)1)], HistoryValues.Extraire("switch.r", J(true)));
        Assert.Equal([("light.l", (double?)0)], HistoryValues.Extraire("light.l", J(new { on = false, brightness = 40 })));
        Assert.Empty(HistoryValues.Extraire("sensor.tarif", J("HC")));

        var thermostat = HistoryValues.Extraire("climate.salon", J(new
        {
            temperature = 19.2, target = 20.0, mode = "comfort", power = 0.4, heating = true, outdoor_temperature = (double?)null,
            status_label = "Chauffe",
        }));
        Assert.Equal(
        [
            ("climate.salon:temperature", (double?)19.2), ("climate.salon:target", 20.0), ("climate.salon:power", 0.4),
            ("climate.salon:outdoor_temperature", null), ("climate.salon:heating", 1),
        ], thermostat);
    }

    // ── Échantillonnage ──────────────────────────────────────────────────────

    [Fact]
    public void Au_plus_un_point_par_minute_le_dernier_changement_gagne_avec_son_instant()
    {
        var e = new HistorySampler();
        e.Offer("s", T0, 1);
        Assert.Equal([new HistoryPoint("s", T0, 1)], e.TakeDue(T0));

        e.Offer("s", T0.AddSeconds(10), 2);
        e.Offer("s", T0.AddSeconds(40), 3);
        Assert.Empty(e.TakeDue(T0.AddSeconds(50)));
        Assert.Equal([new HistoryPoint("s", T0.AddSeconds(40), 3)], e.TakeDue(T0.AddSeconds(60)));
    }

    [Fact]
    public void Une_valeur_inchangee_ne_s_ecrit_qu_au_battement_et_un_aller_retour_ne_laisse_rien()
    {
        var e = new HistorySampler();
        e.Offer("s", T0, 5);
        e.TakeDue(T0);

        e.Offer("s", T0.AddMinutes(5), 5);                    // même valeur : rien
        e.Offer("s", T0.AddMinutes(10), 6);
        e.Offer("s", T0.AddMinutes(10).AddSeconds(5), 5);      // retour à 5 avant l'écriture
        Assert.Empty(e.TakeDue(T0.AddMinutes(11)));

        Assert.Empty(e.TakeDue(T0 + HistorySampler.Battement - TimeSpan.FromSeconds(1)));
        Assert.Equal([new HistoryPoint("s", T0 + HistorySampler.Battement, 5)], e.TakeDue(T0 + HistorySampler.Battement));
    }

    [Fact]
    public void Un_capteur_qui_republie_la_meme_valeur_garde_son_battement()
    {
        var e = new HistorySampler();
        e.Offer("s", T0, 5);
        e.TakeDue(T0);

        var points = new List<HistoryPoint>();
        for (var s = 10; s <= 31 * 60; s += 10)   // le Linky republie toutes les 10 s, sans changer
        {
            e.Offer("s", T0.AddSeconds(s), 5);
            points.AddRange(e.TakeDue(T0.AddSeconds(s)));
        }

        Assert.Equal([new HistoryPoint("s", T0 + HistorySampler.Battement, 5)], points);
    }

    [Fact]
    public void Un_trou_s_ecrit_et_n_a_pas_de_battement()
    {
        var e = new HistorySampler();
        e.Offer("s", T0, 5);
        e.TakeDue(T0);
        e.Offer("s", T0.AddMinutes(2), null);

        Assert.Equal([new HistoryPoint("s", T0.AddMinutes(2), null)], e.TakeDue(T0.AddMinutes(2)));
        Assert.Empty(e.TakeDue(T0.AddHours(2)));
    }

    [Fact]
    public void Un_point_ne_precede_jamais_le_precedent_et_un_echec_le_remet_en_attente()
    {
        var e = new HistorySampler();
        e.Offer("s", T0, 1);
        e.TakeDue(T0);
        e.Offer("s", T0.AddSeconds(-30), 2);   // horloge de plugin qui recule
        var points = e.TakeDue(T0.AddMinutes(1));
        Assert.Equal([new HistoryPoint("s", T0.AddSeconds(1), 2)], points);

        e.Requeue(points);
        Assert.Equal(2, Assert.Single(e.TakeDue(T0.AddMinutes(1))).Value);
    }

    [Fact]
    public async Task L_enregistreur_suit_le_bus_et_un_thermostat_injoignable_troue_chacune_de_ses_series()
    {
        var horloge = new FakeTimeProvider(T0);
        var depot = new DepotEnMemoire();
        var enregistreur = new HistoryRecorder(depot, horloge);
        await using var bus = new EventBus();
        using var _ = enregistreur.Attach(bus);

        bus.Publish(new EntityStateChanged("climate.salon", null, new EntityStateSnapshot(J(new { temperature = 19.0, target = 20.0 }), T0)));
        await AttendreAsync(async () => await enregistreur.FlushAsync() > 0 || depot.Points.Count > 0);
        Assert.Equal(["climate.salon:target", "climate.salon:temperature"], depot.Points.Select(p => p.Series).Order());

        horloge.Advance(TimeSpan.FromMinutes(2));
        enregistreur.Observe("climate.salon", new EntityStateSnapshot(J(new { temperature = 19.0, target = 20.0 }), T0, Available: false));
        await enregistreur.FlushAsync();
        Assert.Equal(2, depot.Points.Count(p => p.Value is null && p.At == T0.AddMinutes(2)));
    }

    private static async Task AttendreAsync(Func<Task<bool>> condition)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    private sealed class DepotEnMemoire : IHistoryRepository
    {
        public List<HistoryPoint> Points { get; } = [];

        public Task AppendAsync(IReadOnlyList<HistoryPoint> points, CancellationToken ct = default)
        {
            lock (Points) Points.AddRange(points);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HistoryPoint>> ReadAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistoryBucket>> ReadHourlyAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SeriesAsync(string entityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RollupAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DateTimeOffset?> LastRolledHourAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(DateTimeOffset before, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── Heures ───────────────────────────────────────────────────────────────

    [Fact]
    public void Une_heure_est_une_moyenne_ponderee_par_le_temps_un_radiateur_allume_un_quart_d_heure_vaut_0_25()
    {
        // Éteint à l'entrée de l'heure, allumé de :10 à :25, puis éteint.
        var seau = HourlyRollup.Calculer(T0, 0, [(T0.AddMinutes(10), 1), (T0.AddMinutes(25), 0)], T0.AddHours(1))!;

        Assert.Equal(0.25, seau.Average!.Value, 6);
        Assert.Equal((0d, 1d, 0d, 1d), (seau.Min!.Value, seau.Max!.Value, seau.Last!.Value, seau.Coverage));
    }

    [Fact]
    public void Un_trou_ne_compte_ni_dans_la_moyenne_ni_dans_la_couverture()
    {
        // 20 °C la première demi-heure, injoignable ensuite.
        var seau = HourlyRollup.Calculer(T0, 20, [(T0.AddMinutes(30), null)], T0.AddHours(1))!;

        Assert.Equal(20, seau.Average);
        Assert.Equal(0.5, seau.Coverage);
        Assert.Null(seau.Last);
    }

    [Fact]
    public void La_purge_ne_depasse_jamais_la_derniere_heure_consolidee()
    {
        Assert.Equal(T0 - HourlyRollup.Retention, HourlyRollup.LimitePurge(T0, T0.AddHours(-1)));
        Assert.Equal(T0.AddDays(-40), HourlyRollup.LimitePurge(T0, T0.AddDays(-40)));   // consolidation en retard
        Assert.Null(HourlyRollup.LimitePurge(T0, null));                               // rien de consolidé : rien à purger
    }

    [Fact]
    public void L_heure_en_cours_s_arrete_a_maintenant_et_une_heure_inconnue_ne_rend_rien()
    {
        var seau = HourlyRollup.Calculer(T0, 10, [(T0.AddMinutes(10), 20)], T0.AddMinutes(20))!;
        Assert.Equal(15, seau.Average);
        Assert.Equal(1, seau.Coverage);

        Assert.Null(HourlyRollup.Calculer(T0, null, [], T0.AddHours(1)));
        Assert.Equal(T0, HourlyRollup.Debut(T0.AddMinutes(59).AddSeconds(59)));
    }
}
