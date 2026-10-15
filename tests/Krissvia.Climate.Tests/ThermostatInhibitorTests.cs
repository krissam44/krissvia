using Krissvia.Climate;
using Krissvia.Contracts;
using Krissvia.Core.Commands;

namespace Krissvia.Climate.Tests;

/// <summary>Bus de commandes de test : enregistre les ordres et répond comme on le lui dit.</summary>
internal sealed class FakeCommander : FauxBusCommandes
{
    private readonly object _verrou = new();

    public List<(string EntityId, string Command, CommandPriority Priority)> Sent { get; } = [];

    public Func<string, CommandOutcome> Respond { get; set; } = _ => CommandOutcome.Accepted;

    /// <summary>Appelé au moment de l'envoi, avant la réponse.</summary>
    public Action<string>? OnSend { get; set; }

    protected override CommandOutcome Envoyer(string entityId, string command, CommandPriority priority)
    {
        lock (_verrou) Sent.Add((entityId, command, priority));
        OnSend?.Invoke(entityId);
        return Respond(entityId);
    }
}

public class ThermostatInhibitorTests
{
    private static (ThermostatInhibitor Inhibitor, FakeCommander Commander) Create()
    {
        var commander = new FakeCommander();
        return (new ThermostatInhibitor(commander), commander);
    }

    // ── Ordre des opérations ─────────────────────────────────────────────────

    [Fact]
    public async Task L_inhibition_est_posee_avant_la_premiere_commande()
    {
        var (inhibitor, commander) = Create();
        var vuInhibe = new List<bool>();
        commander.OnSend = _ => { lock (vuInhibe) vuInhibe.Add(inhibitor.IsInhibited("t1")); };

        await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        Assert.Equal(2, vuInhibe.Count);
        Assert.All(vuInhibe, v => Assert.True(v));
    }

    [Fact]
    public async Task Chaque_switch_est_coupe_en_priorite_securite()
    {
        var (inhibitor, commander) = Create();

        var result = await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        Assert.True(result.FullyCut);
        Assert.Equal(2, commander.Sent.Count);
        Assert.All(commander.Sent, s =>
        {
            Assert.Equal(StandardCommands.TurnOff, s.Command);
            Assert.Equal(CommandPriority.Safety, s.Priority);
        });
    }

    [Fact]
    public async Task La_coupure_est_renvoyee_meme_si_le_thermostat_est_deja_inhibe()
    {
        var (inhibitor, commander) = Create();

        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage);
        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage);

        Assert.Equal(2, commander.Sent.Count);
    }

    [Fact]
    public async Task Un_switch_en_double_n_est_coupe_qu_une_fois()
    {
        var (inhibitor, commander) = Create();

        await inhibitor.InhibitAsync("t1", ["switch.a", "switch.a"], InhibitionReasons.Delestage);

        Assert.Single(commander.Sent);
    }

    // ── Échecs : jamais comptés comme une coupure réussie ────────────────────

    [Fact]
    public async Task Un_ordre_echoue_n_est_pas_une_coupure_reussie_et_reste_en_attente()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = id => id == "switch.b" ? CommandOutcome.Failed : CommandOutcome.Accepted;

        var result = await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        Assert.False(result.FullyCut);
        Assert.Equal(CommandOutcome.Failed, result.Outcomes["switch.b"]);
        Assert.Equal(new[] { "switch.b" }, inhibitor.GetPendingSwitches("t1").ToArray());
        Assert.True(inhibitor.IsInhibited("t1"));
    }

    [Fact]
    public async Task Un_ordre_accepte_sans_etat_qui_le_confirme_n_est_pas_une_coupure()
    {
        // §7.3 : « accepté » dit que le plugin a pris l'ordre, pas que le relais a obéi.
        var (inhibitor, commander) = Create();
        commander.Obeit = (id, _) => id != "switch.b";

        var result = await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        Assert.Equal(CommandOutcome.Accepted, result.Outcomes["switch.b"]);
        Assert.False(result.FullyCut);
        Assert.Equal(new[] { "switch.b" }, inhibitor.GetPendingSwitches("t1").ToArray());
    }

    [Fact]
    public async Task Une_coupure_non_confirmee_est_retentee_jusqu_a_confirmation()
    {
        var (inhibitor, commander) = Create();
        commander.Obeit = (_, _) => false;
        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.FenetreOuverte);

        Assert.Equal(1, await inhibitor.RetryPendingAsync());

        commander.Obeit = (_, _) => true;
        Assert.Equal(0, await inhibitor.RetryPendingAsync());
        Assert.Equal(3, commander.Sent.Count);
        Assert.All(commander.Verifies, v => Assert.StartsWith("inhibition:", v.Origin));
    }

    [Fact]
    public async Task Un_appareil_injoignable_n_est_pas_une_coupure_reussie()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = _ => CommandOutcome.Unreachable;

        var result = await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage);

        Assert.False(result.FullyCut);
        Assert.Equal(new[] { "switch.a" }, inhibitor.GetPendingSwitches("t1").ToArray());
    }

    [Fact]
    public async Task Une_exception_du_bus_devient_un_echec_sans_empecher_les_autres_coupures()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = id => id == "switch.a"
            ? throw new InvalidOperationException("broker déconnecté")
            : CommandOutcome.Accepted;

        var result = await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        Assert.Equal(CommandOutcome.Failed, result.Outcomes["switch.a"]);
        Assert.Equal(CommandOutcome.Accepted, result.Outcomes["switch.b"]);
        Assert.False(result.FullyCut);
    }

    [Fact]
    public async Task Un_thermostat_sans_switch_n_est_pas_une_coupure_reussie()
    {
        var (inhibitor, _) = Create();

        var result = await inhibitor.InhibitAsync("t1", [], InhibitionReasons.Delestage);

        Assert.False(result.FullyCut);
        Assert.True(inhibitor.IsInhibited("t1"));
    }

    [Fact]
    public async Task L_annulation_se_propage_et_l_inhibition_reste_posee()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = _ => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage));

        Assert.True(inhibitor.IsInhibited("t1"));
    }

    // ── Nouvelles tentatives ─────────────────────────────────────────────────

    [Fact]
    public async Task RetryPending_ne_renvoie_que_ce_qui_a_echoue_puis_s_arrete_apres_succes()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = id => id == "switch.b" ? CommandOutcome.Failed : CommandOutcome.Accepted;
        await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        commander.Sent.Clear();
        commander.Respond = _ => CommandOutcome.Accepted;
        var restants = await inhibitor.RetryPendingAsync();

        Assert.Equal(0, restants);
        Assert.Equal(new[] { "switch.b" }, commander.Sent.Select(s => s.EntityId).ToArray());

        var encore = await inhibitor.RetryPendingAsync();
        Assert.Equal(0, encore);
        Assert.Single(commander.Sent);
    }

    [Fact]
    public async Task RetryPending_compte_les_switches_encore_en_attente()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = _ => CommandOutcome.Failed;
        await inhibitor.InhibitAsync("t1", ["switch.a", "switch.b"], InhibitionReasons.Delestage);

        var restants = await inhibitor.RetryPendingAsync();

        Assert.Equal(2, restants);
    }

    [Fact]
    public async Task Apres_la_levee_plus_aucune_coupure_n_est_retentee()
    {
        var (inhibitor, commander) = Create();
        commander.Respond = _ => CommandOutcome.Failed;
        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage);

        var encoreInhibe = inhibitor.Lift("t1", InhibitionReasons.Delestage);
        commander.Sent.Clear();
        var restants = await inhibitor.RetryPendingAsync();

        Assert.False(encoreInhibe);
        Assert.Equal(0, restants);
        Assert.Empty(commander.Sent);
    }

    // ── Plusieurs motifs ─────────────────────────────────────────────────────

    [Fact]
    public async Task Le_thermostat_reste_inhibe_tant_qu_un_autre_motif_demeure()
    {
        var (inhibitor, _) = Create();
        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage);
        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.FenetreOuverte);

        Assert.True(inhibitor.Lift("t1", InhibitionReasons.Delestage));
        Assert.True(inhibitor.IsInhibited("t1"));
        Assert.Equal(new[] { InhibitionReasons.FenetreOuverte }, inhibitor.GetReasons("t1").ToArray());

        Assert.False(inhibitor.Lift("t1", InhibitionReasons.FenetreOuverte));
        Assert.False(inhibitor.IsInhibited("t1"));
    }

    [Fact]
    public void Lever_un_motif_sur_un_thermostat_inconnu_est_sans_effet()
    {
        var (inhibitor, _) = Create();

        Assert.False(inhibitor.Lift("inconnu", InhibitionReasons.Delestage));
        Assert.False(inhibitor.IsInhibited("inconnu"));
    }

    [Fact]
    public async Task Les_thermostats_sont_independants()
    {
        var (inhibitor, _) = Create();

        await inhibitor.InhibitAsync("t1", ["switch.a"], InhibitionReasons.Delestage);

        Assert.True(inhibitor.IsInhibited("t1"));
        Assert.False(inhibitor.IsInhibited("t2"));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "delestage")]
    [InlineData("  ", "delestage")]
    [InlineData("t1", "")]
    public async Task Les_identifiants_vides_sont_refuses(string thermostatId, string reason)
    {
        var (inhibitor, _) = Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            inhibitor.InhibitAsync(thermostatId, ["switch.a"], reason));
    }

    // ── Concurrence (classe de bug du 02/10/2026) ────────────────────────────

    [Fact]
    public async Task Les_acces_concurrents_ne_levent_pas_et_laissent_un_etat_coherent()
    {
        var (inhibitor, _) = Create();

        var taches = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var thermostat = $"t{i % 5}";
            await inhibitor.InhibitAsync(thermostat, [$"switch.{i % 5}"], InhibitionReasons.Delestage);
            _ = inhibitor.IsInhibited(thermostat);
            _ = inhibitor.GetReasons(thermostat);
            _ = await inhibitor.RetryPendingAsync();
            inhibitor.Lift(thermostat, InhibitionReasons.Delestage);
        }));

        await Task.WhenAll(taches);

        for (var t = 0; t < 5; t++) Assert.False(inhibitor.IsInhibited($"t{t}"));
    }
}
