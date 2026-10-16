using System.Text.Json;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Les thermostats vus comme des entités climate : déclaration, état publié, ordres.</summary>
public class ThermostatEntitiesTests
{
    private const string Sonde = "sensor.sonde_salon_temperature";
    private const string Humidite = "sensor.sonde_salon_humidity";
    private const string Radiateur = "switch.radiateur_salon";
    private const string Entite = "climate.salon";

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

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) =>
            Task.FromResult(_documents.Remove((collection, id)));
    }

    private sealed class SansPersistance : IEntityStateRepository
    {
        public Task<IReadOnlyList<PersistedEntityState>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PersistedEntityState>>([]);

        public Task SaveAsync(IReadOnlyList<PersistedEntityState> states, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Compte les publications : un état identique ne doit pas repartir dans le flux à chaque passage.</summary>
    private sealed class EcrivainCompteur(EntityStateStore etats) : IEntityStateWriter
    {
        public List<string> Publications { get; } = [];

        public void Update(string entityId, JsonElement value, DateTimeOffset? measuredAt = null)
        {
            Publications.Add(entityId);
            etats.Update(entityId, value, measuredAt);
        }

        public void SetAvailability(string entityId, bool available) => etats.SetAvailability(entityId, available);
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public EntityStateStore Etats { get; }
        public EcrivainCompteur Ecrivain { get; }
        public ThermostatEngine Moteur { get; }
        public ThermostatService Service { get; }
        public ThermostatEntities Entites { get; }

        public Banc()
        {
            Etats = new EntityStateStore(new SansPersistance(), Horloge);
            Ecrivain = new EcrivainCompteur(Etats);
            var commandes = new ScriptedCommander();
            Moteur = new ThermostatEngine(Etats, Registre, commandes, new ThermostatInhibitor(commandes), Horloge);
            Service = new ThermostatService(new Magasin(), Registre, Moteur);
            Entites = new ThermostatEntities(Service, Moteur, Registre, Etats, Ecrivain);
            Registre.RegisterAsync("test",
            [
                new DeviceDescriptor
                {
                    UniqueId = "sonde", Name = "Sonde salon",
                    Entities =
                    [
                        new EntityDescriptor { UniqueId = "sonde:t", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature },
                        new EntityDescriptor { UniqueId = "sonde:h", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Humidity },
                    ],
                },
                new DeviceDescriptor
                {
                    UniqueId = "rad", Name = "Radiateur salon",
                    Entities = [new EntityDescriptor { UniqueId = "rad:s", Kind = EntityKind.Switch, Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff] }],
                },
            ]).GetAwaiter().GetResult();
            Registre.ListAsync().GetAwaiter().GetResult();   // remplit Loaded, comme au démarrage
        }

        public Task AjouterAsync(string nom = "Salon", ThermostatMode mode = ThermostatMode.Comfort) =>
            Service.SaveAsync(new ThermostatSettings
            {
                Id = "salon", Name = nom, TemperatureEntityId = Sonde, SwitchEntityIds = [Radiateur], ManualMode = mode,
            });

        public async Task PasserAsync()
        {
            await Moteur.TickAsync();
            await Entites.PublishAsync();
        }

        public JsonElement Etat() => Etats.Get(Entite)!.Value;

        public Task<CommandOutcome> OrdonnerAsync(string commande, object args) =>
            Entites.HandleAsync(Registre.Loaded.Single(e => e.EntityId == Entite), new EntityCommand(Entite, commande)
            {
                Args = JsonSerializer.SerializeToElement(args),
            }, CancellationToken.None);
    }

    [Fact]
    public async Task Le_thermostat_est_declare_en_entite_climate_avec_ses_modes_et_ses_commandes()
    {
        var b = new Banc();
        await b.AjouterAsync();

        await b.Entites.PublishAsync();

        var e = await b.Registre.GetAsync(Entite);
        Assert.NotNull(e);
        Assert.Equal(EntityKind.Climate, e.Kind);
        Assert.Equal(ThermostatEntities.IntegrationId, e.IntegrationId);
        Assert.Equal([StandardCommands.SetMode, StandardCommands.SetTarget, StandardCommands.AdjustTarget], e.Commands);
        using var extra = JsonDocument.Parse(e.ExtraJson!);
        Assert.Equal(["comfort", "comfort1", "comfort2", "eco", "frost_protection", "off"],
            extra.RootElement.GetProperty("modes").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("Confort −1", extra.RootElement.GetProperty("modes")[1].GetProperty("label").GetString());
        Assert.Equal(1, extra.RootElement.GetProperty("radiators").GetInt32());
    }

    [Fact]
    public async Task Rien_n_est_publie_avant_le_premier_passage_du_moteur()
    {
        var b = new Banc();
        await b.AjouterAsync();

        await b.Entites.PublishAsync();

        Assert.Null(b.Etats.Get(Entite));
    }

    [Fact]
    public async Task L_etat_publie_porte_mesures_consigne_mode_et_statut_calcule_par_le_coeur()
    {
        var b = new Banc();
        await b.AjouterAsync();
        b.Etats.Update(Sonde, JsonSerializer.SerializeToElement(18.4));
        b.Etats.Update(Humidite, JsonSerializer.SerializeToElement(74.0));

        await b.PasserAsync();

        var etat = b.Etat();
        Assert.Equal(18.4, etat.GetProperty("temperature").GetDouble());
        Assert.Equal(74, etat.GetProperty("humidity").GetDouble());
        Assert.Equal(20, etat.GetProperty("target").GetDouble());
        Assert.Equal("comfort", etat.GetProperty("mode").GetString());
        Assert.True(etat.GetProperty("heating").GetBoolean());
        Assert.Equal("heating", etat.GetProperty("status").GetString());
        Assert.Equal("Chauffe", etat.GetProperty("status_label").GetString());
    }

    [Fact]
    public async Task Un_etat_inchange_n_est_pas_republie()
    {
        var b = new Banc();
        await b.AjouterAsync();
        b.Etats.Update(Sonde, JsonSerializer.SerializeToElement(18.4));

        await b.PasserAsync();
        await b.PasserAsync();

        Assert.Single(b.Ecrivain.Publications);
    }

    [Fact]
    public async Task Choisir_un_mode_passe_par_le_service_et_suspend_le_planning()
    {
        var b = new Banc();
        await b.Service.SaveAsync(new ThermostatSettings
        {
            Id = "salon", Name = "Salon", TemperatureEntityId = Sonde, SwitchEntityIds = [Radiateur], ScheduleActive = true,
        });
        await b.Entites.PublishAsync();

        Assert.Equal(CommandOutcome.Accepted, await b.OrdonnerAsync(StandardCommands.SetMode, new { mode = "eco" }));

        var t = b.Service.Get("salon")!;
        Assert.Equal(ThermostatMode.Eco, t.ManualMode);
        Assert.False(t.ScheduleActive);
    }

    [Fact]
    public async Task Deux_pas_rapproches_font_plus_un_degre_a_partir_de_la_consigne_suivie()
    {
        var b = new Banc();
        await b.AjouterAsync();
        b.Etats.Update(Sonde, JsonSerializer.SerializeToElement(18.4));
        await b.PasserAsync();

        await b.OrdonnerAsync(StandardCommands.AdjustTarget, new { delta = 0.5 });
        await b.OrdonnerAsync(StandardCommands.AdjustTarget, new { delta = 0.5 });

        Assert.Equal(21, b.Service.Get("salon")!.TargetOverride);
    }

    [Fact]
    public async Task Le_pas_est_borne()
    {
        var b = new Banc();
        await b.AjouterAsync();
        await b.Entites.PublishAsync();
        await b.OrdonnerAsync(StandardCommands.SetTarget, new { temperature = 29.8 });

        await b.OrdonnerAsync(StandardCommands.AdjustTarget, new { delta = 0.5 });

        Assert.Equal(ThermostatService.MaxTarget, b.Service.Get("salon")!.TargetOverride);
    }

    [Fact]
    public async Task Une_consigne_sur_un_thermostat_arrete_est_refusee_avec_son_motif()
    {
        var b = new Banc();
        await b.AjouterAsync(mode: ThermostatMode.Off);
        await b.Entites.PublishAsync();

        var refus = await Assert.ThrowsAsync<ThermostatValidationException>(
            () => b.OrdonnerAsync(StandardCommands.AdjustTarget, new { delta = 0.5 }));
        Assert.Contains("arrêté", refus.Message);
    }

    [Fact]
    public async Task Des_arguments_faux_sont_refuses()
    {
        var b = new Banc();
        await b.AjouterAsync();
        await b.Entites.PublishAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => b.OrdonnerAsync(StandardCommands.SetMode, new { mode = "absence" }));
        await Assert.ThrowsAsync<ArgumentException>(() => b.OrdonnerAsync(StandardCommands.AdjustTarget, new { pas = 1 }));
        Assert.Equal(ThermostatMode.Comfort, b.Service.Get("salon")!.ManualMode);
    }

    [Fact]
    public async Task Le_nom_de_l_entite_suit_celui_du_thermostat()
    {
        var b = new Banc();
        await b.AjouterAsync("Salon");
        await b.Entites.PublishAsync();

        await b.AjouterAsync("Séjour");
        await b.Entites.PublishAsync();

        Assert.Equal("Séjour", (await b.Registre.GetAsync(Entite))!.DisplayName);
    }

    [Fact]
    public async Task Un_thermostat_supprime_laisse_une_entite_manquante()
    {
        var b = new Banc();
        await b.AjouterAsync();
        await b.Entites.PublishAsync();

        await b.Service.DeleteAsync("salon");
        await b.Entites.PublishAsync();

        Assert.True((await b.Registre.GetAsync(Entite))!.Missing);
    }
}
