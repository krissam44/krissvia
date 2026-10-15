using Krissvia.Climate;
using Krissvia.Climate.LoadShedding;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Bus de commandes de test : enregistre les ordres, la réponse dépend de l'entité et de la commande.</summary>
internal sealed class ScriptedCommander : FauxBusCommandes
{
    private readonly object _verrou = new();

    public List<(string EntityId, string Command, CommandPriority Priority)> Sent { get; } = [];

    public Func<string, string, CommandOutcome> Respond { get; set; } = (_, _) => CommandOutcome.Accepted;

    protected override CommandOutcome Envoyer(string entityId, string command, CommandPriority priority)
    {
        lock (_verrou) Sent.Add((entityId, command, priority));
        return Respond(entityId, command);
    }
}

/// <summary>
/// Caractérisation du délesteur DomoMud (TESTS-CARACTERISATION.md, D1 à D11), avec les corrections décidées
/// pour D4, D5, D6, D7 et D8. Installation de référence : 6 kVA, alerte 80 % (4 800 VA), restauration 70 % (4 200 VA).
/// </summary>
public class LoadShedderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly LoadSheddingSettings Reglages6Kva = LoadSheddingSettings.FromSubscription(6, 80, 70);

    // Cumulus : priorité 1, coupé en premier, commande directe. Chambre et salon : radiateurs sous thermostat.
    private static SheddableDevice Cumulus() =>
        new("cumulus", "Chauffe-eau", 1, 2000, TimeSpan.FromSeconds(120), ["switch.cumulus"]);

    private static SheddableDevice Chambre(int delaiSecondes = 120) =>
        new("chambre", "Radiateur chambre RdC", 5, 900, TimeSpan.FromSeconds(delaiSecondes), ["switch.rad_chambre"], "t_chambre");

    private static SheddableDevice Salon(int delaiSecondes = 120) =>
        new("salon", "Radiateur salon", 6, 2500, TimeSpan.FromSeconds(delaiSecondes), ["switch.rad_salon"], "t_salon");

    private sealed class Bench
    {
        public ScriptedCommander Commander { get; } = new();
        public ThermostatInhibitor Inhibitor { get; }
        public FakeTimeProvider Clock { get; } = new(T0);
        public LoadShedder Shedder { get; }

        public Bench()
        {
            Inhibitor = new ThermostatInhibitor(Commander);
            Shedder = new LoadShedder(Commander, Inhibitor, Clock);
        }

        public Task ConfigureAsync(params SheddableDevice[] devices) =>
            Shedder.ConfigureAsync(Reglages6Kva, devices);

        public Task<LoadSheddingTick> TickAsync(double valeur) =>
            Shedder.TickAsync(new PowerReading(valeur, Clock.GetUtcNow()));

        public void Avancer(double secondes) => Clock.Advance(TimeSpan.FromSeconds(secondes));
    }

    // ── Réglages ─────────────────────────────────────────────────────────────

    [Fact]
    public void Les_seuils_se_deduisent_de_l_abonnement()
    {
        Assert.Equal(4800, Reglages6Kva.AlertThreshold);
        Assert.Equal(4200, Reglages6Kva.RestoreThreshold);

        var douze = LoadSheddingSettings.FromSubscription(12, 80, 70);
        Assert.Equal(9600, douze.AlertThreshold);
        Assert.Equal(8400, douze.RestoreThreshold);
    }

    [Fact]
    public void Un_seuil_de_restauration_non_inferieur_a_l_alerte_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = LoadSheddingSettings.FromSubscription(6, 70, 80); });
    }

    [Fact]
    public async Task La_configuration_refuse_des_seuils_incoherents_et_des_identifiants_en_double()
    {
        var b = new Bench();
        var incoherents = new LoadSheddingSettings { Enabled = true, AlertThreshold = 4000, RestoreThreshold = 4000 };

        await Assert.ThrowsAsync<ArgumentException>(() => b.Shedder.ConfigureAsync(incoherents, [Cumulus()]));
        await Assert.ThrowsAsync<ArgumentException>(() => b.Shedder.ConfigureAsync(Reglages6Kva, [Cumulus(), Cumulus()]));
    }

    // ── D1 : délestage par priorité croissante ───────────────────────────────

    [Fact]
    public async Task D1_au_dessus_du_seuil_on_deleste_par_priorite_croissante()
    {
        var b = new Bench();
        await b.ConfigureAsync(Salon(), Cumulus(), Chambre());

        var tick = await b.TickAsync(5200);   // excédent de 1 000 sur le seuil de restauration

        Assert.Equal(new[] { "cumulus" }, tick.Shed.ToArray());
        Assert.Equal(ShedTransition.Started, tick.Transition);
        Assert.Equal(new[] { "switch.cumulus" }, b.Commander.Sent.Select(s => s.EntityId).ToArray());
        Assert.All(b.Commander.Sent, s => Assert.Equal(CommandPriority.Safety, s.Priority));
    }

    [Fact]
    public async Task D1_on_deleste_plusieurs_equipements_jusqu_a_couvrir_l_excedent()
    {
        var b = new Bench();
        await b.ConfigureAsync(Salon(), Cumulus(), Chambre());

        var tick = await b.TickAsync(7000);   // excédent de 2 800 : le cumulus (2 000) ne suffit pas

        Assert.Equal(new[] { "cumulus", "chambre" }, tick.Shed.ToArray());
        Assert.Equal(new[] { "switch.cumulus", "switch.rad_chambre" }, b.Commander.Sent.Select(s => s.EntityId).ToArray());
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));
        Assert.False(b.Inhibitor.IsInhibited("t_salon"));
    }

    // ── D2 : rétablissement après le délai ───────────────────────────────────

    [Fact]
    public async Task D2_on_retablit_sous_le_seuil_de_restauration_une_fois_le_delai_ecoule()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre());
        await b.TickAsync(5000);   // excédent de 800 : la chambre (900) suffit

        b.Avancer(60);
        var tropTot = await b.TickAsync(3000);

        Assert.Empty(tropTot.Restored);
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));

        b.Avancer(61);
        var tick = await b.TickAsync(3000);

        Assert.Equal(new[] { "chambre" }, tick.Restored.ToArray());
        Assert.Equal(ShedTransition.Ended, tick.Transition);
        Assert.False(b.Inhibitor.IsInhibited("t_chambre"));
    }

    [Fact]
    public async Task D2_un_ordre_de_remise_sous_tension_refuse_est_retente()
    {
        var b = new Bench();
        await b.ConfigureAsync(Cumulus());
        await b.TickAsync(5200);
        b.Commander.Respond = (_, commande) =>
            commande == StandardCommands.TurnOn ? CommandOutcome.Failed : CommandOutcome.Accepted;

        b.Avancer(121);
        var echec = await b.TickAsync(1000);
        Assert.Empty(echec.Restored);

        b.Commander.Respond = (_, _) => CommandOutcome.Accepted;
        b.Avancer(1);
        var reussite = await b.TickAsync(1000);

        Assert.Equal(new[] { "cumulus" }, reussite.Restored.ToArray());
    }

    // ── D3 : hystérésis ──────────────────────────────────────────────────────

    [Fact]
    public async Task D3_entre_les_deux_seuils_rien_ne_bouge()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre());

        var rien = await b.TickAsync(4500);
        Assert.Empty(rien.Shed);
        Assert.Empty(b.Commander.Sent);

        await b.TickAsync(5000);
        b.Avancer(200);
        var tick = await b.TickAsync(4500);

        Assert.Empty(tick.Shed);
        Assert.Empty(tick.Restored);
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));
    }

    // ── D4 : pas de sur-délestage avant que la mesure reflète l'action ───────

    [Fact]
    public async Task D4_apres_une_coupure_on_attend_une_mesure_posterieure_a_l_effet()
    {
        var b = new Bench();
        await b.ConfigureAsync(Cumulus(), Chambre());

        var premier = await b.TickAsync(5300);   // excédent de 1 100 : le cumulus suffit
        Assert.Equal(new[] { "cumulus" }, premier.Shed.ToArray());

        b.Avancer(2);
        var tropTot = await b.TickAsync(5300);   // la mesure n'a pas encore baissé
        Assert.Empty(tropTot.Shed);
        Assert.Single(b.Commander.Sent);

        b.Avancer(4);
        var apres = await b.TickAsync(5300);     // mesure postérieure à l'effet, toujours trop haute
        Assert.Equal(new[] { "chambre" }, apres.Shed.ToArray());
    }

    // ── D5 : rétablissement un par un, avec marge ────────────────────────────

    [Fact]
    public async Task D5_on_retablit_un_equipement_a_la_fois()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre(0), Salon(0));
        await b.TickAsync(7500);   // les deux sont délestés
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));
        Assert.True(b.Inhibitor.IsInhibited("t_salon"));

        b.Avancer(10);
        var premier = await b.TickAsync(1000);   // le salon (priorité 6) d'abord
        Assert.Equal(new[] { "salon" }, premier.Restored.ToArray());
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));

        b.Avancer(2);
        var attente = await b.TickAsync(3400);   // mesure encore antérieure à l'effet du rétablissement
        Assert.Empty(attente.Restored);

        b.Avancer(4);
        var second = await b.TickAsync(3400);
        Assert.Equal(new[] { "chambre" }, second.Restored.ToArray());
    }

    [Fact]
    public async Task D5_un_equipement_qui_ferait_depasser_le_seuil_d_alerte_n_est_pas_retabli()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre(0), Salon(0));
        await b.TickAsync(7500);

        b.Avancer(10);
        var tick = await b.TickAsync(3000);   // salon : 3 000 + 2 500 = 5 500 > 4 800 ; chambre : 3 900 tient

        Assert.Equal(new[] { "chambre" }, tick.Restored.ToArray());
        Assert.True(b.Inhibitor.IsInhibited("t_salon"));
    }

    // ── D6 : une mesure absente n'est jamais lue comme zéro ──────────────────

    [Fact]
    public async Task D6_sans_mesure_ou_avec_une_mesure_perimee_rien_n_est_retabli()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre());
        await b.TickAsync(5000);
        b.Avancer(300);

        var absente = await b.Shedder.TickAsync(null);
        Assert.True(absente.SourceUnavailable);
        Assert.Empty(absente.Restored);
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));

        var perimee = await b.Shedder.TickAsync(new PowerReading(1000, b.Clock.GetUtcNow() - TimeSpan.FromSeconds(30)));
        Assert.True(perimee.SourceUnavailable);
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));

        var retour = await b.TickAsync(1000);
        Assert.False(retour.SourceUnavailable);
        Assert.Equal(new[] { "chambre" }, retour.Restored.ToArray());
    }

    // ── D7 : une coupure échouée ne compte pas comme économie ────────────────

    [Fact]
    public async Task D7_une_coupure_echouee_ne_compte_pas_et_l_equipement_suivant_est_delesté()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre(), Salon());
        b.Commander.Respond = (id, _) => id == "switch.rad_chambre" ? CommandOutcome.Failed : CommandOutcome.Accepted;

        var tick = await b.TickAsync(5200);   // excédent de 1 000

        Assert.Equal(new[] { "chambre", "salon" }, tick.Shed.ToArray());
        Assert.Equal(new[] { "chambre" }, tick.Unconfirmed.ToArray());
    }

    [Fact]
    public async Task D7_une_coupure_acceptee_mais_non_confirmee_ne_compte_pas_non_plus()
    {
        // Le cas de juillet 2026 : l'ordre part, le Shelly reste alimenté. Sans confirmation par l'état, le
        // cumulus n'a rien économisé : le délesteur doit couper le suivant au lieu de croire l'excédent résorbé.
        var b = new Bench();
        await b.ConfigureAsync(Cumulus(), Chambre(), Salon());
        b.Commander.Obeit = (id, _) => id != "switch.cumulus";

        var tick = await b.TickAsync(5300);   // excédent de 1 100 : le cumulus suffirait

        Assert.Equal(new[] { "cumulus", "chambre", "salon" }, tick.Shed.ToArray());
        Assert.Equal(new[] { "cumulus" }, tick.Unconfirmed.ToArray());
        Assert.All(b.Commander.Sent, s => Assert.Equal(StandardCommands.TurnOff, s.Command));
    }

    [Fact]
    public async Task D7_la_coupure_non_confirmee_est_retentee_et_soldee_quand_l_etat_la_confirme()
    {
        var b = new Bench();
        await b.ConfigureAsync(Cumulus(), Chambre());
        b.Commander.Obeit = (id, _) => id != "switch.cumulus";
        await b.TickAsync(5300);

        b.Avancer(6);
        Assert.Equal(new[] { "cumulus" }, (await b.TickAsync(4500)).Unconfirmed.ToArray());

        b.Commander.Obeit = (_, _) => true;
        b.Avancer(6);
        Assert.Empty((await b.TickAsync(4500)).Unconfirmed);
        Assert.Equal(3, b.Commander.Sent.Count(s => s.EntityId == "switch.cumulus"));
    }

    [Fact]
    public async Task D7_un_equipement_a_deux_relais_n_est_coupe_que_si_les_deux_le_sont()
    {
        var b = new Bench();
        var plancher = new SheddableDevice("plancher", "Plancher chauffant", 1, 3000, TimeSpan.FromSeconds(120),
            ["switch.plancher_zone1", "switch.plancher_zone2"]);
        await b.ConfigureAsync(plancher, Chambre());
        b.Commander.Obeit = (id, _) => id != "switch.plancher_zone2";

        var tick = await b.TickAsync(5300);

        Assert.Equal(new[] { "plancher", "chambre" }, tick.Shed.ToArray());
        Assert.Equal(new[] { "plancher" }, tick.Unconfirmed.ToArray());
    }

    [Fact]
    public async Task Une_coupure_directe_attend_sa_confirmation_le_delai_regle()
    {
        var b = new Bench();
        await b.Shedder.ConfigureAsync(Reglages6Kva with { CutVerificationTimeout = TimeSpan.FromSeconds(3) }, [Cumulus()]);

        await b.TickAsync(5200);

        var coupure = Assert.Single(b.Commander.Verifies);
        Assert.Equal(("switch.cumulus", "delestage", (TimeSpan?)TimeSpan.FromSeconds(3)), coupure);
    }

    [Fact]
    public async Task D7_la_coupure_echouee_est_retentee_a_chaque_tick_jusqu_a_confirmation()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre(), Salon());
        b.Commander.Respond = (id, _) => id == "switch.rad_chambre" ? CommandOutcome.Failed : CommandOutcome.Accepted;
        await b.TickAsync(5200);

        b.Commander.Respond = (_, _) => CommandOutcome.Accepted;
        b.Avancer(6);
        var tick = await b.TickAsync(4500);

        Assert.Empty(tick.Unconfirmed);
        Assert.Equal(2, b.Commander.Sent.Count(s => s.EntityId == "switch.rad_chambre"));
    }

    // ── D8 : un équipement retiré pendant un délestage est rétabli ───────────

    [Fact]
    public async Task D8_un_thermostat_retire_pendant_un_delestage_n_reste_pas_inhibe()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre());
        await b.TickAsync(5000);
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));

        await b.Shedder.ConfigureAsync(Reglages6Kva, []);

        Assert.False(b.Inhibitor.IsInhibited("t_chambre"));
    }

    [Fact]
    public async Task D8_un_equipement_direct_retire_pendant_un_delestage_est_remis_sous_tension()
    {
        var b = new Bench();
        await b.ConfigureAsync(Cumulus());
        await b.TickAsync(5200);

        await b.Shedder.ConfigureAsync(Reglages6Kva, []);

        var dernier = b.Commander.Sent[^1];
        Assert.Equal("switch.cumulus", dernier.EntityId);
        Assert.Equal(StandardCommands.TurnOn, dernier.Command);
    }

    // ── D9 : délesteur désactivé ─────────────────────────────────────────────

    [Fact]
    public async Task D9_desactiver_le_delesteur_retablit_tous_les_equipements()
    {
        var b = new Bench();
        await b.ConfigureAsync(Chambre(), Cumulus());
        await b.TickAsync(7000);

        await b.Shedder.ConfigureAsync(Reglages6Kva with { Enabled = false }, [Chambre(), Cumulus()]);
        var tick = await b.TickAsync(7000);

        Assert.Equal(new[] { "chambre", "cumulus" }, tick.Restored.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ShedTransition.Ended, tick.Transition);
        Assert.False(b.Inhibitor.IsInhibited("t_chambre"));
        Assert.Equal(StandardCommands.TurnOn, b.Commander.Sent[^1].Command);
    }

    // ── D10 : on ne signale que les bascules ─────────────────────────────────

    [Fact]
    public async Task D10_la_bascule_n_est_signalee_qu_une_fois_par_episode()
    {
        var b = new Bench();
        await b.ConfigureAsync(Cumulus());

        var debut = await b.TickAsync(5200);
        Assert.Equal(ShedTransition.Started, debut.Transition);

        b.Avancer(6);
        var encore = await b.TickAsync(5200);
        Assert.Equal(ShedTransition.None, encore.Transition);

        b.Avancer(200);
        var fin = await b.TickAsync(1000);
        Assert.Equal(ShedTransition.Ended, fin.Transition);

        b.Avancer(6);
        var calme = await b.TickAsync(1000);
        Assert.Equal(ShedTransition.None, calme.Transition);
    }

    // ── D11 : thermostat lié, inhibition sans toucher aux autres motifs ──────

    [Fact]
    public async Task D11_le_retablissement_ne_leve_que_le_motif_delestage()
    {
        var b = new Bench();
        await b.Inhibitor.InhibitAsync("t_chambre", ["switch.rad_chambre"], InhibitionReasons.FenetreOuverte);
        await b.ConfigureAsync(Chambre());

        await b.TickAsync(5000);
        Assert.Equal(
            new[] { InhibitionReasons.Delestage, InhibitionReasons.FenetreOuverte },
            b.Inhibitor.GetReasons("t_chambre").ToArray());

        b.Avancer(121);
        await b.TickAsync(3000);

        Assert.Equal(new[] { InhibitionReasons.FenetreOuverte }, b.Inhibitor.GetReasons("t_chambre").ToArray());
        Assert.True(b.Inhibitor.IsInhibited("t_chambre"));
    }
}
