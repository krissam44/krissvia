using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Configuration;
using Krissvia.Core.History;
using Krissvia.Core.Registry;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// L'auto-TPI de bout en bout : l'historique découpé en intervalles, le modèle identifié, le réglage proposé, puis
/// appliqué et remis — jamais appliqué seul (choix du 22/10/2026).
/// </summary>
public class AutoTpiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Intervalles ──────────────────────────────────────────────────────────

    private static HistoryPoint P(double minutes, double? v) => new("s", T0.AddMinutes(minutes), v);

    [Fact]
    public void Un_intervalle_porte_la_part_de_chauffe_et_l_ecart_moyen_ponderes_par_le_temps()
    {
        var interieur = new[] { P(-5, 19), P(15, 20) };          // 19 °C puis 20 °C à mi-intervalle
        var exterieur = new[] { P(-5, 5) };
        var chauffe = new[] { P(-5, 0), P(10, 1), P(20, 0) };    // allumé 10 min sur 30

        var i = Assert.Single(ThermalIntervals.Build(interieur, exterieur, chauffe, T0, T0.AddMinutes(30)));

        Assert.Equal(0.5, i.Hours);
        Assert.Equal((19.0, 20.0), (i.IndoorStart, i.IndoorEnd));
        Assert.Equal(1 / 3.0, i.Duty, 6);
        Assert.Equal(19.5 - 5, i.IndoorOutdoorGap, 6);
    }

    [Fact]
    public void Un_intervalle_ou_une_serie_est_inconnue_plus_de_10_pourcent_du_temps_est_ecarte()
    {
        var interieur = new[] { P(-5, 19), P(10, null), P(20, 19.5) };   // sonde muette 10 min sur 30
        var exterieur = new[] { P(-5, 5) };
        var chauffe = new[] { P(-5, 0) };

        Assert.Empty(ThermalIntervals.Build(interieur, exterieur, chauffe, T0, T0.AddMinutes(30)));
        Assert.Empty(ThermalIntervals.Build([P(5, 19)], exterieur, chauffe, T0, T0.AddMinutes(30)));   // rien avant 00:05
    }

    // ── Service ──────────────────────────────────────────────────────────────

    private sealed class Magasin : IConfigurationStore
    {
        private readonly Dictionary<(string, string), string> _documents = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(_documents.Where(kv => kv.Key.Item1 == collection)
                .Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            _documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) => Task.FromResult(_documents.Remove((collection, id)));
    }

    /// <summary>L'historique en mémoire : ce que <see cref="IHistoryRepository.ReadAsync"/> rendrait, valeur d'avant comprise.</summary>
    private sealed class Historique : IHistoryRepository
    {
        public Dictionary<string, List<HistoryPoint>> Series { get; } = [];

        public Task<IReadOnlyList<HistoryPoint>> ReadAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            var points = Series.GetValueOrDefault(series) ?? [];
            var avant = points.LastOrDefault(p => p.At < from);
            IReadOnlyList<HistoryPoint> r = [.. (avant is null ? [] : new[] { avant }), .. points.Where(p => p.At >= from && p.At <= to)];
            return Task.FromResult(r);
        }

        public Task AppendAsync(IReadOnlyList<HistoryPoint> points, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistoryBucket>> ReadHourlyAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SeriesAsync(string entityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RollupAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DateTimeOffset?> LastRolledHourAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(DateTimeOffset before, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Banc
    {
        public Magasin Magasin { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public FakeTimeProvider Horloge { get; } = new(T0.AddDays(14));
        public Historique Historique { get; } = new();
        public ThermostatService Thermostats { get; }
        public AutoTpiService AutoTpi { get; }
        public string Climat { get; }

        public Banc()
        {
            var bus = new ScriptedCommander();
            Thermostats = new ThermostatService(Magasin, Registre,
                new ThermostatEngine(new FakeStateReader(), Registre, bus, new ThermostatInhibitor(bus), Horloge));
            AutoTpi = new AutoTpiService(Thermostats, Registre, Historique, Magasin, Horloge);
            Registre.RegisterAsync("z2m",
            [
                new DeviceDescriptor { UniqueId = "sonde", Name = "Sonde salon", Entities = [new EntityDescriptor { UniqueId = "t", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature }] },
                new DeviceDescriptor { UniqueId = "ext", Name = "Terrasse", Entities = [new EntityDescriptor { UniqueId = "x", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature }] },
                new DeviceDescriptor { UniqueId = "rad", Name = "Radiateur salon", Entities = [new EntityDescriptor { UniqueId = "r", Kind = EntityKind.Switch }] },
            ]).GetAwaiter().GetResult();
            // Le thermostat tel que le cœur le publie (ThermostatEntities) : c'est sous ce nom que l'historique le range.
            Registre.RegisterAsync(CoreIntegrations.Thermostats,
                [new DeviceDescriptor { UniqueId = "salon", Name = "Salon", Entities = [new EntityDescriptor { UniqueId = "salon", Kind = EntityKind.Climate }] }])
                .GetAwaiter().GetResult();
            Registre.ListAsync().GetAwaiter().GetResult();
            Climat = Registre.Loaded.Single(e => e.Kind == EntityKind.Climate).EntityId;
        }

        public string Id(string unique) => Registre.Loaded.Single(e => e.UniqueId == unique).EntityId;

        public Task AjouterAsync(bool exterieure = true, bool autoTpi = true) => Thermostats.SaveAsync(new ThermostatSettings
        {
            Id = "salon", Name = "Salon", TemperatureEntityId = Id("t"), SwitchEntityIds = [Id("r")],
            OutdoorTemperatureEntityId = exterieure ? Id("x") : null, AutoTpi = autoTpi,
        });

        /// <summary>14 jours de la pièce simulée (g = 2, k = 0,05, c = 0,15), tels que l'historique les aurait gardés.</summary>
        public void Simuler()
        {
            List<HistoryPoint> interieur = [], exterieur = [], chauffe = [];
            var t = 19.0;
            double part = 0;
            for (var m = 0; m < 14 * 24 * 60; m++)
            {
                var at = T0.AddMinutes(m);
                var heure = m / 60.0;
                var dehors = 6 + 5 * Math.Sin(2 * Math.PI * (heure - 9) / 24);
                var consigne = heure % 24 is >= 7 and < 22 ? 21.0 : 19.0;
                if (m % 10 == 0) part = Math.Clamp(0.6 * (consigne - t) + 0.03 * (consigne - dehors), 0, 1);
                var u = m % 10 < part * 10 ? 1.0 : 0.0;
                t += (2.0 * u - 0.05 * (t - dehors) + 0.15) / 60.0;
                interieur.Add(new HistoryPoint("", at, Math.Round(t, 1)));
                exterieur.Add(new HistoryPoint("", at, Math.Round(dehors, 1)));
                chauffe.Add(new HistoryPoint("", at, u));
            }

            Historique.Series[HistoryValues.Serie(Climat, "temperature")] = interieur;
            Historique.Series[HistoryValues.Serie(Climat, "outdoor_temperature")] = exterieur;
            Historique.Series[HistoryValues.Serie(Climat, "heating")] = chauffe;
        }
    }

    [Fact]
    public async Task Sans_sonde_exterieure_rien_n_est_propose_et_le_compte_rendu_survit_au_redemarrage()
    {
        var b = new Banc();
        await b.AjouterAsync(exterieure: false);

        var r = await b.AutoTpi.ComputeAsync("salon");

        Assert.Contains("pas de sonde extérieure", r.Model.Refusal);
        Assert.Null(r.Proposal);
        // Un modèle refusé a une constante de temps infinie : il doit quand même s'écrire et se relire.
        var apres = new AutoTpiService(b.Thermostats, b.Registre, b.Historique, b.Magasin, b.Horloge);
        await apres.LoadAsync();
        Assert.Equal(r.Model.Refusal, apres.Get("salon")!.Model.Refusal);
    }

    [Fact]
    public async Task La_piece_est_identifiee_et_le_reglage_propose_n_est_applique_que_sur_demande()
    {
        var b = new Banc();
        await b.AjouterAsync();
        b.Simuler();

        var r = await b.AutoTpi.ComputeAsync("salon");

        Assert.True(r.Model.Usable, r.Model.Refusal);
        Assert.InRange(r.Model.Gain, 1.8, 2.2);
        Assert.InRange(r.Proposal!.Ki, 0.02, 0.03);
        Assert.Equal(TpiGains.Of(new TpiSettings()), TpiGains.Of(b.Thermostats.Get("salon")!.Tpi));   // rien n'a bougé
        Assert.False(r.ProposalInPlace);

        // Des radiateurs à eau : même pièce, même équilibre (Ki), mais une régulation deux fois plus douce (Kp).
        await b.AutoTpi.SetOptionsAsync("salon", true, HeatingType.Water);
        var eau = await b.AutoTpi.ComputeAsync("salon");
        Assert.Equal(r.Proposal.Ki, eau.Proposal!.Ki);
        Assert.Equal(Math.Round(1 / (eau.Model.Gain * ThermalModel.Lambda(HeatingType.Water)), 3), eau.Proposal.Kp);
    }

    [Fact]
    public async Task Appliquer_garde_le_reglage_d_origine_meme_deux_fois_et_revenir_le_remet()
    {
        var b = new Banc();
        await b.AjouterAsync();
        b.Simuler();
        var origine = TpiGains.Of(b.Thermostats.Get("salon")!.Tpi);
        var propose = (await b.AutoTpi.ComputeAsync("salon")).Proposal!;

        var applique = await b.AutoTpi.ApplyAsync("salon");
        Assert.Equal(new TpiGains(propose.Kp, propose.Ki, propose.Kint), TpiGains.Of(applique.Tpi));
        Assert.True(b.AutoTpi.Get("salon")!.ProposalInPlace);

        await b.AutoTpi.ComputeAsync("salon");    // le calcul du lendemain…
        await b.AutoTpi.ApplyAsync("salon");      // …appliqué à son tour
        Assert.Equal(origine, b.AutoTpi.Get("salon")!.Previous);

        var remis = await b.AutoTpi.RevertAsync("salon");
        Assert.Equal(origine, TpiGains.Of(remis.Tpi));
        Assert.Null(b.AutoTpi.Get("salon")!.Previous);
        await Assert.ThrowsAsync<ThermostatValidationException>(() => b.AutoTpi.RevertAsync("salon"));
    }

    [Fact]
    public async Task Sans_reglage_propose_appliquer_est_refuse()
    {
        var b = new Banc();
        await b.AjouterAsync();
        await b.AutoTpi.ComputeAsync("salon");   // historique vide : pas de modèle

        await Assert.ThrowsAsync<ThermostatValidationException>(() => b.AutoTpi.ApplyAsync("salon"));
    }

    [Fact]
    public async Task Le_calcul_quotidien_ne_touche_que_les_thermostats_qui_l_ont_demande_une_fois_par_jour()
    {
        var b = new Banc();
        await b.AjouterAsync(autoTpi: false);
        Assert.Equal(0, await b.AutoTpi.ComputeDueAsync());

        await b.AutoTpi.SetOptionsAsync("salon", true, HeatingType.Water);
        Assert.Equal(1, await b.AutoTpi.ComputeDueAsync());
        Assert.Equal(HeatingType.Water, b.AutoTpi.Get("salon")!.HeatingType);
        Assert.Equal(0, await b.AutoTpi.ComputeDueAsync());

        b.Horloge.Advance(AutoTpiService.Periode);
        Assert.Equal(1, await b.AutoTpi.ComputeDueAsync());
    }
}
