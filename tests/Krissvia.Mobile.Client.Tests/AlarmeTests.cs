using System.Net;
using System.Text;
using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class AlarmeTests
{
    private sealed class Serveur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        public HttpRequestMessage? Requete { get; private set; }
        public string? Recu { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requete = request;
            Recu = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
        }
    }

    private static KrissviaApi Api(Serveur serveur) => new(new HttpClient(serveur), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

    [Fact]
    public void Une_sortie_en_cours_se_lit_avec_son_decompte()
    {
        var etat = EtatAlarme.Lire(JsonSerializer.Deserialize<JsonElement>("""
            { "enabled": true, "state": { "state": "exit_delay", "label": "EXIT_DELAY:12", "countdown": 12, "ringing": false,
              "sensor": null, "triggered_at": null, "faults": "OK" }, "faults_now": ["Porte d'entrée ouverte"], "locked_seconds": 0 }
            """))!;

        Assert.Equal("Sortie en cours", etat.Libelle);
        Assert.True(etat.EnDecompte);
        Assert.False(etat.Desarmee);
        Assert.Equal(12, etat.Decompte);
        // « OK » n'est pas un refus.
        Assert.Null(etat.DernierRefus);
        Assert.Equal(["Porte d'entrée ouverte"], etat.DefautsMaintenant);
    }

    [Fact]
    public void Un_declenchement_garde_son_capteur_et_son_heure()
    {
        var etat = EtatAlarme.Lire(JsonSerializer.Deserialize<JsonElement>("""
            { "enabled": true, "state": { "state": "triggered", "countdown": 0, "ringing": true, "sensor": "Fenêtre chambre",
              "triggered_at": "2026-10-30T03:12:00+00:00", "faults": "Porte garage" }, "faults_now": [], "locked_seconds": 42 }
            """))!;

        Assert.Equal("Déclenchée", etat.Libelle);
        Assert.True(etat.Sonne);
        Assert.Equal("Fenêtre chambre", etat.Capteur);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 3, 12, 0, TimeSpan.Zero), etat.DeclencheeA);
        Assert.Equal("Porte garage", etat.DernierRefus);
        Assert.Equal(42, etat.BloqueSecondes);
    }

    [Fact]
    public async Task Le_code_part_dans_le_corps_jamais_dans_l_adresse()
    {
        var serveur = new Serveur(HttpStatusCode.OK, """{ "user": "Christian", "state": {} }""");

        var resultat = await Api(serveur).CommanderAlarmeAsync(false, "1234");

        Assert.True(resultat.Ok);
        Assert.Equal("Alarme désarmée par Christian.", resultat.Message);
        Assert.Equal("http://192.168.1.10:5000/api/alarm/disarm", serveur.Requete!.RequestUri!.ToString());
        Assert.DoesNotContain("1234", serveur.Requete.RequestUri.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(serveur.Recu!);
        Assert.Equal("1234", json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    // Un code faux est un 403 : surtout pas le message « clé d'API refusée » d'un 401.
    [InlineData(HttpStatusCode.Forbidden, """{ "error": "code incorrect" }""", "Code incorrect")]
    [InlineData(HttpStatusCode.TooManyRequests, """{ "error": "trop de codes faux : clavier bloqué 60 s" }""", "Trop de codes faux : clavier bloqué 60 s")]
    [InlineData(HttpStatusCode.Conflict, """{ "error": "armement refusé : Porte d'entrée", "faults": "Porte d'entrée" }""", "Armement refusé : Porte d'entrée")]
    public async Task Un_refus_rend_le_motif_du_serveur(HttpStatusCode code, string corps, string attendu)
    {
        var resultat = await Api(new Serveur(code, corps)).CommanderAlarmeAsync(true, "0000");

        Assert.False(resultat.Ok);
        Assert.Equal(attendu, resultat.Message);
    }
}
