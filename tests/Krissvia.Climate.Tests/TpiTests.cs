using Krissvia.Climate.Thermostats;

namespace Krissvia.Climate.Tests;

/// <summary>Le calcul d'un cycle TPI, pur : valeurs par défaut de DomoMud (Kp 0,6, cycle 600 s, minima 30 s).</summary>
public class TpiTests
{
    private static readonly TpiSettings Defaut = new();
    private static readonly TpiSettings SansIntegrale = new() { Kint = 0 };

    [Fact]
    public void La_puissance_est_proportionnelle_a_l_ecart()
    {
        var cycle = Tpi.ComputeCycle(SansIntegrale, cible: 20, interieur: 19, exterieur: null, integrale: 0, TimeSpan.Zero);

        Assert.Equal(0.6, cycle.Power, 6);
        Assert.Equal(360, cycle.OnSeconds);
    }

    [Fact]
    public void La_puissance_est_bornee_entre_zero_et_un()
    {
        Assert.Equal(1.0, Tpi.ComputeCycle(SansIntegrale, 20, 15, null, 0, TimeSpan.Zero).Power);
        Assert.Equal(0.0, Tpi.ComputeCycle(SansIntegrale, 20, 22, null, 0, TimeSpan.Zero).Power);
    }

    [Fact]
    public void Le_froid_exterieur_ajoute_de_la_puissance()
    {
        var sans = Tpi.ComputeCycle(SansIntegrale, 20, 19.5, exterieur: null, 0, TimeSpan.Zero);
        var avec = Tpi.ComputeCycle(SansIntegrale, 20, 19.5, exterieur: 0, 0, TimeSpan.Zero);

        Assert.Equal(sans.Power + 0.01 * 20, avec.Power, 6);
    }

    [Fact]
    public void Sous_la_consigne_un_temps_de_chauffe_trop_court_est_porte_au_minimum()
    {
        // 0,6 × 0,05 = 3 % → 18 s, sous les 30 s minimales : on chauffe 30 s, sinon la consigne n'est jamais atteinte.
        var cycle = Tpi.ComputeCycle(SansIntegrale, 20, 19.95, null, 0, TimeSpan.Zero);

        Assert.Equal(30, cycle.OnSeconds);
    }

    [Fact]
    public void A_la_consigne_un_micro_cycle_est_annule()
    {
        // Le feed-forward seul donne 0,01 × 2 = 2 % (12 s) alors que la pièce est déjà à la consigne.
        var cycle = Tpi.ComputeCycle(SansIntegrale, 20, 20, exterieur: 18, 0, TimeSpan.Zero);

        Assert.Equal(0, cycle.OnSeconds);
        Assert.Equal(0, cycle.Power);
    }

    [Fact]
    public void Un_arret_trop_court_devient_un_cycle_plein()
    {
        // 0,6 × 1,62 = 97,2 % → 583 s de marche, 17 s d'arrêt : moins des 30 s minimales.
        var cycle = Tpi.ComputeCycle(SansIntegrale, 20, 18.38, null, 0, TimeSpan.Zero);

        Assert.Equal(600, cycle.OnSeconds);
        Assert.Equal(1.0, cycle.Power);
    }

    [Fact]
    public void L_integrale_cumule_l_erreur_au_prorata_du_temps()
    {
        var cycle = Tpi.ComputeCycle(Defaut, 20, 19, null, integrale: 0.1, TimeSpan.FromMinutes(5));

        Assert.Equal(0.1 + 1 * 5.0 / 60, cycle.Integral, 6);
    }

    [Fact]
    public void L_integrale_est_bornee()
    {
        var cycle = Tpi.ComputeCycle(Defaut, 20, 10, null, integrale: 1.95, TimeSpan.FromMinutes(10));

        Assert.Equal(2.0, cycle.Integral);
    }

    [Fact]
    public void Au_dessus_de_la_consigne_l_integrale_se_dissipe()
    {
        var cycle = Tpi.ComputeCycle(Defaut, 20, 20.5, null, integrale: 1.0, TimeSpan.FromMinutes(6));

        Assert.Equal((1.0 - 0.5 * 0.1) * 0.5, cycle.Integral, 6);
    }
}
