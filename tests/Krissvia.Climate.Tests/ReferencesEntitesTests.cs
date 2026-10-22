using Krissvia.Climate.Alarm;
using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Ce que visent les modules du cœur, pour que la supervision puisse dire qu'une référence est cassée.</summary>
public class ReferencesEntitesTests
{
    private sealed class Magasin : IConfigurationStore
    {
        private readonly Dictionary<(string, string), string> _documents = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(_documents.Where(kv => kv.Key.Item1 == collection)
                .Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            _documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) => Task.FromResult(_documents.Remove((collection, id)));
    }

    private static DeviceDescriptor Appareil(string id, string nom, EntityKind genre, string? classe = null) =>
        new() { UniqueId = id, Name = nom, Entities = [new EntityDescriptor { UniqueId = $"{id}:e", Kind = genre, DeviceClass = classe }] };

    [Fact]
    public async Task Thermostats_programmations_delestage_et_alarme_declarent_ce_qu_ils_visent()
    {
        var magasin = new Magasin();
        var registre = new EntityRegistry(new InMemoryEntityRepository());
        await registre.RegisterAsync("z2m",
        [
            Appareil("sonde", "Sonde salon", EntityKind.Sensor, DeviceClasses.Temperature),
            Appareil("rad", "Radiateur salon", EntityKind.Switch),
            Appareil("prise", "Prise bureau", EntityKind.Switch),
            Appareil("cumulus", "Cumulus", EntityKind.Switch),
            Appareil("linky", "Linky", EntityKind.Sensor, DeviceClasses.ApparentPower),
            Appareil("porte", "Porte", EntityKind.BinarySensor, DeviceClasses.Opening),
            Appareil("sirene", "Sirène", EntityKind.Switch),
        ]);
        string Id(string appareil) => registre.Loaded.Single(e => e.UniqueId == $"{appareil}:e").EntityId;
        await registre.ListAsync();

        var bus = new ScriptedCommander();
        var horloge = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        var inhibiteur = new ThermostatInhibitor(bus);
        var thermostats = new ThermostatService(magasin, registre, new ThermostatEngine(new FakeStateReader(), registre, bus, inhibiteur, horloge));
        var delesteur = new LoadShedder(bus, inhibiteur, horloge);
        var programmations = new DeviceScheduleService(magasin, registre, thermostats,
            new DeviceScheduleEngine(bus, delesteur, new DesiredSwitchStates(), horloge));
        var delestage = new LoadSheddingService(magasin, registre, new FakeStateReader(), thermostats, delesteur, horloge);
        var alarme = new AlarmService(magasin, horloge);

        await thermostats.SaveAsync(new ThermostatSettings { Id = "salon", Name = "Salon", TemperatureEntityId = Id("sonde"), SwitchEntityIds = [Id("rad")] });
        await programmations.SaveAsync(new DeviceSchedule { Id = "bureau", Name = "Bureau", SwitchEntityIds = [Id("prise")] });
        await programmations.SaveAsync(new DeviceSchedule { Id = "eteinte", Enabled = false, SwitchEntityIds = [Id("cumulus")] });
        await delestage.SaveAsync(new LoadSheddingConfiguration
        {
            Enabled = true,
            Sources = [new PowerSourceConfiguration { Name = "Linky", PowerEntityId = Id("linky") }, new PowerSourceConfiguration { Name = "Somme", Kind = PowerSourceKind.EquipmentSum, PowerEntityId = "sensor.reste" }],
            Devices = [new SheddableDeviceConfiguration { Id = "cumulus", Name = "Cumulus", NominalPower = 2000, SwitchEntityIds = [Id("cumulus")] }],
        });
        await alarme.SaveSettingsAsync(new AlarmSettings { Enabled = true, Sensors = [new AlarmSensor(Id("porte"))], Sirens = [Id("sirene")] });

        var references = await new ClimateEntityReferences(thermostats, programmations, delestage, alarme).GetReferencesAsync();

        Assert.Equal(
        [
            ("Alarme", "capteur", Id("porte")), ("Alarme", "sirène", Id("sirene")),
            ("Délestage", "puissance", Id("linky")), ("Délestage Cumulus", "à couper", Id("cumulus")),
            ("Programmation Bureau", "équipement", Id("prise")),   // la programmation désactivée ne pilote rien : pas vérifiée
            ("Thermostat Salon", "radiateur", Id("rad")), ("Thermostat Salon", "sonde", Id("sonde")),
        ], references.Select(r => (r.OwnerName!, r.Role, r.EntityId)).Order());
    }
}
