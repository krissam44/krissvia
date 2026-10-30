using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Mobile.Client;

namespace Krissvia.Core.Tests;

/// <summary>
/// <c>GET /api/sensors</c> tel que l'appli mobile le lit. Sans DTO commun, un champ renommé ici la laisserait afficher
/// « — » sans que rien ne casse : ce test lui fait lire ce que le cœur sérialise vraiment.
/// </summary>
public class SensorOverviewViewTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static RegisteredEntity E(string id, string classe, string appareil) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = EntityKind.Sensor, DisplayName = "Sonde " + id,
        DeviceClass = classe, Enabled = true,
    };

    private static RegisteredDevice D(string id, string? piece) => new()
    {
        DeviceId = id, IntegrationId = "p", UniqueId = id, DisplayName = id, Room = piece,
    };

    [Fact]
    public void L_appli_mobile_lit_les_moyennes_et_la_sante_des_sondes()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.a", 19.0, Maintenant.AddMinutes(-5));
        etats.Fixer("sensor.a_h", 55.0, Maintenant.AddMinutes(-5));
        etats.Fixer("sensor.a_bat", 87.0, Maintenant.AddMinutes(-5));
        etats.Fixer("sensor.vieille", 30.0, Maintenant.AddHours(-2));
        var resultat = SensorOverview.Build(
            [
                E("sensor.a", DeviceClasses.Temperature, "d1"), E("sensor.a_h", DeviceClasses.Humidity, "d1"),
                E("sensor.a_bat", DeviceClasses.Battery, "d1"), E("sensor.vieille", DeviceClasses.Temperature, "d2"),
                E("sensor.neuve", DeviceClasses.Temperature, "d3"),
            ],
            [D("d1", "Salon"), D("d2", "Salon"), D("d3", null)], etats, Maintenant);

        var json = JsonSerializer.SerializeToElement(SensorOverviewView.From(resultat), SensorOverviewView.Json);
        var vue = VueSondes.Lire(json)!;

        var salon = Assert.Single(vue.Pieces);
        Assert.Equal("Salon", salon.Nom);
        Assert.Equal(19.0, salon.Moyenne);
        Assert.Equal(1, salon.AJour);
        Assert.Equal(2, salon.Total);
        Assert.Equal(1, salon.Ecartees);

        var a = vue.Sondes.Single(s => s.EntityId == "sensor.a");
        Assert.Equal("Sonde sensor.a", a.Nom);
        Assert.Equal("Salon", a.Piece);
        Assert.Equal(19.0, a.Temperature);
        Assert.Equal(55, a.Humidite);
        Assert.Equal(87, a.Batterie);
        Assert.Equal(Maintenant.AddMinutes(-5), a.MesureeA);
        Assert.Equal("ok", a.Sante);
        Assert.Equal("stale", vue.Sondes.Single(s => s.EntityId == "sensor.vieille").Sante);
        Assert.Equal("never_received", vue.Sondes.Single(s => s.EntityId == "sensor.neuve").Sante);
        Assert.Equal((int)SensorOverview.StaleAfter.TotalMinutes, vue.MuetteApresMinutes);
    }
}
