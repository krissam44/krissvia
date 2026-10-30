using System.Text.Json;

namespace Krissvia.Mobile.Client.Tests;

public class ThermostatsEnDirectTests
{
    [Fact]
    public void La_liste_ne_garde_que_les_thermostats_actifs()
    {
        var live = new ThermostatsEnDirect();

        live.Appliquer(Exemples.MessageEntites(
            Exemples.Climat("climate.salon", "Salon"),
            Exemples.Interrupteur,
            Exemples.Climat("climate.ancien", "Supprimé", missing: true),
            Exemples.Climat("climate.garage", "Garage", enabled: false)));

        Assert.Equal(["climate.salon"], live.EntityIds);
    }

    [Fact]
    public void L_etat_publie_se_lit_tel_quel()
    {
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon")));

        var t = Assert.Single(live.Thermostats);

        Assert.Equal("Salon", t.Nom);
        Assert.Equal("Séjour", t.Piece);
        Assert.Equal(2, t.Radiateurs);
        Assert.True(t.APublie);
        Assert.True(t.Disponible);
        Assert.Equal(19.2, t.Temperature);
        Assert.Equal(54, t.Humidite);
        Assert.Equal(8.5, t.Exterieure);
        Assert.Equal(20, t.Consigne);
        Assert.Equal("comfort", t.ModeActif);
        Assert.True(t.PlanningActif);
        Assert.True(t.Chauffe);
        Assert.Equal(0.42, t.Puissance);
        Assert.Equal("heating", t.Statut);
        Assert.Equal("Chauffe", t.LibelleStatut);
        Assert.Equal(0.5, t.PasConsigne);
        Assert.Equal(new DateTime(2026, 10, 30, 22, 0, 0), t.ProchaineBascule!.At);
        Assert.Equal("Éco", t.ProchaineBascule.Label);
        // Les libellés et l'ordre des modes sont ceux du serveur, du plus chaud au plus froid.
        Assert.Equal(["comfort", "comfort1", "comfort2", "eco", "frost_protection", "off"], t.Modes.Select(m => m.Id));
        Assert.Equal("Confort −1", t.Modes[1].Label);
    }

    [Fact]
    public void Jamais_publie_n_est_pas_zero()
    {
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon", etat: null)));

        var t = Assert.Single(live.Thermostats);

        Assert.False(t.APublie);
        Assert.Null(t.Temperature);
        Assert.Null(t.Consigne);
        Assert.False(t.ConsigneReglable);
    }

    [Fact]
    public void Un_message_d_etats_met_a_jour_les_thermostats_connus_seulement()
    {
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon")));
        var notifications = 0;
        live.Changed += () => notifications++;

        var nouvelEtat = Exemples.EtatSalon.Replace("\"temperature\": 19.2", "\"temperature\": 20.1", StringComparison.Ordinal);
        Assert.True(live.Appliquer($$"""{ "type": "states", "states": { "climate.salon": {{nouvelEtat}} } }"""));
        Assert.False(live.Appliquer("""{ "type": "states", "states": { "switch.cave_chauffe_eau": { "value": true, "available": true, "measured_at": "2026-10-30T17:00:00Z" } } }"""));

        Assert.Equal(20.1, Assert.Single(live.Thermostats).Temperature);
        Assert.Equal(["climate.salon"], live.EntityIds);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Une_consigne_ajustee_ne_selectionne_aucun_mode()
    {
        var live = new ThermostatsEnDirect();
        var ajustee = Exemples.EtatSalon.Replace("\"target_adjusted\": false", "\"target_adjusted\": true", StringComparison.Ordinal);
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon", ajustee)));

        var t = Assert.Single(live.Thermostats);

        Assert.True(t.ConsigneAjustee);
        Assert.Equal("comfort", t.Mode);
        Assert.Null(t.ModeActif);
    }

    [Fact]
    public void En_mode_off_la_consigne_ne_se_regle_pas()
    {
        var live = new ThermostatsEnDirect();
        var off = Exemples.EtatSalon.Replace("\"mode\": \"comfort\"", "\"mode\": \"off\"", StringComparison.Ordinal);
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon", off)));

        Assert.False(Assert.Single(live.Thermostats).ConsigneReglable);
    }

    [Fact]
    public void Un_etat_indisponible_garde_ses_dernieres_valeurs()
    {
        var live = new ThermostatsEnDirect();
        var indisponible = Exemples.EtatSalon.Replace("\"available\": true", "\"available\": false", StringComparison.Ordinal);
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon", indisponible)));

        var t = Assert.Single(live.Thermostats);

        Assert.False(t.Disponible);
        Assert.Equal(19.2, t.Temperature);
    }

    [Fact]
    public void Les_thermostats_sont_tries_par_nom()
    {
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon"), Exemples.Climat("climate.bureau", "Bureau")));

        Assert.Equal(["Bureau", "Salon"], live.Thermostats.Select(t => t.Nom));
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("""{ "type": "autre" }""")]
    [InlineData("""[1, 2]""")]
    public void Un_message_inconnu_est_ignore(string message)
    {
        var live = new ThermostatsEnDirect();

        Assert.False(live.Appliquer(message));
        Assert.False(live.Recu);
    }

    [Fact]
    public void La_liste_de_l_api_remplace_celle_du_flux()
    {
        var live = new ThermostatsEnDirect();
        live.Appliquer(Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon")));

        live.Remplacer(JsonSerializer.Deserialize<JsonElement>(Exemples.Liste(Exemples.Climat("climate.bureau", "Bureau"), Exemples.Interrupteur)));

        Assert.Equal(["climate.bureau"], live.EntityIds);
        Assert.True(live.Recu);
    }

    [Theory]
    [InlineData(30, "Éco à 22:00")]
    [InlineData(31, "Éco demain à 22:00")]
    public void La_prochaine_bascule_ne_dit_le_jour_que_s_il_change(int jour, string attendu)
    {
        var bascule = new ProchaineBascule(new DateTime(2026, 10, jour, 22, 0, 0), "eco", "Éco");

        Assert.Equal(attendu, bascule.Texte(new DateTime(2026, 10, 30, 14, 0, 0)));
    }
}
