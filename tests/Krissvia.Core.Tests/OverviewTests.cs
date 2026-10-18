using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Tests;

internal sealed class EtatsEnMemoire : IEntityStateReader
{
    public Dictionary<string, EntityStateSnapshot> Etats { get; } = new(StringComparer.Ordinal);

    public void Fixer(string id, object valeur, DateTimeOffset a, bool disponible = true) =>
        Etats[id] = new EntityStateSnapshot(JsonSerializer.SerializeToElement(valeur), a, disponible);

    public EntityStateSnapshot? Get(string entityId) => Etats.GetValueOrDefault(entityId);
}

public class SensorOverviewTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static RegisteredEntity E(string id, EntityKind kind, string? classe, string appareil, bool active = true) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = kind, DisplayName = id,
        DeviceClass = classe, Enabled = active,
    };

    private static RegisteredDevice D(string id, string? piece) => new()
    {
        DeviceId = id, IntegrationId = "p", UniqueId = id, DisplayName = id, Room = piece,
    };

    [Fact]
    public void La_moyenne_d_une_piece_ne_compte_que_ses_sondes_a_jour()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.a", 19.0, Maintenant.AddMinutes(-5));
        etats.Fixer("sensor.b", 20.0, Maintenant.AddMinutes(-10));
        etats.Fixer("sensor.vieille", 30.0, Maintenant.AddMinutes(-61));
        etats.Fixer("sensor.morte", 30.0, Maintenant, disponible: false);

        var vue = SensorOverview.Build(
            [
                E("sensor.a", EntityKind.Sensor, "temperature", "d1"), E("sensor.b", EntityKind.Sensor, "temperature", "d2"),
                E("sensor.vieille", EntityKind.Sensor, "temperature", "d3"), E("sensor.morte", EntityKind.Sensor, "temperature", "d4"),
            ],
            [D("d1", "Salon"), D("d2", "salon "), D("d3", "Salon"), D("d4", "Salon")], etats, Maintenant);

        var salon = Assert.Single(vue.Rooms);
        Assert.Equal(19.5, salon.Average);
        Assert.Equal(2, salon.Active);
        Assert.Equal(4, salon.Total);
        Assert.Equal(2, salon.Unreliable);
        Assert.Equal(SensorHealth.Stale, vue.Sensors.Single(s => s.Entity.EntityId == "sensor.vieille").Health);
        Assert.Equal(SensorHealth.Offline, vue.Sensors.Single(s => s.Entity.EntityId == "sensor.morte").Health);
    }

    [Fact]
    public void Une_piece_sans_sonde_a_jour_n_a_pas_de_moyenne_plutot_qu_une_fausse()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.a", 19.0, Maintenant.AddHours(-3));

        var salon = Assert.Single(SensorOverview.Build([E("sensor.a", EntityKind.Sensor, "temperature", "d1")], [D("d1", "Salon")], etats, Maintenant).Rooms);

        Assert.Null(salon.Average);
        Assert.Equal(0, salon.Active);
    }

    [Fact]
    public void Humidite_et_batterie_viennent_du_meme_appareil()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.t", 19.0, Maintenant);
        etats.Fixer("sensor.h", 55.5, Maintenant);
        etats.Fixer("sensor.bat", 87.0, Maintenant);
        etats.Fixer("sensor.h_autre", 99.0, Maintenant);

        var sonde = Assert.Single(SensorOverview.Build(
            [
                // L'humidité d'un autre appareil vient d'abord : la prendre serait l'erreur à éviter.
                E("sensor.h_autre", EntityKind.Sensor, "humidity", "d2"),
                E("sensor.t", EntityKind.Sensor, "temperature", "d1"), E("sensor.h", EntityKind.Sensor, "humidity", "d1"),
                E("sensor.bat", EntityKind.Sensor, "battery", "d1"),
            ], [D("d1", "Salon"), D("d2", "Cave")], etats, Maintenant).Sensors);

        Assert.Equal(55.5, sonde.Humidity);
        Assert.Equal(87, sonde.Battery);
    }

    [Fact]
    public void Une_sonde_sans_piece_est_listee_en_dernier_mais_n_a_pas_de_moyenne()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.sans", 19.0, Maintenant);
        etats.Fixer("sensor.cave", 12.0, Maintenant);

        var vue = SensorOverview.Build(
            [E("sensor.sans", EntityKind.Sensor, "temperature", "d1"), E("sensor.cave", EntityKind.Sensor, "temperature", "d2")],
            [D("d1", null), D("d2", "Cave")], etats, Maintenant);

        Assert.Equal(["Cave"], vue.Rooms.Select(r => r.Room));
        Assert.Equal(["sensor.cave", "sensor.sans"], vue.Sensors.Select(s => s.Entity.EntityId));
    }

    [Fact]
    public void Une_sonde_desactivee_ou_jamais_entendue()
    {
        var vue = SensorOverview.Build(
            [E("sensor.off", EntityKind.Sensor, "temperature", "d1", active: false), E("sensor.muette", EntityKind.Sensor, "temperature", "d1")],
            [D("d1", "Salon")], new EtatsEnMemoire(), Maintenant);

        var muette = Assert.Single(vue.Sensors);
        Assert.Equal(SensorHealth.NeverReceived, muette.Health);
        Assert.Null(muette.Temperature);
    }
}

public class ActuatorOverviewTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static RegisteredEntity E(string id, EntityKind kind, string appareil) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = kind, DisplayName = id,
    };

    private static RegisteredDevice D(string id, string? piece) => new()
    {
        DeviceId = id, IntegrationId = "p", UniqueId = id, DisplayName = id, Room = piece,
    };

    private static IReadOnlyList<ActuatorRoom> Construire(EtatsEnMemoire etats, Func<string, bool>? deleste = null) => ActuatorOverview.Build(
        [
            E("switch.prise", EntityKind.Switch, "d1"), E("light.lampe", EntityKind.Light, "d1"),
            E("switch.radiateur", EntityKind.Switch, "d2"), E("switch.cumulus", EntityKind.Switch, "d3"),
            E("switch.injoignable", EntityKind.Switch, "d4"), E("sensor.t", EntityKind.Sensor, "d1"),
        ],
        [D("d1", "Salon"), D("d2", "Salon"), D("d3", "Cave"), D("d4", null)],
        etats,
        new Dictionary<string, string> { ["switch.radiateur"] = "Salon" },
        new Dictionary<string, string> { ["switch.cumulus"] = "Heures creuses" },
        deleste ?? (_ => false));

    private static EtatsEnMemoire Etats()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("switch.prise", false, T);
        etats.Fixer("light.lampe", new { on = true, brightness = 80 }, T);
        etats.Fixer("switch.radiateur", false, T);
        etats.Fixer("switch.cumulus", false, T);
        etats.Fixer("switch.injoignable", false, T, disponible: false);
        return etats;
    }

    [Fact]
    public void Interrupteurs_et_lumieres_par_piece_avec_ce_qui_les_pilote()
    {
        var pieces = Construire(Etats());

        Assert.Equal(["Cave", "Salon", null], pieces.Select(p => p.Room));
        var salon = pieces.Single(p => p.Room == "Salon").Actuators;
        Assert.Equal(["light.lampe", "switch.prise", "switch.radiateur"], salon.Select(a => a.Entity.EntityId));
        Assert.True(salon[0].On);
        Assert.Equal("Salon", salon[2].DrivenBy);
        Assert.False(salon[2].Controllable);
        Assert.Equal("Heures creuses", pieces[0].Actuators.Single().ScheduledBy);
    }

    [Fact]
    public void Tout_allumer_epargne_radiateurs_injoignables_et_deja_allumes()
    {
        var cibles = ActuatorOverview.Bulk(Construire(Etats()), on: true);

        Assert.Equal(["switch.cumulus", "switch.prise"], cibles.Select(a => a.Entity.EntityId).Order());
    }

    [Fact]
    public void Tout_allumer_laisse_un_appareil_deleste_mais_tout_eteindre_le_prend()
    {
        var pieces = Construire(Etats(), id => id == "switch.prise");

        Assert.DoesNotContain(ActuatorOverview.Bulk(pieces, on: true), a => a.Entity.EntityId == "switch.prise");

        var etats = Etats();
        etats.Fixer("switch.prise", true, T);
        Assert.Contains(ActuatorOverview.Bulk(Construire(etats, id => id == "switch.prise"), on: false), a => a.Entity.EntityId == "switch.prise");
    }

    [Fact]
    public void Tout_eteindre_ne_touche_que_ce_qui_est_allume_et_jamais_un_radiateur()
    {
        var etats = Etats();
        etats.Fixer("switch.radiateur", true, T);

        Assert.Equal(["light.lampe"], ActuatorOverview.Bulk(Construire(etats), on: false).Select(a => a.Entity.EntityId));
    }

    [Fact]
    public void Un_appareil_jamais_entendu_n_a_pas_d_etat_et_n_est_pas_commandable()
    {
        var etats = Etats();
        etats.Etats.Remove("switch.prise");

        var prise = Construire(etats).SelectMany(p => p.Actuators).Single(a => a.Entity.EntityId == "switch.prise");

        Assert.Null(prise.On);
        Assert.False(prise.Controllable);
    }
}
