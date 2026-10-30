using System.Net;
using System.Text;
using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class SondesTests
{
    private const string Reponse = """
        {
          "rooms": [ { "room": "Salon", "average": 19.5, "active": 2, "total": 3, "unreliable": 1 },
                     { "room": "Cave", "average": null, "active": 0, "total": 1, "unreliable": 1 } ],
          "sensors": [
            { "entity_id": "sensor.salon_t", "name": "Sonde salon", "room": "Salon", "temperature": 19.4, "humidity": 55,
              "battery": 87, "measured_at": "2026-10-30T10:00:00+00:00", "health": "ok" },
            { "entity_id": "sensor.cave_t", "name": "Sonde cave", "room": "Cave", "temperature": 12.1,
              "measured_at": "2026-10-30T07:00:00+00:00", "health": "stale" },
            { "entity_id": "sensor.neuve", "name": "", "health": "never_received" }
          ],
          "stale_after_minutes": 60
        }
        """;

    private static VueSondes Lire() => VueSondes.Lire(JsonSerializer.Deserialize<JsonElement>(Reponse))!;

    [Fact]
    public void Les_moyennes_et_les_sondes_se_lisent_telles_que_le_serveur_les_calcule()
    {
        var vue = Lire();

        Assert.Equal(["Salon", "Cave"], vue.Pieces.Select(p => p.Nom));
        Assert.Equal(19.5, vue.Pieces[0].Moyenne);
        Assert.Equal(1, vue.Pieces[0].Ecartees);
        // Aucune sonde à jour : pas de moyenne, plutôt qu'un zéro.
        Assert.Null(vue.Pieces[1].Moyenne);

        var salon = vue.Sondes[0];
        Assert.Equal(55, salon.Humidite);
        Assert.Equal(87, salon.Batterie);
        Assert.False(salon.Douteuse);
        Assert.Null(vue.Alerte(salon));
    }

    [Fact]
    public void Une_sonde_douteuse_le_dit_avec_le_delai_du_serveur()
    {
        var vue = Lire();

        Assert.True(vue.Sondes[1].Douteuse);
        Assert.Equal("Muette +60 min", vue.Alerte(vue.Sondes[1]));
        Assert.Equal("Jamais reçue", vue.Alerte(vue.Sondes[2]));
        // Sans nom, l'identifiant.
        Assert.Equal("sensor.neuve", vue.Sondes[2].Nom);
        Assert.Null(vue.Sondes[2].Temperature);
    }

    private sealed class Serveur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Un_serveur_trop_ancien_se_reconnait()
    {
        var api = new KrissviaApi(new HttpClient(new Serveur(HttpStatusCode.NotFound, "")), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

        var (vue, erreur, ancien) = await api.SondesAsync();

        Assert.Null(vue);
        Assert.True(ancien);
        Assert.Contains("/api/sensors", erreur, StringComparison.Ordinal);
        Assert.Contains("à mettre à jour", erreur, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Une_cle_refusee_n_est_pas_un_serveur_ancien()
    {
        var api = new KrissviaApi(new HttpClient(new Serveur(HttpStatusCode.Unauthorized, "")), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

        var (_, erreur, ancien) = await api.SondesAsync();

        Assert.False(ancien);
        Assert.StartsWith("Clé d'API refusée", erreur, StringComparison.Ordinal);
    }

    [Fact]
    public async Task La_vue_arrive_par_l_api()
    {
        var api = new KrissviaApi(new HttpClient(new Serveur(HttpStatusCode.OK, Reponse)), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

        var (vue, erreur, ancien) = await api.SondesAsync();

        Assert.Null(erreur);
        Assert.False(ancien);
        Assert.Equal(3, vue!.Sondes.Count);
    }
}
