namespace Krissvia.Mobile.Client.Tests;

public class RadiateursTests
{
    private static Thermostat Lire(string id, string nom, string etat)
    {
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat(id, nom, etat)));
        return Assert.Single(live.Thermostats);
    }

    private static readonly string Repos = Exemples.EtatSalon.Replace("\"heating\": true", "\"heating\": false", StringComparison.Ordinal);

    [Fact]
    public void Ceux_qui_chauffent_passent_en_tete_et_se_comptent()
    {
        var bureau = Lire("climate.bureau", "Bureau", Repos);
        var salon = Lire("climate.salon", "Salon", Exemples.EtatSalon);
        var attente = Lire("climate.attente", "Atelier", "null");

        var ordre = Radiateurs.Ordonner([bureau, attente, salon]);

        Assert.Equal(["Salon", "Atelier", "Bureau"], ordre.Select(t => t.Nom));
        Assert.Equal(1, Radiateurs.EnChauffe([bureau, salon, attente]));
    }

    [Fact]
    public void Les_actions_rapides_sont_confort_eco_arret_avec_les_libelles_du_serveur()
    {
        var salon = Lire("climate.salon", "Salon", Exemples.EtatSalon);

        Assert.Equal(["Confort", "Éco", "Arrêt"], Radiateurs.Actions(salon).Select(m => m.Label));
        Assert.Equal("Confort", salon.LibelleMode);
    }

    [Fact]
    public void Un_mode_que_le_serveur_ne_declare_pas_n_a_pas_de_bouton()
    {
        var sansEco = Exemples.Climat("climate.salon", "Salon").Replace("""{ "id": "eco", "label": "Éco" },""", "", StringComparison.Ordinal);
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(sansEco));

        Assert.Equal(["comfort", "off"], Radiateurs.Actions(Assert.Single(live.Thermostats)).Select(m => m.Id));
    }
}
