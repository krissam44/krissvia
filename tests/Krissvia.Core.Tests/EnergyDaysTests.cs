using Krissvia.Core.History;

namespace Krissvia.Core.Tests;

/// <summary>La consommation par jour d'un compteur, tirée des heures de son index.</summary>
public class EnergyDaysTests
{
    /// <summary>Paris en été (UTC+2), fixé : le test ne dépend pas du fuseau de la machine.</summary>
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("essai", TimeSpan.FromHours(2), "essai", "essai");

    private static readonly DateOnly J1 = new(2026, 10, 20);

    /// <summary>Une heure de l'index, à l'heure LOCALE donnée du jour J1 (+ jours).</summary>
    private static HistoryBucket H(int jour, int heureLocale, double dernier, double? min = null, double? max = null) =>
        new(EnergyDays.Midnight(J1.AddDays(jour), Paris).AddHours(heureLocale), min ?? dernier, max ?? dernier, dernier, dernier, 1);

    [Fact]
    public void Un_jour_est_la_somme_des_hausses_d_heure_en_heure_y_compris_depuis_la_veille()
    {
        // La veille finit à 1000 ; le jour monte à 1003 à 8 h puis 1006,5 à 23 h.
        var index = new[] { H(-1, 23, 1000), H(0, 8, 1003), H(0, 23, 1006.5) };

        var j = Assert.Single(EnergyDays.FromHours(index, [], Paris, J1, J1));

        Assert.Equal(6.5, j.Kwh!.Value, 6);
    }

    [Fact]
    public void Minuit_est_celui_du_fuseau_pas_celui_d_utc()
    {
        // 0 h 30 locale = 22 h 30 UTC la veille : cette montée appartient au 20, pas au 19.
        var index = new[] { H(-1, 20, 50), H(0, 0, 52), H(0, 10, 53) };

        var jours = EnergyDays.FromHours(index, [], Paris, J1.AddDays(-1), J1);

        Assert.Equal(0, jours[0].Kwh);   // la veille n'a que son premier relevé : aucune hausse
        Assert.Equal(3, jours[1].Kwh!.Value, 6);
    }

    [Fact]
    public void Un_compteur_remis_a_zero_ne_gonfle_pas_le_jour()
    {
        // 5000 → remis à zéro, reparti à 2 → 4 : 4 kWh, et non 5000 comme avec « max − min ».
        var index = new[] { H(-1, 23, 5000), H(0, 9, 2), H(0, 12, 4) };

        Assert.Equal(4, EnergyDays.FromHours(index, [], Paris, J1, J1)[0].Kwh!.Value, 6);
    }

    [Fact]
    public void Sans_veille_la_premiere_heure_compte_sa_propre_montee_et_un_jour_sans_mesure_reste_vide()
    {
        var index = new[] { H(0, 9, 102, min: 100) };
        var puissance = new[] { H(0, 9, 900, max: 3200), H(0, 18, 1200, max: 2500) };

        var jours = EnergyDays.FromHours(index, puissance, Paris, J1, J1.AddDays(1));

        Assert.Equal(2, jours[0].Kwh!.Value, 6);
        Assert.Equal(3200, jours[0].PeakPower);
        Assert.Equal(new EnergyDay(J1.AddDays(1), null, null), jours[1]);
    }
}
