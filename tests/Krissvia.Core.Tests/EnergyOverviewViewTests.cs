using System.Text.Json;
using Krissvia.Core.History;
using Krissvia.Mobile.Client;

namespace Krissvia.Core.Tests;

/// <summary><c>GET /api/energy</c> tel que l'appli mobile le lit : ce que le cœur sérialise vraiment.</summary>
public class EnergyOverviewViewTests
{
    [Fact]
    public void L_appli_mobile_lit_les_compteurs_et_leurs_jours()
    {
        var vue = new EnergyOverviewView(0.25,
        [
            new EnergyMeterView("Linky", "sensor.linky_index_total", 1840, "VA", 231.5, 6.4, 1.6,
            [
                new EnergyDayView(new DateOnly(2026, 10, 29), null, null, null),
                new EnergyDayView(new DateOnly(2026, 10, 30), 6.4, 1.6, 3900),
            ],
            [
                new EnergyPowerPointView(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.FromHours(1)), 110, 90, 180),
                new EnergyPowerPointView(new DateTimeOffset(2026, 10, 30, 0, 5, 0, TimeSpan.FromHours(1)), null, null, null),
            ]),
        ]);

        var lu = VueEnergie.Lire(JsonSerializer.SerializeToElement(vue, EnergyOverviewView.Json))!;

        Assert.Equal(0.25, lu.Prix);
        var linky = Assert.Single(lu.Compteurs);
        Assert.Equal("Linky", linky.Nom);
        Assert.Equal(1840, linky.Puissance);
        Assert.Equal("VA", linky.UnitePuissance);
        Assert.Equal(231.5, linky.Tension);
        Assert.Equal(6.4, linky.AujourdhuiKwh);
        Assert.Equal(1.6, linky.AujourdhuiCout);
        Assert.Equal([new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30)], linky.Jours.Select(j => j.Jour));
        Assert.Null(linky.Jours[0].Kwh);
        Assert.Equal(6.4, linky.Jours[1].Kwh);
        Assert.Equal(1.6, linky.Jours[1].Cout);
        Assert.Equal(3900, linky.Jours[1].Pointe);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.FromHours(1)), linky.Courbe[0].A);
        Assert.Equal((110.0, 90.0, 180.0), (linky.Courbe[0].Moyenne!.Value, linky.Courbe[0].Min!.Value, linky.Courbe[0].Max!.Value));
        // Une case sans mesure reste sans valeur : la courbe se coupe au lieu de tomber à zéro.
        Assert.Null(linky.Courbe[1].Moyenne);
    }
}
