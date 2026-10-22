using Krissvia.Climate.Thermostats;

namespace Krissvia.Climate.Tests;

/// <summary>
/// Auto-TPI : une pièce simulée, dont on connaît le gain de chauffe et les déperditions, doit être reconnue ; et le
/// réglage qui en découle doit être celui que la théorie prévoit, borné.
/// </summary>
public class ModeleThermiqueTests
{
    /// <summary>
    /// Trois jours d'une pièce <c>dT/dt = g·u − k·(T − Text) + c</c>, à la minute : extérieur qui oscille sur la journée,
    /// consigne 19 °C la nuit et 21 °C le jour, chauffe en cycles TPI de 10 min, sonde arrondie au dixième.
    /// </summary>
    private static List<ThermalInterval> Piece(double g, double k, double c, int jours = 3, Func<double, double>? regulation = null)
    {
        var intervalles = new List<ThermalInterval>();
        var t = 19.0;
        double debut = Math.Round(t, 1), sommeU = 0, sommeEcart = 0;
        var minutes = 0;
        double part = 0;
        for (var m = 0; m < jours * 24 * 60; m++)
        {
            var heure = m / 60.0;
            var exterieur = 6 + 5 * Math.Sin(2 * Math.PI * (heure - 9) / 24);
            var consigne = heure % 24 is >= 7 and < 22 ? 21.0 : 19.0;
            if (m % 10 == 0) part = regulation?.Invoke(heure) ?? Math.Clamp(0.6 * (consigne - t) + 0.03 * (consigne - exterieur), 0, 1);
            var u = m % 10 < part * 10 ? 1.0 : 0.0;

            t += (g * u - k * (t - exterieur) + c) / 60.0;
            sommeU += u;
            sommeEcart += t - exterieur;
            if (++minutes == 30)
            {
                var fin = Math.Round(t, 1);
                intervalles.Add(new ThermalInterval(0.5, debut, fin, sommeU / 30, sommeEcart / 30));
                (debut, sommeU, sommeEcart, minutes) = (fin, 0, 0, 0);
            }
        }

        return intervalles;
    }

    [Fact]
    public void Une_piece_simulee_est_reconnue_gain_deperditions_et_apports()
    {
        var m = ThermalModel.Identify(Piece(g: 2.0, k: 0.05, c: 0.15));

        Assert.True(m.Usable, m.Refusal);
        Assert.InRange(m.Gain, 1.8, 2.2);
        Assert.InRange(m.LossRate, 0.04, 0.06);
        Assert.InRange(m.TimeConstantHours, 16, 25);
        Assert.True(m.R2 > 0.5);
        Assert.Equal(144, m.Intervals);
    }

    [Fact]
    public void Le_reglage_suit_la_theorie_ki_egal_k_sur_g_et_kp_depend_du_type_de_chauffage()
    {
        var m = new ThermalIdentification(Gain: 2.0, LossRate: 0.05, FreeGain: 0.1, R2: 0.9, Intervals: 200, ObservedHours: 100, Refusal: null);

        var electrique = ThermalModel.Derive(m, HeatingType.Electric);
        Assert.Equal((0.5, 0.025, 0.025), (electrique.Kp, electrique.Ki, electrique.Kint));
        Assert.Empty(electrique.Notes);

        var eau = ThermalModel.Derive(m, HeatingType.Water);
        Assert.Equal(0.25, eau.Kp);                              // λ = 2 h : deux fois plus doux
        Assert.Equal(0.025, eau.Ki);                             // l'équilibre ne dépend pas du type de chauffage
    }

    [Fact]
    public void Un_reglage_hors_bornes_est_ramene_et_on_dit_pourquoi()
    {
        var plancher = ThermalModel.Derive(new ThermalIdentification(2.0, 0.05, 0, 0.9, 200, 100, null), HeatingType.Floor);
        Assert.Equal(ThermalModel.KpMin, plancher.Kp);           // 1/(2×4) = 0,125
        Assert.Contains(plancher.Notes, n => n.StartsWith("Kp", StringComparison.Ordinal));

        var sousDimensionne = ThermalModel.Derive(new ThermalIdentification(0.3, 0.05, 0, 0.9, 200, 100, null), HeatingType.Electric);
        Assert.Equal(ThermalModel.KiMax, sousDimensionne.Ki);    // 0,05/0,3 = 0,167
        Assert.Contains(sousDimensionne.Notes, n => n.Contains("sous-dimensionné", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => ThermalModel.Derive(new ThermalIdentification(0, 0, 0, 0, 0, 0, "trop tôt"), HeatingType.Electric));
    }

    [Fact]
    public void Trop_peu_de_donnees_ou_une_chauffe_qui_ne_varie_pas_ne_donnent_pas_de_modele()
    {
        Assert.Contains("nécessaires", ThermalModel.Identify(Piece(2.0, 0.05, 0.15)[..20]).Refusal);
        Assert.Contains("chauffe n'a presque pas varié", ThermalModel.Identify(Piece(2.0, 0.05, 0.15, regulation: _ => 0.3)).Refusal);
    }

    [Fact]
    public void Un_ecart_avec_l_exterieur_qui_ne_varie_pas_ne_permet_pas_de_mesurer_les_deperditions()
    {
        var plat = Piece(2.0, 0.05, 0.15).Select(i => i with { IndoorOutdoorGap = 12 + i.Duty * 0.5 }).ToList();

        Assert.Contains("écart avec l'extérieur", ThermalModel.Identify(plat).Refusal);
    }

    /// <summary>
    /// Les chauffes et les écarts d'une pièce réelle, mais une évolution de l'ambiante calculée exactement depuis (g, k, c),
    /// plus un bruit déterministe : pour viser une garde précise sans qu'une autre refuse avant elle.
    /// </summary>
    private static List<ThermalInterval> Synthetique(double g, double k, double c, double bruit = 0) =>
        [.. Piece(2.0, 0.05, 0.15).Select((i, n) => i with
        {
            IndoorEnd = i.IndoorStart + (g * i.Duty - k * i.IndoorOutdoorGap + c) * i.Hours + bruit * Math.Sin(n * 1.7),
        })];

    [Fact]
    public void Le_modele_retrouve_exactement_des_coefficients_connus()
    {
        var m = ThermalModel.Identify(Synthetique(1.5, 0.08, 0.3));

        Assert.Equal((1.5, 0.08, 0.3), (Math.Round(m.Gain, 6), Math.Round(m.LossRate, 6), Math.Round(m.FreeGain, 6)));
        Assert.Equal(1, m.R2, 6);
    }

    [Fact]
    public void Un_radiateur_qui_refroidit_la_piece_n_est_pas_cru()
    {
        // Le radiateur suivi n'est pas celui de la pièce, et sa chauffe coïncide avec un refroidissement.
        Assert.Contains("gain de chauffe", ThermalModel.Identify(Synthetique(-1.0, 0.05, 0.8)).Refusal);
    }

    [Fact]
    public void Des_deperditions_negatives_ne_sont_pas_crues()
    {
        // Une sonde « extérieure » au soleil : plus elle est chaude, plus la pièce perdrait — à l'envers.
        Assert.Contains("déperditions", ThermalModel.Identify(Synthetique(2.0, -0.03, -0.3)).Refusal);
    }

    [Fact]
    public void Une_sonde_trop_bruitee_donne_un_modele_peu_fiable()
    {
        var m = ThermalModel.Identify(Synthetique(2.0, 0.05, 0.15, bruit: 0.6));

        Assert.Contains("peu fiable", m.Refusal);
        Assert.True(m.Gain > 0 && m.LossRate > 0);   // c'est bien le R² qui refuse, pas un signe aberrant
    }
}
