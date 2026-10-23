using Krissvia.Core.History;

namespace Krissvia.Core.Tests;

/// <summary>Ce qu'affiche une case de courbe : moyenne pondérée par le temps, trous, valeur qui court, heures consolidées.</summary>
public class HistoryCurveTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan QuartDHeure = TimeSpan.FromMinutes(15);

    private static HistoryPoint P(double minutes, double? valeur) => new("switch.radiateur", T0.AddMinutes(minutes), valeur);

    [Fact]
    public void Une_case_pese_chaque_valeur_par_le_temps_qu_elle_a_dure_pas_par_le_nombre_de_points()
    {
        // Éteint depuis une heure, allumé 5 min sur les 15 de la case : un tiers, et non la moitié des deux points.
        var points = new[] { P(-60, 0), P(5, 1), P(10, 0) };

        var c = Assert.Single(HistoryCurve.FromPoints(points, [T0], QuartDHeure, T0.AddHours(1)));

        Assert.Equal(1.0 / 3, c.Mean!.Value, 6);
        Assert.Equal((0.0, 1.0), (c.Min, c.Max));
    }

    [Fact]
    public void Un_trou_ne_compte_pas_et_une_case_sans_valeur_reste_vide()
    {
        // 18 °C pendant 5 min, puis le capteur se tait (trou) jusqu'à la case suivante, qu'il ne couvre pas du tout.
        var points = new[] { P(-1, 18), P(5, null), P(40, 20) };

        var cases = HistoryCurve.FromPoints(points, [T0, T0 + QuartDHeure, T0 + 2 * QuartDHeure], QuartDHeure, T0.AddHours(1));

        Assert.Equal(18, cases[0].Mean);   // pas 6 (18 sur 5 min, compté sur 15)
        Assert.Null(cases[1].Mean);         // la courbe s'interrompt
        Assert.Equal(20, cases[2].Mean);    // 20 de 40 à 45 min seulement
    }

    [Fact]
    public void La_derniere_valeur_court_jusqu_a_maintenant_et_pas_au_dela()
    {
        // Allumé à 0, éteint à 6 min ; « maintenant » à 9 min : la case n'a duré que 9 min, dont 6 allumé.
        var points = new[] { P(0, 1), P(6, 0) };

        var c = Assert.Single(HistoryCurve.FromPoints(points, [T0], QuartDHeure, T0.AddMinutes(9)));

        Assert.Equal(6.0 / 9, c.Mean!.Value, 6);
    }

    [Fact]
    public void La_grille_est_alignee_sur_le_pas_et_finit_sur_maintenant()
    {
        var maintenant = T0.AddMinutes(7).AddSeconds(30);
        var periode = HistoryCurve.Ranges.Single(r => r.Key == "6h");

        var grille = HistoryCurve.Grid(periode, maintenant);

        Assert.Equal(72, grille.Count);
        Assert.Equal(T0.AddMinutes(5), grille[^1]);
        Assert.Equal(TimeSpan.FromMinutes(5), grille[1] - grille[0]);
        Assert.False(periode.UsesHourly);
        Assert.True(HistoryCurve.Ranges.Single(r => r.Key == "7j").UsesHourly);
    }

    [Fact]
    public void Les_heures_consolidees_pesent_par_leur_couverture()
    {
        HistoryBucket H(int heure, double? moyenne, double couverture, double? min = null, double? max = null) =>
            new(T0.AddHours(heure), min ?? moyenne, max ?? moyenne, moyenne, moyenne, couverture);
        var heures = new[] { H(0, 10, 1), H(1, 20, 0.5, 18, 23), H(2, null, 0), H(7, 12, 1) };

        var cases = HistoryCurve.FromHours(heures, [T0, T0.AddHours(6)], TimeSpan.FromHours(6));

        Assert.Equal((10 * 1 + 20 * 0.5) / 1.5, cases[0].Mean!.Value, 6);
        Assert.Equal((10.0, 23.0), (cases[0].Min, cases[0].Max));
        Assert.Equal(12, cases[1].Mean);
        Assert.Null(HistoryCurve.FromHours([H(0, null, 0)], [T0], TimeSpan.FromHours(6))[0].Mean);
    }
}
