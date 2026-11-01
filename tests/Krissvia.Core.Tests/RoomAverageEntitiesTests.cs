using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

/// <summary>Les moyennes par pièce publiées en sondes, pour qu'un thermostat puisse réguler dessus.</summary>
public class RoomAverageEntitiesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Ecrivain : IEntityStateWriter
    {
        public List<(string Id, double? Valeur, DateTimeOffset? Quand)> Ecrits { get; } = [];
        public void Update(string entityId, JsonElement value, DateTimeOffset? measuredAt = null) => Ecrits.Add((entityId, value.GetDouble(), measuredAt));
        public void SetAvailability(string entityId, bool available) => Ecrits.Add((entityId, null, null));
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public EtatsEnMemoire Etats { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public Ecrivain Ecrivain { get; } = new();
        public RoomAverageEntities Moyennes { get; }

        public Banc()
        {
            Moyennes = new RoomAverageEntities(Registre, Etats, Ecrivain, Horloge);
            Registre.RegisterAsync("zigbee",
            [
                Sonde("s1", "Sonde salon 1", humidite: true),
                Sonde("s2", "Sonde salon 2", humidite: false),
            ]).GetAwaiter().GetResult();
            foreach (var d in Registre.ListDevicesAsync().GetAwaiter().GetResult())
                Registre.SetRoomAsync(d.DeviceId, "Salon").GetAwaiter().GetResult();
        }

        private static DeviceDescriptor Sonde(string id, string nom, bool humidite) => new()
        {
            UniqueId = id, Name = nom,
            Entities =
            [
                new EntityDescriptor { UniqueId = id + ":t", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature, Unit = "°C" },
                .. humidite ? [new EntityDescriptor { UniqueId = id + ":h", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Humidity, Unit = "%" }] : Array.Empty<EntityDescriptor>(),
            ],
        };

        public string Id(string uniqueId) => Registre.Loaded.Single(e => e.UniqueId == uniqueId).EntityId;
    }

    [Fact]
    public async Task La_moyenne_d_une_piece_devient_une_sonde_avec_l_instant_moyen_de_ses_mesures()
    {
        var b = new Banc();
        b.Etats.Fixer(b.Id("s1:t"), 19.0, T0.AddMinutes(-5));
        b.Etats.Fixer(b.Id("s1:h"), 58.0, T0.AddMinutes(-5));
        b.Etats.Fixer(b.Id("s2:t"), 20.0, T0.AddMinutes(-2));

        await b.Moyennes.UpdateAsync();

        var temperature = b.Registre.Loaded.Single(e => e.UniqueId == RoomAverageEntities.DeviceId("Salon") + ":temperature");
        Assert.Equal(("sensor.moyenne_salon_temperature", DeviceClasses.Temperature), (temperature.EntityId, temperature.DeviceClass));
        // Pas l'instant le plus récent (− 2 min) : avec plusieurs sondes, il y en a toujours une qui vient de parler.
        Assert.Contains((temperature.EntityId, 19.5, T0.AddMinutes(-3.5)), b.Ecrivain.Ecrits);
        Assert.Contains(("sensor.moyenne_salon_humidite", 58.0, T0.AddMinutes(-3.5)), b.Ecrivain.Ecrits);
    }

    [Fact]
    public async Task Rien_n_est_republie_sans_changement_mais_une_mesure_plus_recente_l_est_meme_a_valeur_egale()
    {
        var b = new Banc();
        b.Etats.Fixer(b.Id("s1:t"), 19.0, T0);
        await b.Moyennes.UpdateAsync();
        await b.Moyennes.UpdateAsync();
        Assert.Single(b.Ecrivain.Ecrits, e => e.Id == "sensor.moyenne_salon_temperature");

        // Même valeur, mesure plus récente : sans republication, le failsafe du thermostat croirait la sonde muette.
        b.Horloge.Advance(TimeSpan.FromMinutes(1));
        b.Etats.Fixer(b.Id("s1:t"), 19.0, b.Horloge.GetUtcNow());
        await b.Moyennes.UpdateAsync();
        Assert.Equal(2, b.Ecrivain.Ecrits.Count(e => e.Id == "sensor.moyenne_salon_temperature"));
    }

    [Fact]
    public async Task Plus_aucune_sonde_a_jour_la_moyenne_devient_indisponible_une_fois()
    {
        var b = new Banc();
        b.Etats.Fixer(b.Id("s1:t"), 19.0, T0);
        await b.Moyennes.UpdateAsync();

        b.Horloge.Advance(SensorOverview.StaleAfter + TimeSpan.FromMinutes(1));
        await b.Moyennes.UpdateAsync();
        await b.Moyennes.UpdateAsync();

        Assert.Equal([19.0, null], b.Ecrivain.Ecrits.Where(e => e.Id == "sensor.moyenne_salon_temperature").Select(e => e.Valeur));
    }

    [Fact]
    public async Task Les_moyennes_publiees_n_entrent_ni_dans_l_ecran_sondes_ni_dans_les_moyennes()
    {
        var b = new Banc();
        b.Etats.Fixer(b.Id("s1:t"), 19.0, T0);
        await b.Moyennes.UpdateAsync();
        b.Etats.Fixer("sensor.moyenne_salon_temperature", 30.0, T0);

        var vue = SensorOverview.Build(b.Registre.Loaded, await b.Registre.ListDevicesAsync(), b.Etats, T0);

        Assert.DoesNotContain(vue.Sensors, s => s.Entity.IntegrationId == RoomAverageEntities.IntegrationId);
        Assert.Equal(19.0, Assert.Single(vue.Rooms).Average);
    }
}
