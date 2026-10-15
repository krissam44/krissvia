using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Configuration du délestage : vérifiée, persistée, appliquée ; et la mesure fournie au délesteur.</summary>
public class LoadSheddingServiceTests
{
    private const string Linky = "sensor.linky_power";
    private const string Pince = "sensor.pince_power";
    private const string Cumulus = "switch.cumulus";
    private const string Radiateur = "switch.radiateur_salon";
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class Magasin : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(Documents.Where(kv => kv.Key.Item1 == collection)
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
        public FakeTimeProvider Horloge { get; } = new(T0);
        public Magasin Magasin { get; } = new();
        public FakeStateReader Etats { get; } = new();
        public ScriptedCommander Commandes { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public ThermostatInhibitor Inhibiteur { get; }
        public ThermostatService Thermostats { get; }
        public LoadShedder Delesteur { get; }
        public LoadSheddingService Service { get; }

        public Banc()
        {
            Inhibiteur = new ThermostatInhibitor(Commandes);
            var moteur = new ThermostatEngine(Etats, Registre, Commandes, Inhibiteur, Horloge);
            Thermostats = new ThermostatService(Magasin, Registre, moteur);
            Delesteur = new LoadShedder(Commandes, Inhibiteur, Horloge);
            Service = Nouveau();

            static DeviceDescriptor Appareil(string id, string nom, EntityKind famille, string? classe = null) => new()
            {
                UniqueId = id, Name = nom,
                Entities = [new EntityDescriptor
                {
                    UniqueId = id + ":e", Kind = famille, DeviceClass = classe,
                    Commands = famille == EntityKind.Switch ? [StandardCommands.TurnOn, StandardCommands.TurnOff] : [],
                }],
            };

            Registre.RegisterAsync("test",
            [
                Appareil("linky", "Linky", EntityKind.Sensor, DeviceClasses.Power),
                Appareil("pince", "Pince", EntityKind.Sensor, DeviceClasses.Power),
                Appareil("cumulus", "Cumulus", EntityKind.Switch),
                Appareil("rad", "Radiateur salon", EntityKind.Switch),
                Appareil("sonde", "Sonde salon", EntityKind.Sensor, DeviceClasses.Temperature),
            ]).GetAwaiter().GetResult();
        }

        /// <summary>Un service neuf sur le même stockage : le redémarrage du serveur.</summary>
        public LoadSheddingService Nouveau() => new(Magasin, Registre, Etats, Thermostats, Delesteur);
    }

    private static LoadSheddingConfiguration Config() => new()
    {
        Enabled = true,
        SubscriptionKva = 6,
        PowerEntityId = Linky,
        Devices =
        [
            new SheddableDeviceConfiguration { Id = "cumulus", Name = "Chauffe-eau", Priority = 1, NominalPower = 2000, SwitchEntityIds = [Cumulus] },
        ],
    };

    [Fact]
    public async Task Une_configuration_enregistree_deleste_au_dessus_du_seuil()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config());
        b.Etats.Set(Linky, 5200.0, T0);

        var tick = await b.Delesteur.TickAsync(b.Service.ReadPower());
        b.Service.RecordTick(b.Service.ReadPower(), tick, T0);

        Assert.Equal(["cumulus"], tick.Shed);
        Assert.Equal(["cumulus"], b.Service.Status.ShedDevices);
        Assert.Equal(4800, b.Service.Status.AlertThreshold);
        Assert.Contains(b.Commandes.Sent, s => s.EntityId == Cumulus && s.Command == StandardCommands.TurnOff && s.Priority == CommandPriority.Safety);
    }

    [Fact]
    public async Task Une_pince_en_watts_est_majoree_avant_comparaison()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { PowerEntityId = Pince, PowerQuality = PowerQuality.Active, ActiveToApparentFactor = 1.10 });
        b.Etats.Set(Pince, 4500.0, T0);

        Assert.Equal(4950, b.Service.ReadPower()!.Value.Value, 6);
    }

    [Fact]
    public async Task La_somme_des_equipements_peut_servir_de_source()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with
        {
            SourceKind = PowerSourceKind.EquipmentSum,
            PowerEntityId = null,
            MeteredEquipment = [new MeteredEquipmentConfiguration { SwitchEntityId = Radiateur, NominalPower = 1500 }],
        });
        b.Etats.Set(Radiateur, true, T0);

        Assert.Equal(1500, b.Service.ReadPower()!.Value.Value);
    }

    [Fact]
    public async Task La_configuration_survit_a_un_redemarrage()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { SubscriptionKva = 12, AlertPercent = 85 });

        var apres = b.Nouveau();
        await apres.LoadAsync();

        Assert.Equal(12, apres.Configuration.SubscriptionKva);
        Assert.Equal(10200, apres.Status.AlertThreshold);
        Assert.Equal("cumulus", Assert.Single(apres.Configuration.Devices).Id);
        Assert.Null(apres.LoadError);
    }

    [Fact]
    public async Task Un_document_illisible_laisse_le_delesteur_desactive_et_le_dit()
    {
        var b = new Banc();
        b.Magasin.Documents[(LoadSheddingService.Collection, LoadSheddingService.DocumentId)] = "{pas du json";

        await b.Service.LoadAsync();

        Assert.False(b.Service.Configuration.Enabled);
        Assert.NotNull(b.Service.LoadError);
    }

    public static TheoryData<string, Func<LoadSheddingConfiguration, LoadSheddingConfiguration>> Invalides => new()
    {
        { "n'existe pas", c => c with { PowerEntityId = "sensor.inconnu" } },
        { "pas un Sensor", c => c with { PowerEntityId = Cumulus } },
        { "obligatoire", c => c with { PowerEntityId = null } },
        { "aucun switch", c => c with { Devices = [c.Devices[0] with { SwitchEntityIds = [] }] } },
        { "pas un Switch", c => c with { Devices = [c.Devices[0] with { SwitchEntityIds = [Linky] }] } },
        { "thermostat « salon » n'existe pas", c => c with { Devices = [c.Devices[0] with { ThermostatId = "salon" }] } },
        { "même identifiant", c => c with { Devices = [c.Devices[0], c.Devices[0]] } },
        { "positive", c => c with { Devices = [c.Devices[0] with { NominalPower = 0 }] } },
        { "priorité", c => c with { Devices = [c.Devices[0] with { Priority = 0 }] } },
        { "restauration", c => c with { RestorePercent = 90 } },
        { "majoration", c => c with { ActiveToApparentFactor = 0.9 } },
    };

    [Theory]
    [MemberData(nameof(Invalides))]
    public async Task Une_configuration_inutilisable_est_refusee_sans_rien_changer(
        string message, Func<LoadSheddingConfiguration, LoadSheddingConfiguration> abimer)
    {
        var b = new Banc();

        var ex = await Assert.ThrowsAsync<LoadSheddingValidationException>(() => b.Service.SaveAsync(abimer(Config())));

        Assert.Contains(message, ex.Message);
        Assert.Empty(b.Magasin.Documents);
        Assert.False(b.Service.Configuration.Enabled);
    }

    [Fact]
    public async Task Un_equipement_peut_viser_un_thermostat_existant()
    {
        var b = new Banc();
        await b.Thermostats.SaveAsync(new ThermostatSettings
        {
            Id = "salon", TemperatureEntityId = "sensor.sonde_salon_temperature", SwitchEntityIds = [Radiateur],
        });

        var c = await b.Service.SaveAsync(Config() with
        {
            Devices = [new SheddableDeviceConfiguration { Id = "salon", Priority = 5, NominalPower = 1500, SwitchEntityIds = [Radiateur], ThermostatId = "salon" }],
        });
        b.Etats.Set(Linky, 7000.0, T0);
        await b.Delesteur.TickAsync(b.Service.ReadPower());

        Assert.Equal("salon", c.Devices[0].ThermostatId);
        Assert.Contains(InhibitionReasons.Delestage, b.Inhibiteur.GetReasons("salon"));
    }
}
