using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
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

        public Banc()
        {
            Inhibiteur = new ThermostatInhibitor(Commandes);
            Moteur = new ThermostatEngine(Etats, Registre, Commandes, Inhibiteur, Horloge);
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
