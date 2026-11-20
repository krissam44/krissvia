using Krissvia.Core.History;

namespace Krissvia.Core.Tests;

/// <summary>La consommation par semaine, du lundi au dimanche, tirée des jours.</summary>
public class EnergyWeeksTests
{
    private static EnergyDay J(int annee, int mois, int jour, double? kwh) => new(new DateOnly(annee, mois, jour), kwh, null);

    [Fact]
    public void Une_semaine_va_du_lundi_au_dimanche()
    {
        // Dimanche 15/11/2026, lundi 16/11 … dimanche 22/11, lundi 23/11.
        var semaines = EnergyWeeks.FromDays(
        [
            J(2026, 11, 15, 26), J(2026, 11, 16, 21), J(2026, 11, 17, 13.5), J(2026, 11, 22, 1.5), J(2026, 11, 23, 4),
        ]);

        Assert.Equal([new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)], semaines.Select(s => s.Monday));
        Assert.Equal(36, semaines[1].Kwh!.Value, 6);
        Assert.Equal(3, semaines[1].MeasuredDays);
        Assert.Equal(new DateOnly(2026, 11, 22), semaines[1].Sunday);
    }

    [Fact]
    public void Une_semaine_sans_mesure_n_est_pas_a_zero()
    {
        var semaine = Assert.Single(EnergyWeeks.FromDays([J(2026, 11, 16, null), J(2026, 11, 17, null)]));

        Assert.Null(semaine.Kwh);
        Assert.Equal(0, semaine.MeasuredDays);
    }

    [Fact]
    public void Le_numero_est_celui_de_l_ISO_8601()
    {
        // Le 1er janvier 2027 est un vendredi : il appartient à la semaine 53 de 2026, qui commence le lundi 28/12.
        var semaine = Assert.Single(EnergyWeeks.FromDays([J(2027, 1, 1, 5)]));

        Assert.Equal(new DateOnly(2026, 12, 28), semaine.Monday);
        Assert.Equal(53, semaine.Number);
        Assert.Equal(47, EnergyWeeks.FromDays([J(2026, 11, 20, 1)])[0].Number);
        // La semaine du lundi 4 janvier 2027 est la 1re (ISO) ; « la semaine du 1er janvier » en ferait la 2e.
        Assert.Equal(1, EnergyWeeks.FromDays([J(2027, 1, 6, 1)])[0].Number);
    }
}
