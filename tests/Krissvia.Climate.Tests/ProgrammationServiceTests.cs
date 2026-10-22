using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Programmations : vérifiées à l'enregistrement (un seul pilote par switch), persistées, signalées.</summary>
public class ProgrammationServiceTests
{
    private const string Prise = "switch.prise_salon";
    private const string Radiateur = "switch.radiateur_salon";
    private const string Sonde = "sensor.sonde_salon_temperature";

    private sealed class Magasin : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(Documents
                .Where(kv => kv.Key.Item1 == collection)
                .Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) =>
            Task.FromResult(Documents.Remove((collection, id)));
    }

    private sealed class Banc
    {
        public Magasin Magasin { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public ThermostatService Thermostats { get; }
        public DeviceScheduleEngine Moteur { get; }
        public DeviceScheduleService Service { get; }

        public Banc()
        {
            var bus = new ScriptedCommander();
            var horloge = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero));
            var inhibiteur = new ThermostatInhibitor(bus);
            Thermostats = new ThermostatService(Magasin, Registre, new ThermostatEngine(new FakeStateReader(), Registre, bus, inhibiteur, horloge));
            Moteur = new DeviceScheduleEngine(bus, new LoadShedder(bus, inhibiteur, horloge), new DesiredSwitchStates(), horloge);
            Service = new DeviceScheduleService(Magasin, Registre, Thermostats, Moteur);
            Registre.RegisterAsync("test",
            [
                new DeviceDescriptor { UniqueId = "prise", Name = "Prise salon", Entities = [new EntityDescriptor { UniqueId = "prise:s", Kind = EntityKind.Switch }] },
                new DeviceDescriptor { UniqueId = "rad", Name = "Radiateur salon", Entities = [new EntityDescriptor { UniqueId = "rad:s", Kind = EntityKind.Switch }] },
                new DeviceDescriptor
                {
                    UniqueId = "sonde", Name = "Sonde salon",
                    Entities = [new EntityDescriptor { UniqueId = "sonde:t", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature }],
                },
            ]).GetAwaiter().GetResult();
        }
    }

    private static DeviceSchedule Matin(string id = "matin", string interrupteur = Prise) => new()
    {
        Id = id, Name = "Matin", SwitchEntityIds = [interrupteur],
        Days = new Dictionary<DayOfWeek, IReadOnlyList<DeviceScheduleSlot>>
        {
            [DayOfWeek.Monday] = [new DeviceScheduleSlot(TimeSpan.FromHours(22), TimeSpan.FromHours(6))],
        },
    };

    [Fact]
    public async Task Une_programmation_survit_au_redemarrage()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Matin());

        var apres = new DeviceScheduleService(b.Magasin, b.Registre, b.Thermostats, b.Moteur);
        await apres.LoadAsync();

        var relue = Assert.Single(apres.List());
        Assert.Equal(TimeSpan.FromHours(6), Assert.Single(relue.Days[DayOfWeek.Monday]).End);
        Assert.True(relue.Days[DayOfWeek.Monday][0].On);
        Assert.Empty(apres.LoadErrors);
    }

    [Fact]
    public async Task Le_radiateur_d_un_thermostat_n_est_pas_programmable()
    {
        var b = new Banc();
        await b.Thermostats.SaveAsync(new ThermostatSettings { Id = "salon", TemperatureEntityId = Sonde, SwitchEntityIds = [Radiateur] });

        var ex = await Assert.ThrowsAsync<DeviceScheduleValidationException>(() => b.Service.SaveAsync(Matin(interrupteur: Radiateur)));
        Assert.Contains("thermostat", ex.Message);
    }

    [Fact]
    public async Task Un_switch_programme_ne_peut_pas_devenir_le_radiateur_d_un_thermostat()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Matin(interrupteur: Radiateur));
        var salon = new ThermostatSettings { Id = "salon", TemperatureEntityId = Sonde, SwitchEntityIds = [Radiateur] };

        var ex = await Assert.ThrowsAsync<ThermostatValidationException>(() => b.Thermostats.SaveAsync(salon));
        Assert.Contains("« Radiateur salon » est déjà piloté par la programmation « Matin »", ex.Message);
        Assert.Empty(b.Thermostats.List());

        await b.Service.DeleteAsync("matin");
        await b.Thermostats.SaveAsync(salon);   // libéré : accepté
    }

    [Fact]
    public async Task Un_switch_n_appartient_qu_a_une_programmation()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Matin());

        await Assert.ThrowsAsync<DeviceScheduleValidationException>(() => b.Service.SaveAsync(Matin(id: "soir")));
        Assert.NotNull(await b.Service.SaveAsync(Matin() with { Name = "Matin modifié" }));   // elle-même, oui
    }

    [Theory]
    [InlineData("switch.inconnu")]
    [InlineData(Sonde)]
    public async Task Seul_un_switch_existant_est_programmable(string entite)
    {
        var b = new Banc();

        await Assert.ThrowsAsync<DeviceScheduleValidationException>(() => b.Service.SaveAsync(Matin(interrupteur: entite)));
        Assert.Empty(b.Magasin.Documents);
    }

    [Fact]
    public async Task Chaque_changement_accepte_est_signale_une_fois()
    {
        var b = new Banc();
        var signaux = 0;
        b.Service.Changed += () => signaux++;

        await b.Service.SaveAsync(Matin());
        await b.Service.SetEnabledAsync("matin", false);
        await b.Service.DeleteAsync("matin");
        await b.Service.DeleteAsync("matin");

        Assert.Equal(3, signaux);
    }
}
