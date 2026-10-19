using System.Net;
using System.Text.Json;
using Krissvia.Climate.Alerts;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Alerte gel : un lieu, sa prévision, et ce qui part (ou ne part pas) sur le téléphone.</summary>
public class FrostWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);

    private sealed class Magasin : IConfigurationStore
    {
        private readonly Dictionary<(string, string), string> _documents = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(
                _documents.Where(kv => kv.Key.Item1 == collection).Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            _documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) => Task.FromResult(_documents.Remove((collection, id)));
    }

    private sealed class FauxNtfy : HttpMessageHandler
    {
        public List<string> Titres { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Titres.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("title").GetString()!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FakeStateReader Etats { get; } = new();
        public FauxNtfy Ntfy { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public FrostWatch Veille { get; }
        public AlertService Alertes { get; }
        public string Vertou { get; }

        public Banc()
        {
            var magasin = new Magasin();
            var notifications = new NotificationService(magasin, new HttpClient(Ntfy));
            notifications.SaveAsync(new NotificationSettings { Enabled = true, Topic = "essai" }).GetAwaiter().GetResult();
            Alertes = new AlertService(notifications, magasin, Horloge);
            Registre.RegisterAsync("meteo",
            [
                new DeviceDescriptor
                {
                    UniqueId = "vertou", Name = "Vertou",
                    Entities = [new EntityDescriptor { UniqueId = "vertou:meteo", Kind = EntityKind.Sensor, Name = "Météo", DeviceClass = DeviceClasses.Weather }],
                },
            ]).GetAwaiter().GetResult();
            Vertou = Registre.Loaded.Single().EntityId;
            Veille = new FrostWatch(Registre, Etats, Alertes, Horloge);
        }

        /// <summary>Une prévision horaire de 24 h à partir de maintenant, relevée maintenant.</summary>
        public void Prevoir(params double[] temperatures) =>
            Etats.Set(Vertou, JsonSerializer.SerializeToElement(new
            {
                name = "Vertou",
                hourly = temperatures.Select((t, i) => new { time = Horloge.GetUtcNow().AddHours(i).ToString("yyyy-MM-ddTHH:mm:ssZ"), temperature = t }),
            }), Horloge.GetUtcNow());
    }

    private static readonly FrostAlertSettings Reglages = new() { ThresholdCelsius = 1, HorizonHours = 24 };

    [Fact]
    public async Task Un_gel_annonce_alerte_une_fois_puis_la_fin_est_annoncee()
    {
        var b = new Banc();
        b.Prevoir(6, 3, 0.5, -1.5, 2);

        var c = Assert.Single(await b.Veille.CheckAsync(Reglages));
        Assert.Equal(("Vertou", -1.5, "Raised"), (c.Place, c.Minimum, c.Outcome));

        b.Horloge.Advance(TimeSpan.FromMinutes(15));
        b.Prevoir(5, 0, -1, 3);
        await b.Veille.CheckAsync(Reglages);   // toujours du gel : pas de second message

        b.Horloge.Advance(TimeSpan.FromMinutes(15));
        b.Prevoir(8, 6, 4, 5);
        Assert.Equal("Resolved", Assert.Single(await b.Veille.CheckAsync(Reglages)).Outcome);

        Assert.Equal(["Risque de gel à Vertou", "Risque de gel écarté à Vertou"], b.Ntfy.Titres);
    }

    [Fact]
    public async Task Un_creux_au_dela_de_l_horizon_ne_compte_pas()
    {
        var b = new Banc();
        b.Prevoir(6, 5, 4, 3, -4);   // −4 °C dans 4 h

        await b.Veille.CheckAsync(Reglages with { HorizonHours = 3 });

        Assert.Empty(b.Ntfy.Titres);
    }

    [Fact]
    public async Task Une_prevision_morte_ne_conclut_rien_dans_aucun_sens()
    {
        var b = new Banc();
        b.Prevoir(0, -2, -3, 5, 5, 5, 5, 5, 5, 5);   // gel cette nuit, redoux ensuite
        await b.Veille.CheckAsync(Reglages);

        // Le plugin se tait 4 h. La vieille série ne montre plus que le redoux : elle ne doit pas suffire à
        // annoncer « gel écarté », ni rien d'autre.
        b.Horloge.Advance(TimeSpan.FromHours(4));
        var c = Assert.Single(await b.Veille.CheckAsync(Reglages));

        Assert.Equal("prévision indisponible", c.Outcome);
        Assert.Equal(["Risque de gel à Vertou"], b.Ntfy.Titres);
        Assert.True(b.Alertes.IsActive(FrostWatch.KeyPrefix + b.Vertou));
    }

    [Fact]
    public async Task Desactivee_l_alerte_ne_regarde_rien()
    {
        var b = new Banc();
        b.Prevoir(-5, -6);

        Assert.Empty(await b.Veille.CheckAsync(Reglages with { Enabled = false }));
        Assert.Empty(b.Ntfy.Titres);
    }

    [Fact]
    public void Le_minimum_prevu_va_de_l_heure_en_cours_a_l_horizon()
    {
        var serie = OutdoorForecastTests.Meteo(T0.AddHours(-3), -9, 5, 4, 3, 2, -1, 6);   // −9 il y a 3 h : passé

        Assert.Equal(2, OutdoorForecast.MinimumAhead(serie, T0.AddMinutes(30), TimeSpan.FromHours(1)));
        Assert.Equal(-1, OutdoorForecast.MinimumAhead(serie, T0.AddMinutes(30), TimeSpan.FromHours(3)));
        Assert.Null(OutdoorForecast.MinimumAhead(serie, T0.AddHours(4), TimeSpan.FromHours(3)));   // plus rien à venir
    }
}
