using System.Net;
using System.Text;
using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class EnergieTests
{
    private const string Reponse = """
        {
          "price": 0.2516,
          "meters": [
            { "name": "Linky", "index_entity_id": "sensor.linky_index_total", "power": 1840, "power_unit": "VA", "voltage": 231.5,
              "today_kwh": 6.4, "today_cost": 1.61,
              "days": [ { "day": "2026-10-28", "kwh": null, "cost": null, "peak_power": null },
                        { "day": "2026-10-29", "kwh": 12.3, "cost": 3.09, "peak_power": 5120 },
                        { "day": "2026-10-30", "kwh": 6.4, "cost": 1.61, "peak_power": 3900 } ] }
          ]
        }
        """;

    private sealed class Serveur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        public Uri? Adresse { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Adresse = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Un_compteur_se_lit_avec_ses_jours_et_son_cout()
    {
        var vue = VueEnergie.Lire(JsonSerializer.Deserialize<JsonElement>(Reponse))!;

        Assert.Equal(0.2516, vue.Prix);
        var linky = Assert.Single(vue.Compteurs);
        Assert.Equal(1840, linky.Puissance);
        Assert.Equal("VA", linky.UnitePuissance);
        Assert.Equal(6.4, linky.AujourdhuiKwh);
        Assert.Equal(1.61, linky.AujourdhuiCout);
        Assert.Equal(new DateOnly(2026, 10, 30), linky.Jours[^1].Jour);
        // Un jour sans mesure reste sans valeur : pas « 0 kWh ».
        Assert.Null(linky.Jours[0].Kwh);
        Assert.Equal(12.3, linky.MaxKwh);
    }

    [Fact]
    public async Task La_periode_part_dans_la_requete()
    {
        var serveur = new Serveur(HttpStatusCode.OK, Reponse);
        var api = new KrissviaApi(new HttpClient(serveur), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

        var (vue, erreur, ancien) = await api.EnergieAsync(14);

        Assert.Null(erreur);
        Assert.False(ancien);
        Assert.NotNull(vue);
        Assert.Equal("http://192.168.1.10:5000/api/energy?days=14", serveur.Adresse!.ToString());
    }

    [Fact]
    public async Task Un_serveur_trop_ancien_se_reconnait()
    {
        var api = new KrissviaApi(new HttpClient(new Serveur(HttpStatusCode.NotFound, "")), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

        var (vue, _, ancien) = await api.EnergieAsync();

        Assert.Null(vue);
        Assert.True(ancien);
    }
}
