using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
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

            // La somme des équipements additionne ce qui est déclaré dans le registre.
            Registre.SetNominalPowerAsync(Radiateur, 1500).GetAwaiter().GetResult();
        }

        /// <summary>Un service neuf sur le même stockage : le redémarrage du serveur.</summary>
        public LoadSheddingService Nouveau() => new(Magasin, Registre, Etats, Thermostats, Delesteur, Horloge);
    }

    private static PowerSourceConfiguration Linky_() => new() { Name = "Linky", PowerEntityId = Linky };

    private static PowerSourceConfiguration Pince_() =>
        new() { Name = "Pince", PowerEntityId = Pince, PowerQuality = PowerQuality.Active, ActiveToApparentFactor = 1.10 };

    private static PowerSourceConfiguration Somme_() => new()
    {
        Name = "Somme", Kind = PowerSourceKind.EquipmentSum,
    };

    private static LoadSheddingConfiguration Config() => new()
    {
        Enabled = true,
        SubscriptionKva = 6,
        Sources = [Linky_()],
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

        var tick = await b.Delesteur.TickAsync(b.Service.ReadPower(T0));
        b.Service.RecordTick(b.Service.ReadPower(T0), tick, T0);

        Assert.Equal(["cumulus"], tick.Shed);
        Assert.Equal(["cumulus"], b.Service.Status.ShedDevices);
        Assert.Equal(4800, b.Service.Status.AlertThreshold);
        Assert.Contains(b.Commandes.Sent, s => s.EntityId == Cumulus && s.Command == StandardCommands.TurnOff && s.Priority == CommandPriority.Safety);
    }

    [Fact]
    public async Task Une_pince_en_watts_est_majoree_avant_comparaison()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { Sources = [Pince_()] });
        b.Etats.Set(Pince, 4500.0, T0);

        Assert.Equal(4950, b.Service.ReadPower(T0)!.Value.Value, 6);
    }

    [Fact]
    public async Task La_somme_des_equipements_peut_servir_de_source()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { Sources = [Somme_()] });
        b.Etats.Set(Radiateur, true, T0);

        Assert.Equal(1500, b.Service.ReadPower(T0)!.Value.Value);
    }

    // ── Enchaînement des sources ─────────────────────────────────────────────

    [Fact]
    public async Task La_premiere_source_fraiche_l_emporte()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { Sources = [Linky_(), Pince_(), Somme_()] });
        b.Etats.Set(Linky, 3000.0, T0);
        b.Etats.Set(Pince, 2000.0, T0);

        Assert.Equal(3000, b.Service.ReadPower(T0)!.Value.Value);
        Assert.Equal("Linky", b.Service.ActiveSource);
    }

    [Fact]
    public async Task Un_Linky_muet_passe_la_main_a_la_pince_majoree()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { Sources = [Linky_(), Pince_(), Somme_()] });
        b.Etats.Set(Linky, 3000.0, T0.AddSeconds(-30));   // module TIC débranché depuis 30 s
        b.Etats.Set(Pince, 2000.0, T0);

        Assert.Equal(2200, b.Service.ReadPower(T0)!.Value.Value, 6);
        Assert.Equal("Pince", b.Service.ActiveSource);
    }

    [Fact]
    public async Task Sans_Linky_ni_pince_la_somme_des_equipements_prend_le_relais()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { Sources = [Linky_(), Pince_(), Somme_()] });
        b.Etats.Set(Linky, 3000.0, T0, disponible: false);
        b.Etats.Set(Radiateur, true, T0);

        Assert.Equal(1500, b.Service.ReadPower(T0)!.Value.Value);
        Assert.Equal("Somme", b.Service.ActiveSource);
    }

    [Fact]
    public async Task Aucune_source_fraiche_pas_de_mesure_jamais_zero()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config() with { Sources = [Linky_(), Pince_()] });
        b.Etats.Set(Linky, 3000.0, T0.AddMinutes(-5));

        Assert.Null(b.Service.ReadPower(T0));
        Assert.Null(b.Service.ActiveSource);

        var tick = await b.Delesteur.TickAsync(b.Service.ReadPower(T0));
        b.Service.RecordTick(null, tick, T0);
        Assert.True(b.Service.Status.SourceUnavailable);
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
        Assert.Equal("Linky", Assert.Single(apres.Configuration.Sources).Name);
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
        { "n'existe pas", c => c with { Sources = [Linky_() with { PowerEntityId = "sensor.inconnu" }] } },
        { "pas un Sensor", c => c with { Sources = [Linky_() with { PowerEntityId = Cumulus }] } },
        { "obligatoire", c => c with { Sources = [Linky_() with { PowerEntityId = null }] } },
        { "aucune source", c => c with { Sources = [] } },
        { "aucun switch", c => c with { Devices = [c.Devices[0] with { SwitchEntityIds = [] }] } },
        { "pas un Switch", c => c with { Devices = [c.Devices[0] with { SwitchEntityIds = [Linky] }] } },
        { "thermostat « salon » n'existe pas", c => c with { Devices = [c.Devices[0] with { ThermostatId = "salon" }] } },
        { "même identifiant", c => c with { Devices = [c.Devices[0], c.Devices[0]] } },
        { "positive", c => c with { Devices = [c.Devices[0] with { NominalPower = 0 }] } },
        { "priorité", c => c with { Devices = [c.Devices[0] with { Priority = 0 }] } },
        { "restauration", c => c with { RestorePercent = 90 } },
        { "majoration", c => c with { Sources = [Pince_() with { ActiveToApparentFactor = 0.9 }] } },
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
        await b.Delesteur.TickAsync(b.Service.ReadPower(T0));

        Assert.Equal("salon", c.Devices[0].ThermostatId);
        Assert.Contains(InhibitionReasons.Delestage, b.Inhibiteur.GetReasons("salon"));
    }

    /// <summary>Note ce qui est publié, sans rien d'autre.</summary>
    private sealed class Ecrivain : Krissvia.Core.States.IEntityStateWriter
    {
        public List<(string Id, System.Text.Json.JsonElement Valeur)> Publies { get; } = [];

        public void Update(string entityId, System.Text.Json.JsonElement value, DateTimeOffset? measuredAt = null) => Publies.Add((entityId, value));

        public void SetAvailability(string entityId, bool available) { }
    }

    [Fact]
    public async Task Le_delesteur_publie_son_etat_en_entite_apres_chaque_passage_s_il_a_change()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Config());
        var ecrivain = new Ecrivain();
        var entites = new LoadSheddingEntities(b.Service, b.Registre, ecrivain);

        await entites.PublishAsync();   // pas encore de passage : déclarée, rien de publié
        var entite = Assert.Single(b.Registre.Loaded, e => e.DeviceClass == DeviceClasses.LoadShedding);
        Assert.Equal(CoreIntegrations.LoadShedding, entite.IntegrationId);
        Assert.Equal(("sensor.maison_delesteur", "Maison Délesteur"), (entite.EntityId, entite.DisplayName));
        Assert.Empty(ecrivain.Publies);

        b.Etats.Set(Linky, 5200.0, T0);
        var tick = await b.Delesteur.TickAsync(b.Service.ReadPower(T0));
        b.Service.RecordTick(b.Service.ReadPower(T0), tick, T0);
        await entites.PublishAsync();
        await entites.PublishAsync();   // rien n'a changé : pas de seconde publication

        var (id, v) = Assert.Single(ecrivain.Publies);
        Assert.Equal(entite.EntityId, id);
        Assert.Equal(5200, v.GetProperty("power").GetDouble());
        Assert.Equal(6000, v.GetProperty("subscription_va").GetDouble());
        Assert.Equal(4800, v.GetProperty("alert_threshold").GetDouble());
        Assert.True(v.GetProperty("shedding").GetBoolean());
        Assert.Equal("Chauffe-eau", v.GetProperty("shed")[0].GetProperty("name").GetString());
        Assert.Equal("Linky", v.GetProperty("source").GetString());
    }
}
