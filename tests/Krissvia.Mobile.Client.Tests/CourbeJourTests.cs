namespace Krissvia.Mobile.Client.Tests;

public class CourbeJourTests
{
    private static readonly DateTimeOffset Minuit = new(2026, 10, 30, 0, 0, 0, TimeSpan.FromHours(1));

    private static PointPuissance P(double heures, double? moyenne, double? min = null, double? max = null) =>
        new(Minuit.AddHours(heures), moyenne, min ?? moyenne, max ?? moyenne);

    [Fact]
    public void Le_jour_entier_est_en_abscisse_et_l_axe_monte_par_pas_ronds()
    {
        var courbe = CourbeJour.Tracer([P(0, 100), P(12, 1950, 1200, 1990)], 240, 100);

        // Midi tombe au milieu, même si la journée n'est pas finie : on voit où elle en est.
        Assert.Equal("M0,95L120,2.5", courbe.Ligne);
        Assert.Equal(["0", "1 000", "2 000"], courbe.Graduations.Select(g => g.Libelle.Replace(' ', ' ').Replace(' ', ' ')));
        Assert.Equal([100, 50, 0], courbe.Graduations.Select(g => g.Y));
    }

    [Fact]
    public void Une_case_sans_mesure_coupe_la_ligne_et_la_bande()
    {
        var courbe = CourbeJour.Tracer([P(0, 200), P(1, 300), P(2, null), P(3, 400), P(4, 500)], 240, 100);

        Assert.Equal(2, courbe.Ligne.Count(c => c == 'M'));
        Assert.Equal(2, courbe.Bande.Count(c => c == 'Z'));
    }

    [Fact]
    public void La_bande_suit_le_max_puis_revient_par_le_min()
    {
        var courbe = CourbeJour.Tracer([P(0, 500, 400, 600), P(6, 500, 300, 1000)], 240, 100);

        Assert.Equal("M0,40L60,0L60,70L0,60Z", courbe.Bande);
    }

    [Fact]
    public void Sans_mesure_il_n_y_a_rien_a_tracer()
    {
        Assert.True(CourbeJour.Tracer([], 240, 100).Vide);
        Assert.True(CourbeJour.Tracer([P(0, null), P(1, null)], 240, 100).Vide);
    }

    [Theory]
    [InlineData(650, 1000)]
    [InlineData(150, 200)]
    [InlineData(33.4, 50)]
    [InlineData(500, 500)]
    public void Le_pas_est_rond(double brut, double attendu) => Assert.Equal(attendu, CourbeJour.PasRond(brut));
}
