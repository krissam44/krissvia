namespace Krissvia.Mobile.Client.Tests;

public class LieuxTests
{
    [Fact]
    public void Le_premier_lieu_ajoute_est_affiche_les_suivants_non()
    {
        var carnet = new CarnetLieux();

        var appart = carnet.Ajouter("Appartement", "192.168.10.20");
        carnet.Ajouter("Maison", "http://192.168.1.10:12795/api");

        Assert.Equal(appart, carnet.Actif);
        Assert.Equal("http://192.168.10.20", appart.BaseUrl);
        Assert.Equal("http://192.168.1.10:12795", carnet.Lieux[1].BaseUrl);
    }

    [Fact]
    public void Le_carnet_se_relit_avec_le_lieu_affiche()
    {
        var carnet = new CarnetLieux();
        carnet.Ajouter("Appartement", "192.168.10.20");
        var maison = carnet.Ajouter("Maison", "192.168.1.10:12795");
        Assert.True(carnet.Choisir(maison.Id));

        var (relu, repris) = CarnetLieux.Lire(carnet.Ecrire(), ancienneAdresse: "192.168.1.99");

        Assert.Null(repris);   // un carnet existe : l'ancienne adresse ne crée rien
        Assert.Equal(["Appartement", "Maison"], relu.Lieux.Select(l => l.Nom));
        Assert.Equal(maison, relu.Actif);
    }

    [Fact]
    public void L_ancien_reglage_devient_le_premier_lieu()
    {
        var (carnet, repris) = CarnetLieux.Lire(null, "192.168.1.10:12795");

        Assert.NotNull(repris);
        Assert.Equal(repris, carnet.Actif);
        Assert.Equal("http://192.168.1.10:12795", repris.BaseUrl);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("pas du json", null)]
    public void Sans_carnet_ni_ancien_reglage_il_n_y_a_aucun_lieu(string? json, string? ancienne)
    {
        var (carnet, repris) = CarnetLieux.Lire(json, ancienne);

        Assert.Null(repris);
        Assert.Empty(carnet.Lieux);
        Assert.Null(carnet.Actif);
    }

    [Fact]
    public void Supprimer_le_lieu_affiche_passe_au_suivant()
    {
        var carnet = new CarnetLieux();
        var appart = carnet.Ajouter("Appartement", "192.168.10.20");
        var maison = carnet.Ajouter("Maison", "192.168.1.10");

        carnet.Supprimer(appart.Id);

        Assert.Equal(maison, carnet.Actif);
        carnet.Supprimer(maison.Id);
        Assert.Null(carnet.Actif);
    }

    [Fact]
    public void Modifier_le_lieu_affiche_le_garde_a_jour()
    {
        var carnet = new CarnetLieux();
        var appart = carnet.Ajouter("Appart", "192.168.10.20:12795");

        carnet.Modifier(appart.Id, "Appartement", "192.168.10.20");

        Assert.Equal(new Lieu(appart.Id, "Appartement", "http://192.168.10.20"), carnet.Actif);
    }

    [Fact]
    public void Un_nom_vide_prend_un_nom_par_defaut()
    {
        var carnet = new CarnetLieux();
        carnet.Ajouter("Maison", "a");

        Assert.Equal("Lieu 2", carnet.Ajouter("  ", "b").Nom);
    }

    [Fact]
    public void Choisir_le_lieu_deja_affiche_ne_change_rien()
    {
        var carnet = new CarnetLieux();
        var appart = carnet.Ajouter("Appartement", "a");

        Assert.False(carnet.Choisir(appart.Id));
    }

    [Fact]
    public void Vider_les_thermostats_oublie_la_liste_du_lieu_precedent()
    {
        var thermostats = new ThermostatsEnDirect();
        thermostats.Remplacer(System.Text.Json.JsonDocument.Parse("""[{ "entity_id": "climate.salon", "kind": "climate", "name": "Salon", "enabled": true }]""").RootElement);
        Assert.True(thermostats.Recu);

        thermostats.Vider();

        Assert.False(thermostats.Recu);
        Assert.Empty(thermostats.Thermostats);
    }
}
