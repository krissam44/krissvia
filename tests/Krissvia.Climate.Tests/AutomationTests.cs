using System.Net;
using System.Text.Json;
using Krissvia.Climate.Automations;
using Krissvia.Climate.Presence;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// Les automatismes : règles de déclenchement et de conditions de DomoMud (front, pas de rattrapage, donnée inconnue
/// jamais satisfaite), puis le moteur — on vérifie les ORDRES émis, comme pour les autres moteurs.
/// </summary>
public class AutomationTests
{
    // Mercredi 21 octobre 2026, 22 h 30 (le fuseau du banc est UTC).
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 22, 30, 0, TimeSpan.Zero);

    private static EntityStateSnapshot Etat(object valeur, DateTimeOffset a, bool disponible = true) =>
        new(JsonSerializer.SerializeToElement(valeur), a, disponible);

    private static EntityStateChanged Appui(string action, DateTimeOffset a, string entite = "event.bouton") =>
        new(entite, null, Etat(action, a));

    // ── Déclencheurs ─────────────────────────────────────────────────────────

    [Fact]
    public void Un_bouton_declenche_sur_son_action_ou_sur_toutes()
    {
        var simple = new AutomationTrigger { Type = TriggerType.Button, EntityId = "event.bouton", Action = "single" };
        var toutes = simple with { Action = null };

        Assert.True(AutomationRules.Fires(simple, Appui("SINGLE", T0), T0));
        Assert.False(AutomationRules.Fires(simple, Appui("double", T0), T0));
        Assert.True(AutomationRules.Fires(toutes, Appui("double", T0), T0));
        Assert.False(AutomationRules.Fires(toutes, Appui("double", T0, "event.autre"), T0));
    }

    [Fact]
    public void Un_appui_rejoue_a_la_reconnexion_ou_une_disponibilite_ne_declenche_pas()
    {
        var t = new AutomationTrigger { Type = TriggerType.Button, EntityId = "event.bouton" };

        // Un plugin qui se reconnecte republie son dernier appui, vieux d'une heure.
        Assert.False(AutomationRules.Fires(t, Appui("single", T0.AddHours(-1)), T0));

        // Même appui, même instant : seule la disponibilité a changé.
        var avant = Etat("single", T0, disponible: false);
        Assert.False(AutomationRules.Fires(t, new EntityStateChanged("event.bouton", avant, avant with { Available = true }), T0));

        // Le même appui répété (« single » deux fois) est bien deux appuis.
        Assert.True(AutomationRules.Fires(t, new EntityStateChanged("event.bouton", Etat("single", T0.AddSeconds(-5)), Etat("single", T0)), T0));
    }

    [Fact]
    public void Un_changement_d_etat_declenche_sur_le_front_choisi_jamais_sur_un_premier_etat()
    {
        var actif = new AutomationTrigger { Type = TriggerType.StateChange, EntityId = "binary_sensor.radar_occupancy" };
        EntityStateChanged Front(bool? avant, bool apres) =>
            new("binary_sensor.radar_occupancy", avant is { } a ? Etat(a, T0) : null, Etat(apres, T0));

        Assert.True(AutomationRules.Fires(actif, Front(false, true), T0));
        Assert.False(AutomationRules.Fires(actif, Front(true, false), T0));
        Assert.False(AutomationRules.Fires(actif, Front(true, true), T0));   // le radar réaffirme sa détection
        Assert.False(AutomationRules.Fires(actif, Front(null, true), T0));   // premier état après un redémarrage
        Assert.True(AutomationRules.Fires(actif with { Transition = StateTransition.Deactivated }, Front(true, false), T0));
        Assert.True(AutomationRules.Fires(actif with { Transition = StateTransition.Any }, Front(true, false), T0));
    }

    [Fact]
    public void Une_lumiere_publie_un_objet_et_reste_un_tout_ou_rien()
    {
        Assert.True(AutomationRules.Active(Etat(new { on = true, brightness = 40 }, T0)));
        Assert.False(AutomationRules.Active(Etat(new { on = false }, T0)));
        Assert.Null(AutomationRules.Active(Etat(true, T0, disponible: false)));
        Assert.Null(AutomationRules.Active(Etat(19.5, T0)));
    }

    [Fact]
    public void Un_horaire_part_a_sa_minute_et_les_jours_vides_valent_tous_les_jours()
    {
        var t = new AutomationTrigger { Type = TriggerType.Time, Time = new TimeSpan(22, 30, 0) };

        Assert.True(AutomationRules.TimeMatches(t, T0.DateTime));
        Assert.True(AutomationRules.TimeMatches(t, T0.DateTime.AddSeconds(59)));
        Assert.False(AutomationRules.TimeMatches(t, T0.DateTime.AddMinutes(1)));
        Assert.False(AutomationRules.TimeMatches(t with { Days = [DayOfWeek.Monday] }, T0.DateTime));
        Assert.True(AutomationRules.TimeMatches(t with { Days = [DayOfWeek.Wednesday] }, T0.DateTime));
    }

    // ── Conditions ───────────────────────────────────────────────────────────

    private static ConditionContext Contexte(FakeStateReader? etats = null, bool absente = false, ThermostatMode? mode = null) =>
        new(T0.DateTime, absente ? new HomeAbsenceState(true, AbsenceSource.Detection, null) : HomeAbsenceState.Home,
            id => id == "salon" ? mode : null, etats ?? new FakeStateReader());

    [Fact]
    public void Une_plage_de_nuit_passe_minuit()
    {
        Assert.True(AutomationRules.InRange(new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0), new TimeSpan(23, 0, 0)));
        Assert.True(AutomationRules.InRange(new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0), new TimeSpan(5, 59, 0)));
        Assert.False(AutomationRules.InRange(new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0), new TimeSpan(6, 0, 0)));
        Assert.False(AutomationRules.InRange(new TimeSpan(8, 0, 0), new TimeSpan(20, 0, 0), new TimeSpan(22, 30, 0)));
    }

    [Fact]
    public void Une_donnee_inconnue_ne_satisfait_jamais_une_condition_meme_inversee()
    {
        var etats = new FakeStateReader();
        etats.Set("sensor.salon", 19.0, T0);
        var froid = new AutomationCondition { Type = ConditionType.Below, EntityId = "sensor.salon", Threshold = 20 };

        Assert.True(AutomationRules.Satisfied(froid, Contexte(etats)));
        Assert.False(AutomationRules.Satisfied(froid with { Negated = true }, Contexte(etats)));
        // Sonde absente : ni « en dessous » ni « pas en dessous » — mieux vaut ne rien faire que supposer.
        Assert.False(AutomationRules.Satisfied(froid with { EntityId = "sensor.cave" }, Contexte(etats)));
        Assert.False(AutomationRules.Satisfied(froid with { EntityId = "sensor.cave", Negated = true }, Contexte(etats)));

        var mode = new AutomationCondition { Type = ConditionType.ThermostatMode, ThermostatId = "salon", Mode = ThermostatMode.Eco };
        Assert.True(AutomationRules.Satisfied(mode, Contexte(mode: ThermostatMode.Eco)));
        Assert.False(AutomationRules.Satisfied(mode with { Negated = true, ThermostatId = "disparu" }, Contexte(mode: ThermostatMode.Eco)));

        Assert.True(AutomationRules.Satisfied(new AutomationCondition { Type = ConditionType.HomeEmpty }, Contexte(absente: true)));
        Assert.Equal(ConditionType.HomeOccupied,
            AutomationRules.FirstUnmet([new AutomationCondition { Type = ConditionType.TimeRange, Start = new(22, 0, 0), End = new(6, 0, 0) },
                new AutomationCondition { Type = ConditionType.HomeOccupied }], Contexte(absente: true))!.Type);
    }

    // ── Moteur ───────────────────────────────────────────────────────────────

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

    private sealed class Foyer : IHomeAbsence
    {
        public HomeAbsenceState Current { get; set; } = HomeAbsenceState.Home;
    }

    private sealed class FauxNtfy : HttpMessageHandler
    {
        public int Recues;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Recues);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FakeStateReader Etats { get; } = new();
        public ScriptedCommander Commandes { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public Magasin Magasin { get; } = new();
        public Foyer Foyer { get; } = new();
        public FauxNtfy Ntfy { get; } = new();
        public NotificationService Notifications { get; }
        public ThermostatService Thermostats { get; }
        public AutomationEngine Moteur { get; }
        public AutomationService Service { get; }

        public Banc()
        {
            var thermoMoteur = new ThermostatEngine(Etats, Registre, Commandes, new ThermostatInhibitor(Commandes), Horloge);
            Thermostats = new ThermostatService(Magasin, Registre, thermoMoteur);
            Notifications = new NotificationService(Magasin, new HttpClient(Ntfy));
            Moteur = new AutomationEngine(Commandes, Etats, Registre, Thermostats, thermoMoteur, Notifications, Foyer, Horloge);
            Service = new AutomationService(Magasin, Registre, Thermostats, Moteur);
            Registre.RegisterAsync("test",
            [
                Appareil("Bouton", EntityKind.Event),
                Appareil("Radar", EntityKind.BinarySensor, DeviceClasses.Occupancy),
                Appareil("Lampe", EntityKind.Switch, commandes: [StandardCommands.TurnOn, StandardCommands.TurnOff]),
                Appareil("Prise", EntityKind.Switch, commandes: [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle]),
                Appareil("Sonde", EntityKind.Sensor, DeviceClasses.Temperature),
            ]).GetAwaiter().GetResult();
        }

        private static DeviceDescriptor Appareil(string nom, EntityKind famille, string? classe = null, IReadOnlyList<string>? commandes = null) => new()
        {
            UniqueId = nom, Name = nom,
            Entities = [new EntityDescriptor { UniqueId = nom, Kind = famille, DeviceClass = classe, Commands = commandes ?? [] }],
        };

        public async Task<Automation> AjouterAsync(params AutomationAction[] actions) =>
            await Service.SaveAsync(new Automation
            {
                Id = "essai", Name = "Essai",
                Trigger = new AutomationTrigger { Type = TriggerType.Button, EntityId = "event.bouton" },
                Actions = actions.Length > 0 ? actions : [new AutomationAction { Type = ActionType.TurnOn, EntityId = "switch.lampe" }],
            });

        public async Task AppuyerAsync(string action = "single")
        {
            Moteur.OnStateChanged(Appui(action, Horloge.GetUtcNow()));
            await Moteur.WhenIdleAsync();
        }

        public List<(string EntityId, string Command, string Origin)> Ordres => [.. Commandes.Envoyes];
    }

    [Fact]
    public async Task Un_appui_allume_la_lampe_au_nom_de_l_automatisme()
    {
        var b = new Banc();
        await b.AjouterAsync();

        await b.AppuyerAsync();

        Assert.Equal([("switch.lampe", StandardCommands.TurnOn, "automatisme:essai")], b.Ordres);
        Assert.Equal(RunOutcome.Done, b.Moteur.LastRun("essai")!.Outcome);
    }

    [Fact]
    public async Task Une_condition_non_remplie_n_envoie_rien_et_le_journal_le_dit()
    {
        var b = new Banc();
        var a = await b.AjouterAsync();
        await b.Service.SaveAsync(a with { Conditions = [new AutomationCondition { Type = ConditionType.HomeEmpty }] });

        await b.AppuyerAsync();

        Assert.Empty(b.Ordres);
        Assert.Equal(RunOutcome.ConditionsNotMet, b.Moteur.LastRun("essai")!.Outcome);

        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Manual, null);
        await b.AppuyerAsync();
        Assert.Single(b.Ordres);
    }

    [Fact]
    public async Task Un_automatisme_desactive_ne_part_pas()
    {
        var b = new Banc();
        await b.AjouterAsync();
        await b.Service.SetEnabledAsync("essai", false);

        await b.AppuyerAsync();

        Assert.Empty(b.Ordres);
    }

    [Fact]
    public async Task Le_delai_retarde_l_action_sans_bloquer_le_moteur()
    {
        var b = new Banc();
        await b.AjouterAsync(
            new AutomationAction { Type = ActionType.TurnOn, EntityId = "switch.lampe" },
            new AutomationAction { Type = ActionType.TurnOff, EntityId = "switch.lampe", DelaySeconds = 600 });

        b.Moteur.OnStateChanged(Appui("single", T0));
        await AttendreAsync(() => b.Ordres.Count == 1);
        b.Horloge.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        Assert.Single(b.Ordres);   // toujours allumée au bout de 5 min

        b.Horloge.Advance(TimeSpan.FromMinutes(10));
        await b.Moteur.WhenIdleAsync();
        Assert.Equal(StandardCommands.TurnOff, b.Ordres[1].Command);
    }

    [Fact]
    public async Task Une_notification_qui_echoue_n_empeche_pas_les_actions_suivantes()
    {
        var b = new Banc();   // ntfy jamais configuré : l'envoi est refusé
        await b.AjouterAsync(
            new AutomationAction { Type = ActionType.Notify, Title = "Bouton", Message = "appui" },
            new AutomationAction { Type = ActionType.TurnOn, EntityId = "switch.lampe" });

        await b.AppuyerAsync();

        Assert.Single(b.Ordres);
        var run = b.Moteur.LastRun("essai")!;
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Contains("notification", run.Detail);
        Assert.Equal(0, b.Ntfy.Recues);
    }

    [Fact]
    public async Task Basculer_utilise_toggle_s_il_existe_sinon_le_contraire_de_l_etat()
    {
        var b = new Banc();
        b.Etats.Set("switch.lampe", true, T0);
        await b.AjouterAsync(
            new AutomationAction { Type = ActionType.Toggle, EntityId = "switch.lampe" },
            new AutomationAction { Type = ActionType.Toggle, EntityId = "switch.prise" });

        await b.AppuyerAsync();

        Assert.Equal([StandardCommands.TurnOff, StandardCommands.Toggle], b.Ordres.Select(o => o.Command));
    }

    [Fact]
    public async Task Une_boucle_est_arretee_au_bout_de_dix_declenchements_par_minute()
    {
        var b = new Banc();
        await b.AjouterAsync();

        for (var i = 0; i < AutomationEngine.MaxRunsPerMinute + 3; i++) await b.AppuyerAsync();

        Assert.Equal(AutomationEngine.MaxRunsPerMinute, b.Ordres.Count);
        Assert.Equal(RunOutcome.Throttled, b.Moteur.LastRun("essai")!.Outcome);

        b.Horloge.Advance(TimeSpan.FromMinutes(1));
        await b.AppuyerAsync();
        Assert.Equal(AutomationEngine.MaxRunsPerMinute + 1, b.Ordres.Count);
    }

    [Fact]
    public async Task Un_horaire_ne_part_qu_une_fois_dans_sa_minute()
    {
        var b = new Banc();
        var a = await b.AjouterAsync();
        await b.Service.SaveAsync(a with { Trigger = new AutomationTrigger { Type = TriggerType.Time, Time = new TimeSpan(22, 31, 0) } });

        b.Moteur.Tick();
        b.Horloge.Advance(TimeSpan.FromMinutes(1));
        b.Moteur.Tick();
        b.Horloge.Advance(TimeSpan.FromSeconds(20));
        b.Moteur.Tick();
        await b.Moteur.WhenIdleAsync();

        Assert.Single(b.Ordres);
    }

    [Fact]
    public async Task Changer_le_mode_d_un_thermostat_passe_par_le_meme_chemin_que_le_bouton()
    {
        var b = new Banc();
        await b.Thermostats.SaveAsync(new ThermostatSettings
        {
            Id = "salon", TemperatureEntityId = "sensor.sonde_temperature", SwitchEntityIds = ["switch.lampe"], ScheduleActive = true,
        });
        await b.AjouterAsync(new AutomationAction { Type = ActionType.ThermostatMode, ThermostatId = "salon", Mode = ThermostatMode.FrostProtection });

        await b.AppuyerAsync();

        var t = b.Thermostats.Get("salon")!;
        Assert.Equal((ThermostatMode.FrostProtection, false), (t.ManualMode, t.ScheduleActive));
    }

    // ── Service ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Les_references_sont_verifiees_avec_leur_famille()
    {
        var b = new Banc();
        var a = new Automation
        {
            Id = "essai",
            Trigger = new AutomationTrigger { Type = TriggerType.Button, EntityId = "switch.lampe" },
            Actions = [new AutomationAction { Type = ActionType.TurnOn, EntityId = "switch.lampe" }],
        };

        await Assert.ThrowsAsync<AutomationValidationException>(() => b.Service.SaveAsync(a));
        await Assert.ThrowsAsync<AutomationValidationException>(() => b.Service.SaveAsync(a with
        {
            Trigger = new AutomationTrigger { Type = TriggerType.Button, EntityId = "event.bouton" },
            Actions = [new AutomationAction { Type = ActionType.TurnOn, EntityId = "sensor.sonde_temperature" }],
        }));
        await Assert.ThrowsAsync<AutomationValidationException>(() => b.Service.SaveAsync(a with
        {
            Trigger = new AutomationTrigger { Type = TriggerType.Button, EntityId = "event.bouton" },
            Actions = [new AutomationAction { Type = ActionType.ThermostatMode, ThermostatId = "inconnu" }],
        }));
        await Assert.ThrowsAsync<AutomationValidationException>(() => b.Service.SaveAsync(a with { Actions = [] }));
    }

    [Fact]
    public async Task Un_automatisme_enregistre_est_relu_a_l_identique()
    {
        var b = new Banc();
        var a = new Automation
        {
            Id = "nuit", Name = "Radar de nuit",
            Trigger = new AutomationTrigger { Type = TriggerType.StateChange, EntityId = "binary_sensor.radar_occupancy", Transition = StateTransition.Activated },
            Conditions = [new AutomationCondition { Type = ConditionType.TimeRange, Start = new(22, 0, 0), End = new(6, 0, 0) },
                new AutomationCondition { Type = ConditionType.Below, EntityId = "sensor.sonde_temperature", Threshold = 19.5, Negated = true }],
            Actions = [new AutomationAction { Type = ActionType.TurnOn, EntityId = "switch.lampe" },
                new AutomationAction { Type = ActionType.TurnOff, EntityId = "switch.lampe", DelaySeconds = 120 },
                new AutomationAction { Type = ActionType.Notify, Title = "Radar", Message = "passage" }],
        };
        await b.Service.SaveAsync(a);

        var relu = new AutomationService(b.Magasin, b.Registre, b.Thermostats, b.Moteur);
        await relu.LoadAsync();

        var r = relu.Get("nuit")!;
        Assert.Empty(relu.LoadErrors);
        Assert.Equal(JsonSerializer.Serialize(a, ThermostatService.Json), JsonSerializer.Serialize(r, ThermostatService.Json));
        Assert.Contains("\"type\":\"state_change\"", b.Magasin.Documents[(AutomationService.Collection, "nuit")]);
    }

    private static async Task AttendreAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
