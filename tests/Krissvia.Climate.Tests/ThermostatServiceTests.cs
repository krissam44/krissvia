using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>La configuration des thermostats : vérifiée au registre, persistée, appliquée au moteur.</summary>
public class ThermostatServiceTests
{
    private const string Sonde = "sensor.sonde_salon_temperature";
    private const string Radiateur = "switch.radiateur_salon";
    private const string Fenetre = "binary_sensor.fenetre_salon_contact";

    /// <summary>Le stockage en mémoire : sa version SQLite est testée dans Krissvia.Persistence.Tests.</summary>
    private sealed class MagasinEnMemoire : IConfigurationStore
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
        public MagasinEnMemoire Magasin { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public FakeStateReader Etats { get; } = new();
        public ScriptedCommander Commandes { get; } = new();
        public ThermostatInhibitor Inhibiteur { get; }
        public ThermostatEngine Moteur { get; }
        public ThermostatService Service { get; }

        public Banc()
        {
            Inhibiteur = new ThermostatInhibitor(Commandes);
            Moteur = new ThermostatEngine(Etats, Registre, Commandes, Inhibiteur, new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)));
            Service = new ThermostatService(Magasin, Registre, Moteur);
            Registre.RegisterAsync("test",
            [
                new DeviceDescriptor
                {
                    UniqueId = "sonde", Name = "Sonde salon",
                    Entities = [new EntityDescriptor { UniqueId = "sonde:t", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature }],
                },
                new DeviceDescriptor
                {
                    UniqueId = "rad", Name = "Radiateur salon",
                    Entities = [new EntityDescriptor { UniqueId = "rad:s", Kind = EntityKind.Switch, Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff] }],
                },
                new DeviceDescriptor
                {
                    UniqueId = "fen", Name = "Fenetre salon",
                    Entities = [new EntityDescriptor { UniqueId = "fen:c", Kind = EntityKind.BinarySensor, DeviceClass = DeviceClasses.Opening, Name = "contact" }],
                },
            ]).GetAwaiter().GetResult();
        }

        /// <summary>Un autre service sur le même stockage : le redémarrage du serveur.</summary>
        public ThermostatService Redemarrer() => new(Magasin, Registre, Moteur);
    }

    private static ThermostatSettings Salon() => new()
    {
        Id = "salon", Name = "Salon", TemperatureEntityId = Sonde, SwitchEntityIds = [Radiateur],
        OpeningEntityId = Fenetre, ManualMode = ThermostatMode.Comfort, ScheduleActive = true,
        Schedule = new WeeklySchedule
        {
            Days = new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>
            {
                [DayOfWeek.Monday] = [new ScheduleSlot(TimeSpan.FromHours(6), TimeSpan.FromHours(8.5), ThermostatMode.Comfort)],
                [DayOfWeek.Saturday] = [new ScheduleSlot(TimeSpan.FromHours(22), TimeSpan.FromHours(7), ThermostatMode.Comfort2)],
            },
            DefaultMode = ThermostatMode.Eco,
        },
        Tpi = new TpiSettings { Kp = 0.7, CycleSeconds = 900 },
    };

    [Fact]
    public async Task Un_thermostat_enregistre_est_applique_au_moteur()
    {
        var b = new Banc();

        await b.Service.SaveAsync(Salon());
        await b.Moteur.TickAsync();

        Assert.NotNull(b.Moteur.GetSnapshot("salon"));
    }

    [Fact]
    public async Task La_configuration_survit_a_un_redemarrage_planning_compris()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon());

        var apres = b.Redemarrer();
        await apres.LoadAsync();

        var relu = Assert.Single(apres.List());
        Assert.Equal(TimeSpan.FromHours(8.5), relu.Schedule.Days[DayOfWeek.Monday][0].End);
        Assert.Equal(ThermostatMode.Comfort2, relu.Schedule.Days[DayOfWeek.Saturday][0].Mode);
        Assert.Equal(900, relu.Tpi.CycleSeconds);
        Assert.Equal([Radiateur], relu.SwitchEntityIds);
        Assert.Empty(apres.LoadErrors);
    }

    [Fact]
    public async Task Le_json_enregistre_est_lisible_par_un_humain()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon());

        var json = b.Magasin.Documents[(ThermostatService.Collection, "salon")];

        Assert.Contains("\"manual_mode\":\"comfort\"", json);
        Assert.Contains("\"temperature_entity_id\":\"sensor.sonde_salon_temperature\"", json);
        Assert.Contains("\"monday\":[{\"start\":\"06:00:00\",\"end\":\"08:30:00\",\"mode\":\"comfort\"}]", json);
    }

    [Theory]
    [InlineData("sensor.inexistant", Radiateur, "n'existe pas")]
    [InlineData(Radiateur, Radiateur, "pas un Sensor")]
    [InlineData(Sonde, Sonde, "pas un Switch")]
    public async Task Une_reference_inconnue_ou_de_la_mauvaise_famille_est_refusee(string sonde, string radiateur, string message)
    {
        var b = new Banc();

        var ex = await Assert.ThrowsAsync<ThermostatValidationException>(() =>
            b.Service.SaveAsync(Salon() with { TemperatureEntityId = sonde, SwitchEntityIds = [radiateur] }));

        Assert.Contains(message, ex.Message);
        Assert.Empty(b.Magasin.Documents);
    }

    [Theory]
    [InlineData("Salon")]
    [InlineData("salon principal")]
    [InlineData("")]
    public async Task Un_identifiant_hors_format_est_refuse(string id)
    {
        await Assert.ThrowsAsync<ThermostatValidationException>(() => new Banc().Service.SaveAsync(Salon() with { Id = id }));
    }

    [Fact]
    public async Task Choisir_un_mode_suspend_le_planning_et_efface_la_consigne_ajustee()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon());
        await b.Service.SetTargetAsync("salon", 21.3);

        var t = await b.Service.SetModeAsync("salon", ThermostatMode.Eco);

        Assert.Equal((ThermostatMode.Eco, false, (double?)null), (t!.ManualMode, t.ScheduleActive, t.TargetOverride));
        var relu = b.Redemarrer();
        await relu.LoadAsync();
        Assert.Equal(ThermostatMode.Eco, relu.Get("salon")!.ManualMode);
    }

    [Fact]
    public async Task Une_consigne_hors_bornes_est_refusee_et_null_revient_au_mode()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon());

        await Assert.ThrowsAsync<ThermostatValidationException>(() => b.Service.SetTargetAsync("salon", 35));
        Assert.Equal(19.5, (await b.Service.SetTargetAsync("salon", 19.5))!.TargetOverride);
        Assert.Null((await b.Service.SetTargetAsync("salon", null))!.TargetOverride);
    }

    [Fact]
    public async Task Une_consigne_sur_un_thermostat_arrete_est_refusee()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon() with { ScheduleActive = false });
        await b.Service.SetModeAsync("salon", ThermostatMode.Off);

        var ex = await Assert.ThrowsAsync<ThermostatValidationException>(() => b.Service.SetTargetAsync("salon", 24));

        Assert.Contains("mode off", ex.Message);
        Assert.Null(b.Service.Get("salon")!.TargetOverride);
        Assert.Null((await b.Service.SetTargetAsync("salon", null))!.TargetOverride);   // revenir au mode reste permis
    }

    [Fact]
    public async Task Un_thermostat_inconnu_renvoie_null()
    {
        var b = new Banc();

        Assert.Null(await b.Service.SetModeAsync("cuisine", ThermostatMode.Off));
        Assert.False(await b.Service.DeleteAsync("cuisine"));
        Assert.Empty(b.Magasin.Documents);
    }

    [Fact]
    public async Task Un_document_illisible_est_ecarte_sans_bloquer_les_autres()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon());
        b.Magasin.Documents[(ThermostatService.Collection, "abime")] = "{pas du json";

        var apres = b.Redemarrer();
        await apres.LoadAsync();

        Assert.Equal("salon", Assert.Single(apres.List()).Id);
        Assert.Contains("abime", Assert.Single(apres.LoadErrors));
    }

    [Fact]
    public async Task Supprimer_retire_du_moteur_et_de_la_base()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon());
        await b.Moteur.TickAsync();

        Assert.True(await b.Service.DeleteAsync("salon"));
        await b.Moteur.TickAsync();

        Assert.Null(b.Moteur.GetSnapshot("salon"));
        Assert.Empty(b.Magasin.Documents);
    }

    [Fact]
    public async Task Chaque_changement_accepte_est_signale_une_fois()
    {
        var b = new Banc();
        var signaux = 0;
        b.Service.Changed += () => signaux++;

        await b.Service.SaveAsync(Salon());
        await b.Service.SetModeAsync("salon", ThermostatMode.Eco);
        await b.Service.SetTargetAsync("salon", 19);
        await b.Service.SetScheduleActiveAsync("salon", true);
        await b.Service.DeleteAsync("salon");

        Assert.Equal(5, signaux);
    }

    [Fact]
    public async Task Un_changement_refuse_ou_sans_objet_n_est_pas_signale()
    {
        var b = new Banc();
        await b.Service.SaveAsync(Salon() with { ScheduleActive = false, ManualMode = ThermostatMode.Off });
        var signaux = 0;
        b.Service.Changed += () => signaux++;

        await Assert.ThrowsAsync<ThermostatValidationException>(() => b.Service.SetTargetAsync("salon", 19));
        await Assert.ThrowsAsync<ThermostatValidationException>(() => b.Service.SaveAsync(Salon() with { TemperatureEntityId = "sensor.inexistant" }));
        Assert.Null(await b.Service.SetModeAsync("inconnu", ThermostatMode.Eco));
        Assert.False(await b.Service.DeleteAsync("inconnu"));

        Assert.Equal(0, signaux);
    }

    [Fact]
    public async Task Le_signal_part_verrou_rendu_et_un_abonne_peut_relire_le_service()
    {
        var b = new Banc();
        ThermostatSettings? vu = null;
        var dejaAppele = false;
        // Un abonné qui rappelle le service pendant le signal : verrou encore tenu, ce serait un interblocage.
        b.Service.Changed += () =>
        {
            if (dejaAppele) return;
            dejaAppele = true;
            vu = b.Service.SetScheduleActiveAsync("salon", false).GetAwaiter().GetResult();
        };

        await Task.Run(() => b.Service.SaveAsync(Salon())).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(vu);
    }
}
