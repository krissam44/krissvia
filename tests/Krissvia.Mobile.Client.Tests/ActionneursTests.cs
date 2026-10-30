using System.Net;
using System.Text;
using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class ActionneursTests
{
    private const string Reponse = """
        {
          "rooms": [
            { "room": "Salon", "actuators": [
                { "entity_id": "light.salon", "name": "Plafonnier", "usage": "eclairage", "on": true, "available": true,
                  "shed": false, "controllable": true },
                { "entity_id": "switch.radiateur_salon", "name": "Radiateur", "usage": "radiateur", "on": false, "available": true,
                  "driven_by": "Salon", "shed": false, "controllable": false } ] },
            { "room": null, "actuators": [
                { "entity_id": "switch.cumulus", "name": "Chauffe-eau", "on": null, "available": false,
                  "scheduled_by": "Heures creuses", "shed": true, "controllable": false } ] }
          ]
        }
        """;

    private sealed class Serveur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        public string? Recu { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Recu = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(code) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
        }
    }

    private static KrissviaApi Api(Serveur serveur) => new(new HttpClient(serveur), new KrissviaApiTests.Connexion("http://192.168.1.10:5000", "k"));

    [Fact]
    public void Les_pieces_et_ce_qui_pilote_chaque_appareil_se_lisent_tels_quels()
    {
        var vue = VueActionneurs.Lire(JsonSerializer.Deserialize<JsonElement>(Reponse))!;

        Assert.Equal(["Salon", null], vue.Pieces.Select(p => p.Nom));
        Assert.Equal(3, vue.Total);
        Assert.Equal(1, vue.Allumes);

        var radiateur = vue.Pieces[0].Actionneurs[1];
        Assert.False(radiateur.Commandable);
        Assert.Equal("Thermostat « Salon »", radiateur.Remarque);

        var cumulus = vue.Pieces[1].Actionneurs[0];
        // Jamais publié : ni allumé ni éteint.
        Assert.Null(cumulus.Allume);
        // L'injoignable l'emporte sur le délestage et la programmation : c'est lui qui explique l'absence d'état.
        Assert.Equal("Injoignable", cumulus.Remarque);
        Assert.Null(vue.Pieces[0].Actionneurs[0].Remarque);
    }

    [Fact]
    public async Task Allumer_part_en_ordre_turn_on()
    {
        var serveur = new Serveur(HttpStatusCode.OK, """{ "outcome": "accepted", "verification": "confirmed" }""");

        var resultat = await Api(serveur).AllumerAsync("light.salon", true);

        Assert.True(resultat.Ok);
        Assert.False(resultat.NonConfirme);
        using var json = JsonDocument.Parse(serveur.Recu!);
        Assert.Equal("turn_on", json.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Un_ordre_accepte_mais_non_confirme_se_dit()
    {
        var serveur = new Serveur(HttpStatusCode.OK, """{ "outcome": "accepted", "verification": "not_confirmed" }""");

        var resultat = await Api(serveur).AllumerAsync("light.salon", false);

        Assert.True(resultat.Ok);
        Assert.True(resultat.NonConfirme);
        Assert.NotNull(resultat.Motif);
    }

    [Fact]
    public async Task Un_appareil_injoignable_rend_le_motif_du_serveur()
    {
        var serveur = new Serveur(HttpStatusCode.OK, """{ "outcome": "unreachable", "verification": "not_sent", "detail": "plugin zigbee2mqtt arrêté" }""");

        var resultat = await Api(serveur).AllumerAsync("light.salon", true);

        Assert.False(resultat.Ok);
        Assert.Equal("plugin zigbee2mqtt arrêté", resultat.Motif);
    }

    [Fact]
    public async Task Un_serveur_trop_ancien_se_reconnait()
    {
        var (vue, _, ancien) = await Api(new Serveur(HttpStatusCode.NotFound, "")).ActionneursAsync();

        Assert.Null(vue);
        Assert.True(ancien);
    }
}
