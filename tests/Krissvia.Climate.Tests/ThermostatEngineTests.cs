using Krissvia.Climate.Presence;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.Scheduling;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// Le moteur des thermostats. Règle reprise de DomoMud : on vérifie les ORDRES émis, jamais la croyance du
/// moteur. Les protections sont testées pièce au-dessus de la consigne — le moteur se croit à l'arrêt — et
/// doivent couper quand même (le bug de juillet 2026).
/// </summary>
public class ThermostatEngineTests
{
    private const string Sonde = "sensor.salon_temperature";
    private const string Fenetre = "binary_sensor.fenetre_salon";
    private const string Radiateur = "switch.radiateur_salon";
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FakeStateReader Etats { get; } = new();
        public ScriptedCommander Commandes { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public ThermostatInhibitor Inhibiteur { get; }
        public ThermostatEngine Moteur { get; }
        public MemoryHeatingSlopeStore Pentes { get; }
        public Foyer Foyer { get; } = new();

        public Banc(MemoryHeatingSlopeStore? pentes = null)
        {
            Pentes = pentes ?? new MemoryHeatingSlopeStore();
            Inhibiteur = new ThermostatInhibitor(Commandes);
            Moteur = new ThermostatEngine(Etats, Registre, Commandes, Inhibiteur, Horloge, Pentes, Foyer);
            Registre.RegisterAsync("test",
            [
                new DeviceDescriptor
                {
                    UniqueId = "rad", Name = "Radiateur salon",
                    Entities = [new EntityDescriptor { UniqueId = "rad:state", Kind = EntityKind.Switch, Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff] }],
                },
            ]).GetAwaiter().GetResult();
        }

        /// <summary>Confort à 20 °C, mode manuel, valeurs TPI de DomoMud.</summary>
        public static ThermostatSettings Salon(Func<ThermostatSettings, ThermostatSettings>? ajuster = null)
        {
            var t = new ThermostatSettings
            {
                Id = "salon", Name = "Salon", TemperatureEntityId = Sonde, SwitchEntityIds = [Radiateur],
                ManualMode = ThermostatMode.Comfort,
            };
            return ajuster?.Invoke(t) ?? t;
        }

        public async Task<ThermostatSnapshot> DemarrerAsync(ThermostatSettings t)
        {
            await Moteur.ConfigureAsync([t]);
            return await TickAsync();
        }

        public async Task<ThermostatSnapshot> TickAsync()
        {
            await Moteur.TickAsync();
            return Moteur.GetSnapshot("salon")!;
        }

        /// <summary>Avance l'horloge en ticks de 10 s, comme la boucle de production.</summary>
        public async Task<ThermostatSnapshot> AvancerAsync(TimeSpan duree)
        {
            for (var t = TimeSpan.Zero; t < duree; t += TimeSpan.FromSeconds(10))
            {
                Horloge.Advance(TimeSpan.FromSeconds(10));
                await Moteur.TickAsync();
            }

            return Moteur.GetSnapshot("salon")!;
        }

        public void Mesurer(double temperature) => Etats.Set(Sonde, temperature, Horloge.GetUtcNow());

        public int Ordres(string commande, CommandPriority? priorite = null) =>
            Commandes.Sent.Count(s => s.EntityId == Radiateur && s.Command == commande && (priorite is null || s.Priority == priorite));

        public void Oublier() => Commandes.Sent.Clear();
    }

    // ── Régulation ───────────────────────────────────────────────────────────

    // ── Absence ──────────────────────────────────────────────────────────────

    private sealed class Foyer : IHomeAbsence
    {
        public HomeAbsenceState Current { get; set; } = HomeAbsenceState.Home;
    }

    private static ThermostatSettings SuitAbsence(ThermostatSettings t) => t with { FollowAbsence = true };

    [Fact]
    public async Task Maison_vide_le_thermostat_qui_suit_l_absence_passe_dans_son_mode_d_absence()
    {
        var b = new Banc();
        b.Mesurer(18);
        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Detection, null);

        var s = await b.DemarrerAsync(Banc.Salon(t => SuitAbsence(t) with { TargetOverride = 21 }));

        Assert.True(s.Away);
        Assert.Equal(ThermostatMode.FrostProtection, s.Mode);
        Assert.Equal(7, s.Target);   // la consigne ajustée avant le départ ne chauffe pas la maison vide
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOn));
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
    }

    [Fact]
    public async Task Un_thermostat_qui_ne_suit_pas_l_absence_garde_son_mode()
    {
        var b = new Banc();
        b.Mesurer(18);
        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Detection, null);

        var s = await b.DemarrerAsync(Banc.Salon());

        Assert.False(s.Away);
        Assert.Equal(20, s.Target);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Pendant_l_absence_le_planning_ne_prechauffe_pas()
    {
        // Sans absence, 18,5 °C à 30 min d'un créneau Confort déclencherait le préchauffage (voir plus bas).
        var b = new Banc();
        b.Mesurer(18.5);
        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Scheduled, T0.AddDays(3));

        var s = await b.DemarrerAsync(Banc.Salon(t => SuitAbsence(AvecPlanning(t))));

        Assert.NotEqual(ThermostatStatus.Preheating, s.Status);
        Assert.Equal(7, s.Target);
    }

    [Fact]
    public async Task Une_absence_en_arret_remonte_quand_meme_avant_le_retour()
    {
        // 2 °C à gagner × 30 min/°C = 60 min ; retour dans 50 min : on chauffe vers le Confort de l'arrivée.
        var b = new Banc();
        b.Mesurer(18);
        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Scheduled, T0.AddMinutes(50));

        var s = await b.DemarrerAsync(Banc.Salon(t => SuitAbsence(t) with { AwayMode = ThermostatMode.Off }));

        Assert.Equal(ThermostatStatus.Preheating, s.Status);
        Assert.Equal(20, s.Target);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Une_absence_en_arret_reste_ferme_tant_que_le_retour_est_loin()
    {
        var b = new Banc();
        b.Mesurer(18);
        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Scheduled, T0.AddHours(5));

        var s = await b.DemarrerAsync(Banc.Salon(t => SuitAbsence(t) with { AwayMode = ThermostatMode.Off }));

        Assert.Equal(ThermostatStatus.Off, s.Status);
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOn));
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
    }

    [Fact]
    public async Task La_remontee_vise_le_mode_du_planning_a_l_heure_du_retour_sans_plafond_de_trois_heures()
    {
        // Hors-gel → Confort : 13 °C × 30 min/°C = 6 h 30. Retour à 18 h, il est 12 h : il est temps. Le planning
        // donne Confort à 18 h (créneau 12 h 30 - 23 h) : c'est sa consigne qui est visée, pas celle de midi.
        var b = new Banc();
        b.Mesurer(7);
        b.Foyer.Current = new HomeAbsenceState(true, AbsenceSource.Scheduled, T0.AddHours(6));

        var s = await b.DemarrerAsync(Banc.Salon(t => SuitAbsence(AvecPlanning(t))));

        Assert.Equal(ThermostatStatus.Preheating, s.Status);
        Assert.Equal(20, s.Target);
    }

    // ── Préchauffage anticipé ────────────────────────────────────────────────

    /// <summary>Planning : Éco (16,5) jusqu'à 12 h 30, Confort (20) ensuite. Il est 12 h (UTC = heure locale du banc).</summary>
    private static ThermostatSettings AvecPlanning(ThermostatSettings t) => t with
    {
        ScheduleActive = true,
        Schedule = new WeeklySchedule
        {
            Days = new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>
            {
                [DayOfWeek.Wednesday] = [new ScheduleSlot(new TimeSpan(12, 30, 0), TimeSpan.FromHours(23), ThermostatMode.Comfort)],
            },
            DefaultMode = ThermostatMode.Eco,
        },
    };

    [Fact]
    public async Task Le_prechauffage_vise_la_consigne_du_creneau_assez_tot_pour_l_atteindre_a_l_heure()
    {
        // 1,5 °C à gagner × 30 min/°C = 45 min, il en reste 30 : on chauffe dès maintenant vers 20 °C.
        var b = new Banc();
        b.Mesurer(18.5);

        var s = await b.DemarrerAsync(Banc.Salon(AvecPlanning));

        Assert.Equal(ThermostatStatus.Preheating, s.Status);
        Assert.Equal(20, s.Target);
        Assert.Equal(ThermostatMode.Eco, s.Mode);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Trop_tot_le_planning_suit_son_cours()
    {
        // 0,5 °C × 30 = 15 min, il en reste 30 : rien à faire encore, la consigne reste celle d'Éco.
        var b = new Banc();
        b.Mesurer(19.5);

        var s = await b.DemarrerAsync(Banc.Salon(AvecPlanning));

        Assert.NotEqual(ThermostatStatus.Preheating, s.Status);
        Assert.Equal(16.5, s.Target);
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Un_froid_annonce_pendant_la_montee_avance_le_prechauffage()
    {
        // Pente apprise 29 min/°C par 10 °C dehors ; 1 °C à gagner, 30 min avant le créneau : encore tôt par
        // 10 °C, déjà en retard par −10 °C annoncés (29 × 0,9 / 0,7 ≈ 37 min) : la prévision fait partir maintenant.
        async Task<ThermostatSnapshot> Essai(double prevu)
        {
            var pentes = new MemoryHeatingSlopeStore();
            pentes.Slopes["salon"] = new HeatingSlope(29, OutdoorReference: 10);
            var b = new Banc(pentes);
            b.Mesurer(19);
            b.Etats.Set(Prevision, OutdoorForecastTests.Meteo(T0.AddHours(-1), prevu, prevu, prevu, prevu, prevu), T0);
            return await b.DemarrerAsync(Banc.Salon(t => AvecPlanning(t) with { ForecastEntityId = Prevision }));
        }

        Assert.NotEqual(ThermostatStatus.Preheating, (await Essai(10)).Status);
        Assert.Equal(ThermostatStatus.Preheating, (await Essai(-10)).Status);
    }

    [Fact]
    public async Task La_pente_s_apprend_a_pleine_puissance_et_survit_a_un_redemarrage()
    {
        var b = new Banc();
        b.Mesurer(15);
        b.Etats.Set(Exterieur, 6.0, T0);
        var t = Banc.Salon(s => s with { OutdoorTemperatureEntityId = Exterieur });
        await b.DemarrerAsync(t);   // 5 °C sous la consigne : pleine puissance

        await b.AvancerAsync(TimeSpan.FromMinutes(10));
        b.Mesurer(15.5);
        b.Etats.Set(Exterieur, 6.0, b.Horloge.GetUtcNow());
        var s = await b.TickAsync();

        // 0,5 °C en 10 min : 20 min/°C, enregistrée avec la température dehors.
        Assert.Equal(new HeatingSlope(20, 6), s.Slope);
        Assert.Equal(new HeatingSlope(20, 6), b.Pentes.Slopes["salon"]);

        // Un autre moteur sur le même magasin (le cœur a redémarré) la retrouve.
        var apres = new Banc(b.Pentes);
        apres.Mesurer(19);
        Assert.Equal(new HeatingSlope(20, 6), (await apres.DemarrerAsync(t)).Slope);
    }

    [Fact]
    public async Task Sans_pleine_puissance_rien_n_est_appris()
    {
        var b = new Banc();
        b.Mesurer(19.8);   // 0,2 °C sous la consigne : puissance partielle
        await b.DemarrerAsync(Banc.Salon());

        await b.AvancerAsync(TimeSpan.FromMinutes(10));
        b.Mesurer(20.3);
        var s = await b.TickAsync();

        Assert.Null(s.Slope);
        Assert.Empty(b.Pentes.Slopes);
    }

    // ── Anticipation météo ───────────────────────────────────────────────────

    private const string Exterieur = "sensor.dehors_temperature";
    private const string Prevision = "sensor.vertou_meteo";

    /// <summary>Pièce à 19,5 pour 20, dehors 10 °C mesurés ; la prévision annonce 0 °C sur les deux heures.</summary>
    private static async Task<(Banc Banc, ThermostatSnapshot Etat)> FroidAnnonceAsync(
        Func<ThermostatSettings, ThermostatSettings> reglages, DateTimeOffset? releve = null)
    {
        var b = new Banc();
        b.Mesurer(19.5);
        b.Etats.Set(Exterieur, 10.0, T0);
        b.Etats.Set(Prevision, OutdoorForecastTests.Meteo(T0.AddHours(-1), 0, 0, 0, 0, 0), releve ?? T0);
        var s = await b.DemarrerAsync(Banc.Salon(t => reglages(t with { OutdoorTemperatureEntityId = Exterieur })));
        return (b, s);
    }

    [Fact]
    public async Task Un_froid_annonce_chauffe_davantage_que_la_mesure_seule()
    {
        var (_, mesure) = await FroidAnnonceAsync(t => t);
        var (_, anticipe) = await FroidAnnonceAsync(t => t with { ForecastEntityId = Prevision });

        // Feed-forward Ki × (cible − extérieur) : 0,01 × (20 − 0) au lieu de 0,01 × (20 − 10).
        Assert.Equal(0.1, anticipe.Power - mesure.Power, 3);
        Assert.Equal(0, anticipe.OutdoorForecast);
        Assert.Equal(10, anticipe.OutdoorTemperature);   // l'écran montre toujours la mesure
    }

    [Fact]
    public async Task Sans_prevision_utilisable_le_moteur_reprend_la_mesure()
    {
        var (_, mesure) = await FroidAnnonceAsync(t => t);
        var (_, eteinte) = await FroidAnnonceAsync(t => t with { ForecastEntityId = Prevision, ForecastAnticipation = false });
        var (_, vieille) = await FroidAnnonceAsync(t => t with { ForecastEntityId = Prevision }, releve: T0 - TimeSpan.FromHours(4));

        Assert.Equal(mesure.Power, eteinte.Power);
        Assert.Null(eteinte.OutdoorForecast);
        Assert.Equal(mesure.Power, vieille.Power);
        Assert.Null(vieille.OutdoorForecast);
    }

    [Fact]
    public async Task Sans_sonde_exterieure_la_prevision_suffit_au_feed_forward()
    {
        var b = new Banc();
        b.Mesurer(19.5);
        b.Etats.Set(Prevision, OutdoorForecastTests.Meteo(T0.AddHours(-1), 0, 0, 0, 0, 0), T0);

        var temoin = new Banc();
        temoin.Mesurer(19.5);
        var sansPrevision = await temoin.DemarrerAsync(Banc.Salon());

        var s = await b.DemarrerAsync(Banc.Salon(t => t with { ForecastEntityId = Prevision }));

        Assert.Null(s.OutdoorTemperature);
        Assert.Equal(0.2, s.Power - sansPrevision.Power, 3);   // Ki × (20 − 0)
        Assert.Equal(0, s.OutdoorForecast);
    }

    [Fact]
    public async Task Une_piece_froide_declenche_la_chauffe()
    {
        var b = new Banc();
        b.Mesurer(17);

        var s = await b.DemarrerAsync(Banc.Salon());

        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
        Assert.Equal(ThermostatStatus.Heating, s.Status);
        Assert.Equal(20, s.Target);
    }

    [Fact]
    public async Task Au_demarrage_l_etat_voulu_part_sans_condition()
    {
        // Après un redémarrage, le moteur ne sait rien du relais : il peut être resté allumé.
        var b = new Banc();
        b.Mesurer(20.5);

        var s = await b.DemarrerAsync(Banc.Salon());

        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
        Assert.Equal(ThermostatStatus.Idle, s.Status);
    }

    [Fact]
    public async Task Seules_les_bascules_sont_emises()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon());

        await b.AvancerAsync(TimeSpan.FromMinutes(2));

        Assert.Single(b.Commandes.Sent);
    }

    [Fact]
    public async Task Le_cycle_est_fige_une_montee_en_temperature_ne_raccourcit_pas_la_chauffe()
    {
        var b = new Banc(); // 19 °C pour 20 : 60 %, 360 s de chauffe
        b.Mesurer(19);
        await b.DemarrerAsync(Banc.Salon(t => t with { Tpi = t.Tpi with { Kint = 0 } }));

        b.Mesurer(19.9);
        await b.AvancerAsync(TimeSpan.FromSeconds(350));
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOff));

        await b.AvancerAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
    }

    [Fact]
    public async Task Un_changement_de_consigne_recalcule_le_cycle_tout_de_suite()
    {
        // 19,5 °C pour 20 : 30 %, 180 s de chauffe. Passée à 19 °C, la consigne est dépassée de 0,5 °C — sous le
        // seuil d'inertie : seul le recalcul immédiat du cycle peut arrêter la chauffe avant 180 s.
        var b = new Banc();
        b.Mesurer(19.5);
        await b.DemarrerAsync(Banc.Salon());

        await b.Moteur.ConfigureAsync([Banc.Salon(t => t with { ManualMode = ThermostatMode.Comfort1 })]);
        var s = await b.AvancerAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(19, s.Target);
        Assert.Equal(ThermostatStatus.Idle, s.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
    }

    [Fact]
    public async Task Une_consigne_ajustee_l_emporte_sur_le_mode()
    {
        var b = new Banc();
        b.Mesurer(19);

        var s = await b.DemarrerAsync(Banc.Salon(t => t with { ManualMode = ThermostatMode.Eco, TargetOverride = 21 }));

        Assert.Equal(21, s.Target);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Un_ordre_refuse_est_renvoye_au_tick_suivant()
    {
        var b = new Banc();
        b.Mesurer(17);
        b.Commandes.Respond = (_, _) => CommandOutcome.Unreachable;
        await b.DemarrerAsync(Banc.Salon());

        b.Commandes.Respond = (_, _) => CommandOutcome.Accepted;
        await b.AvancerAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, b.Ordres(StandardCommands.TurnOn));   // un refusé, un accepté, puis plus rien
    }

    [Fact]
    public async Task Le_mode_Off_coupe_et_remet_l_integrale_a_zero()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon());
        await b.AvancerAsync(TimeSpan.FromMinutes(12));
        Assert.True(b.Moteur.GetSnapshot("salon")!.Integral > 0);

        await b.Moteur.ConfigureAsync([Banc.Salon(t => t with { ManualMode = ThermostatMode.Off })]);
        var s = await b.AvancerAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ThermostatStatus.Off, s.Status);
        Assert.Equal(0, s.Integral);
        Assert.Equal(StandardCommands.TurnOff, b.Commandes.Sent[^1].Command);
    }

    [Fact]
    public async Task Le_mode_Off_coupe_meme_si_le_moteur_se_croit_a_l_arret()
    {
        var b = new Banc();
        b.Mesurer(20.5);
        await b.DemarrerAsync(Banc.Salon());   // au-dessus de la consigne : arrêt envoyé et accepté
        b.Oublier();

        await b.Moteur.ConfigureAsync([Banc.Salon(t => t with { ManualMode = ThermostatMode.Off })]);
        await b.AvancerAsync(TimeSpan.FromSeconds(10));

        // Le dernier ordre accepté était « arrêt », mais le relais a pu être rallumé à la main depuis (C4).
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
        await b.AvancerAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));   // une fois par épisode, pas à chaque tick
    }

    [Fact]
    public async Task En_Off_une_consigne_ajustee_ne_chauffe_pas_et_n_est_pas_affichee()
    {
        var b = new Banc();
        b.Mesurer(17);

        var s = await b.DemarrerAsync(Banc.Salon(t => t with { ManualMode = ThermostatMode.Off, TargetOverride = 24 }));

        Assert.Equal(ThermostatStatus.Off, s.Status);
        Assert.Equal(7, s.Target);
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task L_inertie_coupe_au_dessus_du_seuil_haut_et_reprend_sous_le_seuil_bas()
    {
        var b = new Banc();
        b.Mesurer(19);
        await b.DemarrerAsync(Banc.Salon());

        b.Mesurer(21.2);   // consigne + 1,2 > seuil haut (1,0)
        var inertie = await b.AvancerAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ThermostatStatus.Coasting, inertie.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));

        b.Mesurer(20.5);   // + 0,5 : encore au-dessus du seuil bas (0,3)
        Assert.Equal(ThermostatStatus.Coasting, (await b.AvancerAsync(TimeSpan.FromSeconds(10))).Status);

        b.Mesurer(19.5);
        Assert.Equal(ThermostatStatus.Heating, (await b.AvancerAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(2, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task L_entree_en_inertie_coupe_meme_si_le_moteur_se_croit_a_l_arret()
    {
        var b = new Banc();
        b.Mesurer(20.5);
        await b.DemarrerAsync(Banc.Salon());   // déjà « arrêt » accepté
        b.Oublier();

        b.Mesurer(21.5);
        await b.AvancerAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff));
    }

    // ── Sonde perdue ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Une_sonde_muette_au_dela_du_delai_coupe_en_securite()
    {
        var b = new Banc();
        b.Mesurer(21);   // pièce chaude : le moteur ne chauffe pas
        await b.DemarrerAsync(Banc.Salon());

        var avant = await b.AvancerAsync(TimeSpan.FromMinutes(59));
        Assert.NotEqual(ThermostatStatus.SensorLost, avant.Status);
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOff, CommandPriority.Safety));

        var apres = await b.AvancerAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ThermostatStatus.SensorLost, apres.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff, CommandPriority.Safety));
        Assert.Contains(InhibitionReasons.SondePerdue, apres.InhibitionReasons);
    }

    [Fact]
    public async Task Une_sonde_indisponible_coupe_sans_attendre_le_delai()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon());

        b.Etats.Set(Sonde, 17.0, b.Horloge.GetUtcNow(), disponible: false);
        var s = await b.AvancerAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ThermostatStatus.SensorLost, s.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff, CommandPriority.Safety));
    }

    [Fact]
    public async Task Le_retour_de_la_sonde_leve_la_coupure_et_reprend_la_chauffe()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon());
        await b.AvancerAsync(TimeSpan.FromMinutes(61));
        b.Oublier();

        b.Mesurer(17);
        var s = await b.AvancerAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ThermostatStatus.Heating, s.Status);
        Assert.Empty(s.InhibitionReasons);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Failsafe_desactive_une_sonde_muette_ne_coupe_jamais()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon(t => t with { FailsafeMinutes = 0 }));

        await b.AvancerAsync(TimeSpan.FromHours(3));

        Assert.Equal(0, b.Ordres(StandardCommands.TurnOff, CommandPriority.Safety));
    }

    // ── Fenêtre ouverte ──────────────────────────────────────────────────────

    [Fact]
    public async Task Une_fenetre_ouverte_coupe_apres_son_delai_et_reprend_apres_la_fermeture()
    {
        var b = new Banc();
        b.Mesurer(17);
        b.Etats.Set(Fenetre, false, T0);
        await b.DemarrerAsync(Banc.Salon(t => t with { OpeningEntityId = Fenetre }));
        await b.AvancerAsync(TimeSpan.FromMinutes(12));

        b.Etats.Set(Fenetre, true, b.Horloge.GetUtcNow());
        await b.AvancerAsync(TimeSpan.FromSeconds(110));
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOff, CommandPriority.Safety));

        var ouverte = await b.AvancerAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ThermostatStatus.WindowOpen, ouverte.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOff, CommandPriority.Safety));
        Assert.Equal(0, ouverte.Integral);

        b.Mesurer(17);
        b.Etats.Set(Fenetre, false, b.Horloge.GetUtcNow());
        b.Oublier();
        var encore = await b.AvancerAsync(TimeSpan.FromSeconds(50));
        Assert.Equal(ThermostatStatus.WindowOpen, encore.Status);

        var reprise = await b.AvancerAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ThermostatStatus.Heating, reprise.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    // ── Délestage (inhibition extérieure) ────────────────────────────────────

    [Fact]
    public async Task Sous_delestage_rien_n_est_rallume_et_l_integrale_est_conservee()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon());
        await b.AvancerAsync(TimeSpan.FromMinutes(12));
        var integrale = b.Moteur.GetSnapshot("salon")!.Integral;

        await b.Inhibiteur.InhibitAsync("salon", [Radiateur], InhibitionReasons.Delestage);
        b.Oublier();
        b.Mesurer(17);
        var deleste = await b.AvancerAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(ThermostatStatus.Inhibited, deleste.Status);
        Assert.Equal(0, b.Ordres(StandardCommands.TurnOn));
        Assert.Equal(integrale, deleste.Integral);

        b.Inhibiteur.Lift("salon", InhibitionReasons.Delestage);
        b.Mesurer(17);
        var reprise = await b.AvancerAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ThermostatStatus.Heating, reprise.Status);
        Assert.Equal(1, b.Ordres(StandardCommands.TurnOn));
    }

    // ── Configuration ────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_radiateur_absent_du_registre_est_signale()
    {
        var b = new Banc();
        b.Mesurer(17);

        var s = await b.DemarrerAsync(Banc.Salon(t => t with { SwitchEntityIds = ["switch.radiateur_chbre_rdc"] }));

        Assert.Equal(ThermostatStatus.UnknownSwitch, s.Status);
        Assert.Equal(["switch.radiateur_chbre_rdc"], s.UnknownSwitches);
    }

    [Fact]
    public async Task Un_radiateur_que_le_plugin_ne_declare_plus_est_signale()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.Registre.RegisterAsync("test", []);   // le plugin ne le déclare plus : « manquant » au registre

        var s = await b.DemarrerAsync(Banc.Salon());

        Assert.Equal(ThermostatStatus.UnknownSwitch, s.Status);
    }

    [Fact]
    public async Task Un_thermostat_retire_leve_ses_propres_coupures()
    {
        var b = new Banc();
        b.Mesurer(17);
        await b.DemarrerAsync(Banc.Salon());
        await b.AvancerAsync(TimeSpan.FromMinutes(61));
        Assert.True(b.Inhibiteur.IsInhibited("salon"));

        await b.Moteur.ConfigureAsync([]);

        Assert.False(b.Inhibiteur.IsInhibited("salon"));
        Assert.Null(b.Moteur.GetSnapshot("salon"));
    }

    [Fact]
    public async Task Une_configuration_incoherente_est_refusee()
    {
        var b = new Banc();

        await Assert.ThrowsAsync<ArgumentException>(() => b.Moteur.ConfigureAsync([Banc.Salon(t => t with { SwitchEntityIds = [] })]));
        await Assert.ThrowsAsync<ArgumentException>(() => b.Moteur.ConfigureAsync(
            [Banc.Salon(t => t with { Tpi = t.Tpi with { CoastingLow = 1.5 } })]));
        await Assert.ThrowsAsync<ArgumentException>(() => b.Moteur.ConfigureAsync([Banc.Salon(), Banc.Salon()]));
    }
}
