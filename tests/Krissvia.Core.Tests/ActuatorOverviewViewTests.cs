using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Mobile.Client;

namespace Krissvia.Core.Tests;

/// <summary><c>GET /api/actuators</c> tel que l'appli mobile le lit : ce que le cœur sérialise vraiment.</summary>
public class ActuatorOverviewViewTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static RegisteredEntity E(string id, EntityKind kind, string appareil, string? usage = null) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = kind, DisplayName = "Nom " + id,
        Usage = usage, Enabled = true,
    };

    [Fact]
    public void L_appli_mobile_lit_les_actionneurs_et_ce_qui_les_pilote()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("light.salon", true, Maintenant);
        etats.Fixer("switch.radiateur", false, Maintenant);
        etats.Fixer("switch.cumulus", false, Maintenant);
        var pieces = ActuatorOverview.Build(
            [E("light.salon", EntityKind.Light, "d1"), E("switch.radiateur", EntityKind.Switch, "d1", EntityUsages.Radiator), E("switch.cumulus", EntityKind.Switch, "d2")],
            [new RegisteredDevice { DeviceId = "d1", IntegrationId = "p", UniqueId = "d1", DisplayName = "d1", Room = "Salon" },
             new RegisteredDevice { DeviceId = "d2", IntegrationId = "p", UniqueId = "d2", DisplayName = "d2" }],
            etats,
            new Dictionary<string, string> { ["switch.radiateur"] = "Thermostat salon" },
            new Dictionary<string, string> { ["switch.cumulus"] = "Heures creuses" },
            id => id == "switch.cumulus");

        var json = JsonSerializer.SerializeToElement(ActuatorOverviewView.From(pieces), ActuatorOverviewView.Json);
        var vue = VueActionneurs.Lire(json)!;

        Assert.Equal(["Salon", null], vue.Pieces.Select(p => p.Nom));
        var lumiere = vue.Pieces[0].Actionneurs.Single(a => a.EntityId == "light.salon");
        Assert.Equal("Nom light.salon", lumiere.Nom);
        Assert.Equal(EntityUsages.Lighting, lumiere.Usage);
        Assert.True(lumiere.Allume);
        Assert.True(lumiere.Disponible);
        Assert.True(lumiere.Commandable);

        var radiateur = vue.Pieces[0].Actionneurs.Single(a => a.EntityId == "switch.radiateur");
        Assert.Equal("Thermostat salon", radiateur.PiloteePar);
        Assert.False(radiateur.Commandable);
        Assert.Equal(EntityUsages.Radiator, radiateur.Usage);

        var cumulus = Assert.Single(vue.Pieces[1].Actionneurs);
        Assert.Equal("Heures creuses", cumulus.Programmation);
        Assert.True(cumulus.Deleste);
        Assert.False(cumulus.Allume);
    }
}
