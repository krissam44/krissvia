using Krissvia.Climate.Thermostats;
using Krissvia.Core.Scheduling;

namespace Krissvia.Climate.Tests;

/// <summary>Préchauffage anticipé de DomoMud : prochain créneau, avance nécessaire, correction météo, apprentissage.</summary>
public class PreheatTests
{
    // Mercredi 14 octobre 2026 ; Confort de 7 h à 9 h, Éco sinon.
    private static DateTime Mercredi(int heure, int minute = 0) => new(2026, 10, 14, heure, minute, 0);

    private static readonly WeeklySchedule Planning = new()
    {
        Days = new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>
        {
            [DayOfWeek.Wednesday] = [new ScheduleSlot(TimeSpan.FromHours(7), TimeSpan.FromHours(9), ThermostatMode.Comfort)],
        },
        DefaultMode = ThermostatMode.Eco,
    };

    private static ThermostatSettings Salon(Func<ThermostatSettings, ThermostatSettings>? ajuster = null)
    {
        var t = new ThermostatSettings
        {
            Id = "salon", TemperatureEntityId = "sensor.t", SwitchEntityIds = ["switch.r"], ScheduleActive = true, Schedule = Planning,
        };
        return ajuster?.Invoke(t) ?? t;
    }

    private static readonly Func<double, TimeSpan, double> Trente = (_, _) => 30;

    [Fact]
    public void Le_prochain_changement_est_cherche_dans_les_trois_heures()
    {
        Assert.Equal(new NextSlot(Mercredi(7), ThermostatMode.Comfort), Preheat.NextChange(Planning, Mercredi(6)));
        Assert.Equal(new NextSlot(Mercredi(9), ThermostatMode.Eco), Preheat.NextChange(Planning, Mercredi(7, 30)));
        Assert.Null(Preheat.NextChange(Planning, Mercredi(3, 59)));   // 7 h est à plus de 3 h
    }

    [Theory]
    [InlineData(6, 15, 18.5, true)]    // 45 min nécessaires, 45 min avant le créneau : on part
    [InlineData(6, 14, 18.5, false)]   // 46 min avant : encore trop tôt
    [InlineData(6, 45, 19.5, true)]    // 15 min nécessaires
    [InlineData(6, 30, 19.95, false)]  // déjà au niveau
    [InlineData(4, 0, 5.0, true)]      // 450 min nécessaires, plafonnées à 3 h
    public void Le_prechauffage_part_quand_l_avance_necessaire_est_atteinte(int h, int m, double ambiante, bool attendu)
    {
        var cible = Preheat.Target(Salon(), ambiante, Mercredi(h, m), 16.5, Trente);

        Assert.Equal(attendu ? 20.0 : null, cible);
    }

    [Fact]
    public void Rien_a_preparer_sans_planning_avec_une_consigne_ajustee_ou_desactive()
    {
        Assert.Null(Preheat.Target(Salon(t => t with { ScheduleActive = false }), 18, Mercredi(6, 50), 16.5, Trente));
        Assert.Null(Preheat.Target(Salon(t => t with { TargetOverride = 18 }), 18, Mercredi(6, 50), 18, Trente));
        Assert.Null(Preheat.Target(Salon(t => t with { PreheatEnabled = false }), 18, Mercredi(6, 50), 16.5, Trente));
    }

    [Fact]
    public void Un_creneau_moins_chaud_ou_Off_ne_se_prepare_pas()
    {
        // Avant 9 h : on passe de Confort à Éco, rien à chauffer en avance.
        Assert.Null(Preheat.Target(Salon(), 18, Mercredi(8, 50), 20, Trente));

        var versOff = Salon(t => t with
        {
            Schedule = Planning with
            {
                Days = new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>
                {
                    [DayOfWeek.Wednesday] = [new ScheduleSlot(TimeSpan.FromHours(7), TimeSpan.FromHours(9), ThermostatMode.Off)],
                },
                DefaultMode = ThermostatMode.FrostProtection,
            },
        });
        Assert.Null(Preheat.Target(versOff, 5, Mercredi(6, 50), 7, Trente));
    }

    [Fact]
    public void La_correction_meteo_se_deduit_de_Ki_et_reste_bornee()
    {
        var pente = new HeatingSlope(30, OutdoorReference: 10);

        // Ki 0,01, consigne 20 : tenir coûte 10 % à 10 °C dehors, 30 % à −10 °C ; surplus 0,9 / 0,7.
        Assert.Equal(30, Preheat.CorrectedMinutesPerDegree(pente, 0.01, 20, 10), 6);
        Assert.Equal(30 * 0.9 / 0.7, Preheat.CorrectedMinutesPerDegree(pente, 0.01, 20, -10), 6);
        Assert.Equal(30 * 0.9 / 1.0, Preheat.CorrectedMinutesPerDegree(pente, 0.01, 20, 25), 6);   // plus doux : plus court
        // Ki 0,04 : surplus 0,8 à 15 °C, 0,15 à −30 °C (tenue plafonnée à 0,85) → ×5,3, borné à ×3.
        Assert.Equal(90, Preheat.CorrectedMinutesPerDegree(new HeatingSlope(30, 15), 0.04, 20, -30), 6);

        Assert.Equal(30, Preheat.CorrectedMinutesPerDegree(pente, 0.01, 20, null));
        Assert.Equal(30, Preheat.CorrectedMinutesPerDegree(pente, 0, 20, -10));
        Assert.Equal(30, Preheat.CorrectedMinutesPerDegree(new HeatingSlope(30, null), 0.01, 20, -10));
    }

    [Fact]
    public void La_pente_s_apprend_par_moyenne_glissante_avec_sa_reference_exterieure()
    {
        var premiere = Preheat.Learn(null, minutes: 20, degres: 0.5, exterieur: 8);
        Assert.Equal(new HeatingSlope(40, 8), premiere);

        var seconde = Preheat.Learn(premiere, minutes: 10, degres: 0.5, exterieur: 0);
        Assert.Equal(new HeatingSlope(0.7 * 40 + 0.3 * 20, 0.7 * 8 + 0.3 * 0), seconde);

        Assert.Equal(8, Preheat.Learn(premiere, 10, 0.5, exterieur: null).OutdoorReference);   // sans mesure, on garde
        Assert.Equal(240, Preheat.Learn(null, 600, 0.5, null).MinutesPerDegree);              // bornée
        Assert.Equal(5, Preheat.Learn(null, 1, 0.5, null).MinutesPerDegree);
    }
}

/// <summary>Le magasin des pentes : un aller-retour, là où un champ se perd sans bruit.</summary>
public class HeatingSlopeStoreTests
{
    private sealed class Magasin : Krissvia.Core.Configuration.IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<Krissvia.Core.Configuration.ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Krissvia.Core.Configuration.ConfigurationDocument>>(
                Documents.Where(kv => kv.Key.Item1 == collection).Select(kv => new Krissvia.Core.Configuration.ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, Krissvia.Core.Configuration.ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) => Task.FromResult(Documents.Remove((collection, id)));
    }

    [Fact]
    public async Task Une_pente_enregistree_se_relit_telle_quelle_et_un_document_illisible_est_ignore()
    {
        var magasin = new Magasin();
        var pentes = new HeatingSlopeStore(magasin);
        await pentes.SaveAsync("salon", new HeatingSlope(42.5, 6.1));
        await pentes.SaveAsync("chambre", new HeatingSlope(20, null));
        magasin.Documents[(HeatingSlopeStore.Collection, "cuisine")] = "{pas du json";

        var relues = await new HeatingSlopeStore(magasin).LoadAsync();

        Assert.Equal(new HeatingSlope(42.5, 6.1), relues["salon"]);
        Assert.Equal(new HeatingSlope(20, null), relues["chambre"]);
        Assert.False(relues.ContainsKey("cuisine"));
    }
}
