using System.Net;
using System.Text;
using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class PresenceTests
{
    private const string Reponse = """
        {
          "state": { "away": false, "source": "none" },
          "period": { "departure": "2026-12-20T08:00:00+01:00", "return": "2026-12-27T18:00:00+01:00", "cancel_on_presence": true,
                      "cancelled": false, "departure_seen": false },
          "presence": { "occupied": true, "detection_lost": false, "tracked": 2, "present": ["Redmi-Note-11"], "absent": ["Xiaomi-14T"],
                        "unknown": [] }
        }
        """;

    private sealed class Serveur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Requete, string? Corps)> Recues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Recues.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
        }
    }

    private static EtatPresence Lire(string json) => EtatPresence.Lire(JsonSerializer.Deserialize<JsonElement>(json))!;

    private static KrissviaApi Api(Serveur serveur) =>
        new(new HttpClient(serveur), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

    [Fact]
    public void La_presence_se_lit_avec_ses_appareils_et_la_periode_a_venir()
    {
        var p = Lire(Reponse);

        Assert.False(p.Absente);
        // « forced » absent du JSON (le serveur omet les null) : automatique.
        Assert.Equal(Forcage.Automatique, p.Forcage);
        Assert.Equal("Maison occupée", p.Libelle);
        Assert.Equal(["Redmi-Note-11"], p.Presents);
        Assert.Equal(["Xiaomi-14T"], p.Absents);
        Assert.Equal(2, p.Suivis);
        Assert.Equal(new DateTimeOffset(2026, 12, 27, 18, 0, 0, TimeSpan.FromHours(1)), p.Periode!.Retour);
    }

    [Theory]
    [InlineData("""{ "state": { "away": true, "source": "manual" }, "forced": true }""", "Absence forcée", Forcage.Absent)]
    [InlineData("""{ "state": { "away": false, "source": "manual" }, "forced": false }""", "Présence forcée", Forcage.Present)]
    [InlineData("""{ "state": { "away": true, "source": "scheduled", "return": "2026-12-27T18:00:00+01:00" } }""", "Absence programmée", Forcage.Automatique)]
    [InlineData("""{ "state": { "away": true, "source": "detection" }, "presence": { "absent": ["A"] } }""", "Maison vide", Forcage.Automatique)]
    [InlineData("""{ "state": { "away": false, "source": "none" }, "presence": { "detection_lost": true, "unknown": ["A"] } }""", "Présence inconnue", Forcage.Automatique)]
    public void Chaque_situation_a_son_libelle(string json, string libelle, Forcage forcage)
    {
        var p = Lire(json);

        Assert.Equal(libelle, p.Libelle);
        Assert.Equal(forcage, p.Forcage);
    }

    [Fact]
    public void Une_periode_annulee_n_est_plus_annoncee()
    {
        var p = Lire("""
            { "state": { "away": false, "source": "none" },
              "period": { "departure": "2026-12-20T08:00:00+01:00", "return": "2026-12-27T18:00:00+01:00", "cancelled": true } }
            """);

        Assert.Null(p.Periode);
    }

    [Theory]
    [InlineData(Forcage.Absent, "true")]
    [InlineData(Forcage.Present, "false")]
    [InlineData(Forcage.Automatique, "null")]
    public async Task Le_forcage_part_en_put_avec_away(Forcage forcage, string away)
    {
        var serveur = new Serveur(HttpStatusCode.OK, """{ "away": true, "source": "manual" }""");

        var resultat = await Api(serveur).ForcerAbsenceAsync(forcage);

        Assert.True(resultat.Ok);
        var (requete, corps) = Assert.Single(serveur.Recues);
        Assert.Equal(HttpMethod.Put, requete.Method);
        Assert.Equal("http://192.168.1.10:5000/api/absence/forced", requete.RequestUri!.ToString());
        using var json = JsonDocument.Parse(corps!);
        Assert.Equal(away, json.RootElement.GetProperty("away").GetRawText());
    }

    [Fact]
    public async Task Un_refus_du_serveur_rend_son_motif()
    {
        var serveur = new Serveur(HttpStatusCode.BadRequest, """{ "error": "attendu : away" }""");

        var resultat = await Api(serveur).ForcerAbsenceAsync(Forcage.Absent);

        Assert.False(resultat.Ok);
        Assert.Equal("attendu : away", resultat.Motif);
    }
}
