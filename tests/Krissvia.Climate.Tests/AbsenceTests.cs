using Krissvia.Climate.Presence;
using Krissvia.Core.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// L'absence du foyer : la période programmée (C8 de DomoMud), la hiérarchie forçage → période → détection, et
/// l'enregistrement qui doit survivre à un redémarrage en pleine absence.
/// </summary>
public class AbsenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 12, 20, 8, 0, 0, TimeSpan.Zero);

    private static HomePresenceState Present(params string[] noms) => new(true, false, noms, [], [], null);
    private static HomePresenceState Vide() => new(false, false, [], ["Téléphone"], [], T0);
    private static HomePresenceState Perdue() => new(true, true, [], [], ["Téléphone"], null);

    /// <summary>Une semaine, à partir de T0.</summary>
    private static ScheduledAbsence Semaine(bool annulerSiPresence = true) => new(T0, T0.AddDays(7), annulerSiPresence);

    // ── Période programmée ───────────────────────────────────────────────────

    [Fact]
    public void Programmer_depuis_son_salon_ne_l_annule_pas()
    {
        // Le piège de DomoMud : on est chez soi quand on programme son départ.
        var p = Semaine().Evolve(Present("Christian"), T0.AddMinutes(1));

        Assert.False(p!.Cancelled);
        Assert.False(p.DepartureSeen);
        Assert.True(p.IsActive(T0.AddMinutes(1)));
    }

    [Fact]
    public void Un_retour_anticipe_annule_une_fois_le_depart_constate()
    {
        var p = Semaine().Evolve(Vide(), T0.AddHours(1))!;
        Assert.True(p.DepartureSeen);

        p = p.Evolve(Present("Christian"), T0.AddDays(3))!;

        Assert.True(p.Cancelled);
        Assert.False(p.IsActive(T0.AddDays(3)));
    }

    [Fact]
    public void Une_detection_perdue_n_est_pas_un_retour()
    {
        // Box injoignable au milieu des vacances : « occupée » par sécurité, mais personne n'est rentré.
        var p = Semaine().Evolve(Vide(), T0.AddHours(1))!.Evolve(Perdue(), T0.AddDays(3))!;

        Assert.False(p.Cancelled);
        Assert.True(p.IsActive(T0.AddDays(3)));
    }

    [Fact]
    public void Sans_l_option_un_retour_anticipe_ne_change_rien()
    {
        var p = Semaine(annulerSiPresence: false).Evolve(Vide(), T0.AddHours(1))!.Evolve(Present("Anne"), T0.AddDays(3))!;

        Assert.False(p.Cancelled);
    }

    [Fact]
    public void La_periode_s_efface_au_retour_et_n_agit_pas_avant_le_depart()
    {
        var avant = new ScheduledAbsence(T0.AddDays(1), T0.AddDays(2));
        Assert.Equal(avant, avant.Evolve(Vide(), T0));   // pas commencée : le vide de ce matin ne vaut pas départ

        Assert.Null(Semaine().Evolve(Present(), T0.AddDays(7)));
    }

    // ── Hiérarchie ───────────────────────────────────────────────────────────

    [Fact]
    public void Le_forcage_l_emporte_sur_la_periode_qui_l_emporte_sur_la_detection()
    {
        var maintenant = T0.AddHours(1);

        var periode = HomeAbsenceState.Compute(null, Semaine(), Present("Christian"), maintenant);
        Assert.Equal(new HomeAbsenceState(true, AbsenceSource.Scheduled, T0.AddDays(7)), periode);

        Assert.Equal(new HomeAbsenceState(false, AbsenceSource.Manual, null), HomeAbsenceState.Compute(false, Semaine(), Vide(), maintenant));
        // Forcée absente pendant une période : le retour prévu reste visé par la remontée.
        Assert.Equal(T0.AddDays(7), HomeAbsenceState.Compute(true, Semaine(), null, maintenant).Return);

        Assert.Equal(new HomeAbsenceState(true, AbsenceSource.Detection, null), HomeAbsenceState.Compute(null, null, Vide(), maintenant));
        Assert.Equal(HomeAbsenceState.Home, HomeAbsenceState.Compute(null, null, Perdue(), maintenant));
        // Pas encore de calcul de présence : on ne prétend pas savoir que la maison est vide.
        Assert.Equal(HomeAbsenceState.Home, HomeAbsenceState.Compute(null, null, null, maintenant));
    }

    // ── Service ──────────────────────────────────────────────────────────────

    private sealed class Magasin : IConfigurationStore
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

    [Fact]
    public async Task Un_redemarrage_en_pleine_absence_garde_la_periode_et_le_depart_constate()
    {
        var magasin = new Magasin();
        var horloge = new FakeTimeProvider(T0.AddDays(-1));
        var service = new AbsenceService(magasin, horloge);
        await service.ScheduleAsync(T0, T0.AddDays(7), annulerSiPresence: true);
        horloge.SetUtcNow(T0.AddHours(1));
        await service.UpdateAsync(Vide());
        Assert.True(service.Period!.DepartureSeen);

        var relu = new AbsenceService(magasin, horloge);
        await relu.LoadAsync();

        Assert.Equal(service.Period, relu.Period);
        Assert.Equal(new HomeAbsenceState(true, AbsenceSource.Scheduled, T0.AddDays(7)), relu.Current);
        // Le départ constaté a survécu : le retour anticipé annule bien après le redémarrage.
        await relu.UpdateAsync(Present("Christian"));
        Assert.False(relu.Current.Away);
    }

    [Fact]
    public async Task Le_service_previent_des_changements_et_refuse_une_periode_incoherente()
    {
        var horloge = new FakeTimeProvider(T0);
        var service = new AbsenceService(new Magasin(), horloge);
        var changements = 0;
        service.Changed += () => changements++;

        await Assert.ThrowsAsync<AbsenceValidationException>(() => service.ScheduleAsync(T0.AddDays(2), T0.AddDays(1), true));
        await Assert.ThrowsAsync<AbsenceValidationException>(() => service.ScheduleAsync(T0.AddDays(-3), T0.AddDays(-1), true));
        Assert.Equal(0, changements);

        await service.ForceAsync(true);
        Assert.Equal(1, changements);
        Assert.True(service.Current.Away);

        await service.UpdateAsync(Present("Christian"));   // forcée : la présence n'y change rien
        Assert.Equal(1, changements);

        await service.ForceAsync(null);
        Assert.False(service.Current.Away);
        Assert.Equal(2, changements);
    }
}
