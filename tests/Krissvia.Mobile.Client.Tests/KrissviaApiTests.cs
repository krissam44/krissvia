using System.Net;
using System.Text;
using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class KrissviaApiTests
{
    internal sealed record Connexion(string BaseUrl, string ApiKey) : IConnexion;

    /// <summary>Répond ce qu'on lui dit et garde les requêtes, corps compris (il est relu après l'envoi).</summary>
    /// <remarks>
    /// <paramref name="redirigeVers"/> imite ce que HttpClient laisse après avoir suivi une redirection : la réponse
    /// porte la DERNIÈRE requête, en GET, à la nouvelle adresse.
    /// </remarks>
    private sealed class FauxServeur(HttpStatusCode code, string corps, string? redirigeVers = null) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Requete, string? Corps)> Recues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Recues.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var reponse = new HttpResponseMessage(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
            if (redirigeVers is not null)
                reponse.RequestMessage = new HttpRequestMessage(HttpMethod.Get, redirigeVers + request.RequestUri!.PathAndQuery);
            return reponse;
        }
    }

    private static (KrissviaApi Api, FauxServeur Serveur) Creer(HttpStatusCode code, string corps, string cle = "secret")
    {
        var serveur = new FauxServeur(code, corps);
        return (new KrissviaApi(new HttpClient(serveur), new Connexion("http://192.168.1.10:5000", cle)), serveur);
    }

    [Fact]
    public async Task Un_ordre_part_sur_le_bus_avec_la_cle_et_la_source_mobile()
    {
        var (api, serveur) = Creer(HttpStatusCode.OK, """{ "outcome": "accepted", "verification": "not_applicable" }""");

        var resultat = await api.ChoisirModeAsync("climate.salon", "eco");

        Assert.True(resultat.Ok);
        var (requete, corps) = Assert.Single(serveur.Recues);
        Assert.Equal(HttpMethod.Post, requete.Method);
        Assert.Equal("http://192.168.1.10:5000/api/entities/climate.salon/commands", requete.RequestUri!.ToString());
        Assert.Equal("secret", Assert.Single(requete.Headers.GetValues(KrissviaApi.EnTeteCle)));
        using var json = JsonDocument.Parse(corps!);
        Assert.Equal("set_mode", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("eco", json.RootElement.GetProperty("args").GetProperty("mode").GetString());
        Assert.Equal("mobile", json.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Le_pas_de_consigne_est_relatif()
    {
        var (api, serveur) = Creer(HttpStatusCode.OK, """{ "outcome": "accepted" }""");

        await api.AjusterConsigneAsync("climate.salon", -0.5);
        await api.ActiverPlanningAsync("climate.salon", false);

        using var pas = JsonDocument.Parse(serveur.Recues[0].Corps!);
        Assert.Equal("adjust_target", pas.RootElement.GetProperty("command").GetString());
        Assert.Equal(-0.5, pas.RootElement.GetProperty("args").GetProperty("delta").GetDouble());
        using var planning = JsonDocument.Parse(serveur.Recues[1].Corps!);
        Assert.Equal("set_schedule", planning.RootElement.GetProperty("command").GetString());
        Assert.False(planning.RootElement.GetProperty("args").GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Un_ordre_refuse_par_le_coeur_rend_son_motif()
    {
        var (api, _) = Creer(HttpStatusCode.OK,
            """{ "outcome": "failed", "verification": "not_sent", "detail": "thermostat arrêté (mode off) : choisir d'abord un mode" }""");

        var resultat = await api.AjusterConsigneAsync("climate.salon", 0.5);

        Assert.False(resultat.Ok);
        Assert.Equal("thermostat arrêté (mode off) : choisir d'abord un mode", resultat.Motif);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "", "Clé d'API refusée")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", "aucune clé d'API n'est configurée")]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": "« climate.salon » n'accepte pas « turn_on »" }""", "n'accepte pas")]
    [InlineData(HttpStatusCode.NotFound, "", "à mettre à jour")]
    public async Task Une_erreur_http_se_lit(HttpStatusCode code, string corps, string attendu)
    {
        var (api, _) = Creer(code, corps);

        var resultat = await api.ChoisirModeAsync("climate.salon", "eco");

        Assert.False(resultat.Ok);
        Assert.Contains(attendu, resultat.Motif, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sans_cle_aucun_en_tete_n_est_envoye()
    {
        var (api, serveur) = Creer(HttpStatusCode.OK, "[]", cle: "");

        await api.EntitesAsync();

        Assert.False(serveur.Recues[0].Requete.Headers.Contains(KrissviaApi.EnTeteCle));
    }

    [Fact]
    public async Task Sans_adresse_rien_ne_part()
    {
        var serveur = new FauxServeur(HttpStatusCode.OK, "[]");
        var api = new KrissviaApi(new HttpClient(serveur), new Connexion("", ""));

        Assert.Equal("Serveur non configuré.", await api.TesterAsync());
        Assert.Empty(serveur.Recues);
    }

    [Fact]
    public async Task Un_serveur_injoignable_garde_sa_cause()
    {
        // Un port fermé sur la boucle locale : la vraie exception de la pile réseau, pas une imitation.
        var api = new KrissviaApi(new HttpClient(), new Connexion("http://127.0.0.1:1", "secret"));

        var erreur = await api.TesterAsync();

        Assert.StartsWith("Serveur injoignable", erreur, StringComparison.Ordinal);
        Assert.Contains("(", erreur, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Une_adresse_redirigee_est_nommee_au_lieu_du_405()
    {
        // http:// redirigé vers https:// par le proxy : le POST est devenu un GET, et le serveur répond 405.
        var serveur = new FauxServeur(HttpStatusCode.MethodNotAllowed, "", redirigeVers: "https://home.exemple.fr");
        var api = new KrissviaApi(new HttpClient(serveur), new Connexion("http://home.exemple.fr", "secret"));

        var resultat = await api.ChoisirModeAsync("climate.salon", "eco");

        Assert.False(resultat.Ok);
        Assert.Contains("redirige vers https://home.exemple.fr,", resultat.Motif, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Le_test_de_connexion_signale_une_adresse_qui_redirige()
    {
        var serveur = new FauxServeur(HttpStatusCode.OK, "[]", redirigeVers: "https://home.exemple.fr");
        var api = new KrissviaApi(new HttpClient(serveur), new Connexion("http://home.exemple.fr", "secret"));

        var erreur = await api.TesterAsync();

        Assert.NotNull(erreur);
        Assert.Contains("Enregistrez https://home.exemple.fr.", erreur, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://home.exemple.fr", "http://home.exemple.fr")]
    [InlineData("http://HOME.exemple.fr:80", "http://home.exemple.fr")]
    public async Task Sans_redirection_le_test_reussit(string adresse, string servie)
    {
        var serveur = new FauxServeur(HttpStatusCode.OK, "[]", redirigeVers: servie);
        var api = new KrissviaApi(new HttpClient(serveur), new Connexion(adresse, "secret"));

        Assert.Null(await api.TesterAsync());
    }
}
