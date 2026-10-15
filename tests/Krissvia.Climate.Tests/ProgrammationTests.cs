using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// Programmations d'équipements : on n'agit qu'aux changements de créneau, l'état attendu est appliqué au démarrage,
/// un ordre non confirmé est repris, et le délestage garde la main sur ce qu'il a coupé.
/// </summary>
public class ProgrammationTests
{
    private const string Prise = "switch.prise_salon";
    private const string Cumulus = "switch.cumulus";

    // Lundi 12 octobre 2026, heure locale (le faux temps est en UTC, fuseau UTC).
    private static DateTimeOffset Lundi(int h, int m = 0) => new(2026, 10, 12, h, m, 0, TimeSpan.Zero);

    private static DeviceSchedule TousLesJours(string id, string interrupteur, double debut, double fin) => new()
    {
        Id = id,
        SwitchEntityIds = [interrupteur],
        Days = Core.Scheduling.Jours.Semaine.ToDictionary(j => j,
            _ => (IReadOnlyList<DeviceScheduleSlot>)[new DeviceScheduleSlot(TimeSpan.FromHours(debut), TimeSpan.FromHours(fin))]),
    };

    private sealed class Banc
    {
        public ScriptedCommander Bus { get; } = new();
        public FakeTimeProvider Horloge { get; } = new(Lundi(5));
        public DesiredSwitchStates Voulus { get; } = new();
        public LoadShedder Delesteur { get; }
        public DeviceScheduleEngine Moteur { get; }

        public Banc()
        {
            Delesteur = new LoadShedder(Bus, new ThermostatInhibitor(Bus), Horloge, Voulus);
            Moteur = new DeviceScheduleEngine(Bus, Delesteur, Voulus, Horloge);
        }

        public async Task A(DateTimeOffset t)
        {
            Horloge.SetUtcNow(t);
            await Moteur.TickAsync();
        }

        /// <summary>Ordres envoyés à ce switch depuis le dernier appel.</summary>
        public List<string> Ordres(string entite)
        {
            var ordres = Bus.Sent.Where(s => s.EntityId == entite).Select(s => s.Command).ToList();
            Bus.Sent.Clear();
            return ordres;
        }
    }

    [Fact]
    public async Task Au_demarrage_l_etat_attendu_est_applique_une_seule_fois()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8)]);

        await b.A(Lundi(6, 30));
        Assert.Equal([StandardCommands.TurnOn], b.Ordres(Prise));

        await b.A(Lundi(6, 31));
        Assert.Empty(b.Ordres(Prise));
    }

    [Fact]
    public async Task Seuls_les_changements_de_creneau_agissent_un_geste_manuel_tient_jusque_la()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8)]);
        await b.A(Lundi(5));
        Assert.Equal([StandardCommands.TurnOff], b.Ordres(Prise));

        // Allumée à la main à 5:10 : rien ne la rééteint avant le prochain changement.
        await b.A(Lundi(5, 30));
        Assert.Empty(b.Ordres(Prise));

        await b.A(Lundi(6));
        Assert.Equal([StandardCommands.TurnOn], b.Ordres(Prise));
        await b.A(Lundi(8));
        Assert.Equal([StandardCommands.TurnOff], b.Ordres(Prise));
    }

    [Fact]
    public async Task Un_ordre_non_confirme_est_renvoye_jusqu_a_confirmation()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8)]);
        b.Bus.Obeit = (_, _) => false;

        await b.A(Lundi(6));
        await b.A(Lundi(6, 1));
        Assert.Equal([StandardCommands.TurnOn, StandardCommands.TurnOn], b.Ordres(Prise));
        Assert.False(b.Moteur.GetSnapshot("prise")!.Applied);

        b.Bus.Obeit = (_, _) => true;
        await b.A(Lundi(6, 2));
        await b.A(Lundi(6, 3));
        Assert.Equal([StandardCommands.TurnOn], b.Ordres(Prise));
        Assert.True(b.Moteur.GetSnapshot("prise")!.Applied);
    }

    [Fact]
    public async Task Les_ordres_sont_signes_par_la_programmation()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8)]);

        await b.A(Lundi(6));

        Assert.Equal("programmation:prise", Assert.Single(b.Bus.Verifies).Origin);
    }

    [Fact]
    public async Task Une_nuit_finit_le_lendemain_matin()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("cumulus", Cumulus, 22, 6)]);

        await b.A(Lundi(23));
        Assert.Equal([StandardCommands.TurnOn], b.Ordres(Cumulus));
        await b.A(Lundi(23).AddHours(6).AddMinutes(59));   // mardi 5:59
        Assert.Empty(b.Ordres(Cumulus));
        await b.A(Lundi(23).AddHours(7));                   // mardi 6:00
        Assert.Equal([StandardCommands.TurnOff], b.Ordres(Cumulus));
    }

    [Fact]
    public async Task Une_programmation_desactivee_ne_pilote_rien_et_ne_retient_rien()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8) with { Enabled = false }]);

        await b.A(Lundi(6));

        Assert.Empty(b.Ordres(Prise));
        Assert.Null(b.Voulus.Get(Prise));
    }

    [Fact]
    public async Task Une_programmation_modifiee_est_reappliquee_aussitot()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8)]);
        await b.A(Lundi(7));
        b.Ordres(Prise);

        // Modifiée à 7:00 : désormais de 6 à 7:30, toujours allumée — l'ordre repart quand même, une fois.
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 7.5)]);
        await b.A(Lundi(7, 1));
        await b.A(Lundi(7, 2));

        Assert.Equal([StandardCommands.TurnOn], b.Ordres(Prise));
    }

    // ── Avec le délestage ────────────────────────────────────────────────────

    private static async Task DelesterCumulusAsync(Banc b)
    {
        await b.Delesteur.ConfigureAsync(LoadSheddingSettings.FromSubscription(6, 80, 70),
            [new SheddableDevice("cumulus", "Chauffe-eau", 1, 2000, TimeSpan.FromSeconds(120), [Cumulus])]);
        await b.Delesteur.TickAsync(new PowerReading(5200, b.Horloge.GetUtcNow()));
        Assert.True(b.Delesteur.IsSwitchShed(Cumulus));
    }

    [Fact]
    public async Task Une_programmation_ne_rallume_pas_un_equipement_deleste_le_retablissement_le_fera()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("cumulus", Cumulus, 6, 8)]);
        await b.A(Lundi(5, 59));
        b.Horloge.SetUtcNow(Lundi(5, 59));
        await DelesterCumulusAsync(b);
        b.Ordres(Cumulus);

        await b.A(Lundi(6));
        Assert.Empty(b.Ordres(Cumulus));
        Assert.True(b.Voulus.Get(Cumulus));

        b.Horloge.SetUtcNow(Lundi(6, 3));
        await b.Delesteur.TickAsync(new PowerReading(1000, b.Horloge.GetUtcNow()));
        Assert.Equal([StandardCommands.TurnOn], b.Ordres(Cumulus));
    }

    [Fact]
    public async Task Le_retablissement_ne_rallume_pas_ce_que_la_programmation_a_eteint()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("cumulus", Cumulus, 6, 8)]);
        await b.A(Lundi(7, 58));
        b.Horloge.SetUtcNow(Lundi(7, 58));
        await DelesterCumulusAsync(b);

        await b.A(Lundi(8));                 // fin du créneau pendant le délestage
        b.Ordres(Cumulus);

        b.Horloge.SetUtcNow(Lundi(8, 1));
        var tick = await b.Delesteur.TickAsync(new PowerReading(1000, b.Horloge.GetUtcNow()));

        Assert.Equal(["cumulus"], tick.Restored);
        Assert.DoesNotContain(StandardCommands.TurnOn, b.Ordres(Cumulus));
    }

    [Fact]
    public async Task Le_moteur_annonce_le_changement_de_la_minute_suivante()
    {
        var b = new Banc();
        await b.Moteur.ConfigureAsync([TousLesJours("prise", Prise, 6, 8)]);

        await b.A(Lundi(5, 59));

        var instantane = b.Moteur.GetSnapshot("prise")!;
        Assert.False(instantane.Desired);
        Assert.Equal(Lundi(6).DateTime, instantane.NextChange);
    }

    [Fact]
    public void Le_prochain_changement_est_annonce()
    {
        var p = TousLesJours("prise", Prise, 6, 8);

        Assert.Equal(Lundi(6).DateTime, p.NextChange(Lundi(5).DateTime));
        Assert.Equal(Lundi(8).DateTime, p.NextChange(Lundi(6, 30).DateTime));
        Assert.Null((p with { Days = new Dictionary<DayOfWeek, IReadOnlyList<DeviceScheduleSlot>>() }).NextChange(Lundi(5).DateTime));
    }
}
