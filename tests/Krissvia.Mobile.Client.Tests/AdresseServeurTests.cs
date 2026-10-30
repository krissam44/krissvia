namespace Krissvia.Mobile.Client.Tests;

public class AdresseServeurTests
{
    [Theory]
    // Le réflexe de l'utilisateur sur DomoMud : pointer sur /api. Sans correction, /api/api/… et un 404 déroutant.
    [InlineData("http://192.168.1.10:5000/api", "http://192.168.1.10:5000")]
    [InlineData("http://192.168.1.10:5000/api/", "http://192.168.1.10:5000")]
    [InlineData("  https://maison.exemple.fr/  ", "https://maison.exemple.fr")]
    // Sans schéma : http sur le réseau local, où il n'y a pas de certificat ; https ailleurs.
    [InlineData("192.168.1.10:5000", "http://192.168.1.10:5000")]
    [InlineData("localhost:5000", "http://localhost:5000")]
    [InlineData("krissvia:5000", "http://krissvia:5000")]
    [InlineData("krissvia.local", "http://krissvia.local")]
    [InlineData("maison.exemple.fr/api", "https://maison.exemple.fr")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalise_la_saisie(string? saisie, string attendu) =>
        Assert.Equal(attendu, AdresseServeur.Normaliser(saisie));

    [Fact]
    public void Normaliser_est_stable() =>
        // Appliquée à l'enregistrement puis à chaque lecture : une deuxième passe ne doit rien changer.
        Assert.Equal("http://192.168.1.10:5000", AdresseServeur.Normaliser(AdresseServeur.Normaliser("192.168.1.10:5000/api/")));

    [Theory]
    [InlineData("http://192.168.1.10:5000", "ws://192.168.1.10:5000/api/live?entities=climate.salon,climate.chambre")]
    [InlineData("https://maison.exemple.fr/api", "wss://maison.exemple.fr/api/live?entities=climate.salon,climate.chambre")]
    public void Flux_en_ws_ou_wss_restreint_aux_thermostats(string serveur, string attendu) =>
        Assert.Equal(attendu, AdresseServeur.Flux(serveur, ["climate.salon", "climate.chambre"]).ToString());
}
