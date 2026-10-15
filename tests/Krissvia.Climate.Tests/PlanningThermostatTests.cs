using Krissvia.Climate.Thermostats;

namespace Krissvia.Climate.Tests;

/// <summary>Planning hebdomadaire : comportements de DomoMud conservés (C11, C12).</summary>
public class PlanningThermostatTests
{
    private static readonly TimeSpan H6 = TimeSpan.FromHours(6);
    private static readonly TimeSpan H8 = TimeSpan.FromHours(8);
    private static readonly TimeSpan H22 = TimeSpan.FromHours(22);

    // Mercredi 14 octobre 2026.
    private static DateTime Mercredi(int heure, int minute = 0) => new(2026, 10, 14, heure, minute, 0);

    private static WeeklySchedule Planning(DayOfWeek jour, params ScheduleSlot[] creneaux) => new()
    {
        Days = new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>> { [jour] = creneaux },
        DefaultMode = ThermostatMode.Eco,
    };

    [Theory]
    [InlineData(5, 59, ThermostatMode.Eco)]
    [InlineData(6, 0, ThermostatMode.Comfort)]
    [InlineData(7, 59, ThermostatMode.Comfort)]
    [InlineData(8, 0, ThermostatMode.Eco)]
    public void Un_creneau_est_actif_de_son_debut_inclus_a_sa_fin_exclue(int h, int m, ThermostatMode attendu)
    {
        var p = Planning(DayOfWeek.Wednesday, new ScheduleSlot(H6, H8, ThermostatMode.Comfort));

        Assert.Equal(attendu, p.ModeAt(Mercredi(h, m)));
    }

    [Theory]
    [InlineData(23, ThermostatMode.Comfort1)]
    [InlineData(3, ThermostatMode.Comfort1)]
    [InlineData(12, ThermostatMode.Eco)]
    public void Un_creneau_de_nuit_est_actif_le_soir_et_le_matin_du_meme_jour(int h, ThermostatMode attendu)
    {
        var p = Planning(DayOfWeek.Wednesday, new ScheduleSlot(H22, H6, ThermostatMode.Comfort1));

        Assert.Equal(attendu, p.ModeAt(Mercredi(h)));
    }

    [Fact]
    public void Une_fin_a_minuit_veut_dire_fin_de_journee()
    {
        var p = Planning(DayOfWeek.Wednesday, new ScheduleSlot(H22, TimeSpan.Zero, ThermostatMode.Comfort));

        Assert.Equal(ThermostatMode.Comfort, p.ModeAt(Mercredi(23, 30)));
        Assert.Equal(ThermostatMode.Eco, p.ModeAt(Mercredi(3)));
    }

    [Fact]
    public void Deux_creneaux_qui_se_chevauchent_le_plus_matinal_gagne()
    {
        var p = Planning(DayOfWeek.Wednesday,
            new ScheduleSlot(TimeSpan.FromHours(7), TimeSpan.FromHours(9), ThermostatMode.Comfort2),
            new ScheduleSlot(H6, H8, ThermostatMode.Comfort));

        Assert.Equal(ThermostatMode.Comfort, p.ModeAt(Mercredi(7, 30)));
    }

    [Fact]
    public void Un_jour_sans_creneau_prend_le_mode_par_defaut()
    {
        var p = Planning(DayOfWeek.Sunday, new ScheduleSlot(H6, H8, ThermostatMode.Comfort));

        Assert.Equal(ThermostatMode.Eco, p.ModeAt(Mercredi(7)));
        Assert.Equal(ThermostatMode.Comfort, p.ModeAt(new DateTime(2026, 10, 18, 7, 0, 0)));   // dimanche
    }

    [Fact]
    public void Le_planning_ne_s_applique_que_s_il_est_actif()
    {
        var reglages = new ThermostatSettings
        {
            Id = "t", TemperatureEntityId = "s", SwitchEntityIds = ["r"], ManualMode = ThermostatMode.Comfort2,
            Schedule = Planning(DayOfWeek.Wednesday, new ScheduleSlot(H6, H8, ThermostatMode.Comfort)),
        };

        Assert.Equal(ThermostatMode.Comfort2, reglages.ModeAt(Mercredi(7)));
        Assert.Equal(ThermostatMode.Comfort, (reglages with { ScheduleActive = true }).ModeAt(Mercredi(7)));
    }
}
