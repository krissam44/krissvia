namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>Ce que la platine affiche, à partir des états suivis.</summary>
public class EcranTests
{
    private static readonly DateTime Jeudi = new(2026, 10, 22, 10, 0, 0);

    private static Dictionary<string, string> Attributs(Dictionary<string, Krissvia.Contracts.EntityStateUpdate> etats, Reglages? reglages = null) =>
        Ecran.Attributs(Donnees.Salon(), reglages ?? Donnees.Reglages(), etats, Jeudi).ToDictionary(a => a.Attribut, a => a.Valeur);

    [Fact]
    public void Le_thermostat_montre_ambiante_consigne_mode_statut_et_chauffe()
    {
        var a = Attributs(Donnees.Etats(Donnees.Climat()));

        Assert.Equal("Salon", a["p1b1.text"]);
        Assert.Equal("19.2°", a["p1b2.text"]);
        Assert.Equal("20.0°", a["p1b7.text"]);
        Assert.Equal("Mode: Confort", a["p1b20.text"]);
        Assert.Equal("Statut: Chauffe", a["p1b9.text"]);
        Assert.Equal("0", a["p1b18.hidden"]);
        Assert.Equal("#FF5722", a["p1b15.bg_color"]);   // Confort allumé…
        Assert.Equal("#3A3A3C", a["p1b12.bg_color"]);   // …Éco éteint
        Assert.Equal("#0A84FF", a["p1b21.bg_color"]);   // planning actif
    }

    [Theory]
    [InlineData("comfort1", Icones.Flamme)]
    [InlineData("eco", Icones.Feuille)]
    [InlineData("frost_protection", Icones.Flocon)]
    [InlineData("off", Icones.Arret)]
    public void L_icone_du_mode_suit_le_mode_et_sa_couleur(string mode, string icone)
    {
        var a = Attributs(Donnees.Etats(Donnees.Climat(mode: mode)));

        Assert.Equal(icone, a["p1b16.text"]);
        Assert.Equal(a["p1b20.text_color"], a["p1b16.text_color"]);
        Assert.Equal(a["p1b9.text_color"], a["p1b17.text_color"]);
    }

    [Fact]
    public void Une_consigne_ajustee_n_allume_aucun_mode()
    {
        var a = Attributs(Donnees.Etats(Donnees.Climat(ajustee: true)));

        Assert.Equal("Mode: consigne ajustée", a["p1b20.text"]);
        Assert.All(Objets.Modes, m => Assert.Equal("#3A3A3C", a[$"p1b{m.Objet}.bg_color"]));
    }

    [Fact]
    public void Un_thermostat_indisponible_ne_montre_pas_un_etat_invente()
    {
        var a = Attributs(Donnees.Etats(Donnees.Climat() with { Available = false }));

        Assert.Equal("--.-°", a["p1b2.text"]);
        Assert.Equal("Statut: indisponible", a["p1b9.text"]);
        Assert.Equal("1", a["p1b18.hidden"]);
        Assert.Equal("", a["p3b42.text"]);
    }

    [Fact]
    public void Sur_un_fond_clair_le_texte_du_mode_est_assombri_mais_pas_le_bouton()
    {
        var reglages = Donnees.Reglages();
        reglages.Affichage.CouleurFond = "#F0F0F0";

        var a = Attributs(Donnees.Etats(Donnees.Climat(mode: "comfort2", chauffe: false, statut: "idle")), reglages);

        Assert.NotEqual("#FFD60A", a["p1b20.text_color"]);
        Assert.True(CouleurOpenHasp.Contraste(a["p1b20.text_color"], "#F0F0F0") >= 3);
        Assert.Equal("#FFD60A", a["p1b13.bg_color"]);
    }

    [Fact]
    public void La_prochaine_bascule_s_annonce_sous_l_horloge_avec_un_libelle_de_la_platine()
    {
        var a = Attributs(Donnees.Etats(Donnees.Climat(prochaine: new { at = "2026-10-22T18:00:00", mode = "comfort1", label = "Confort −1" })));

        Assert.Equal("Confort -1 à 18:00", a["p3b42.text"]);
    }

    [Fact]
    public void Date_et_exterieure_partagent_une_ligne()
    {
        var a = Attributs(Donnees.Etats(Donnees.Etat(Donnees.Exterieure, 12.44)));

        Assert.Equal("Jeudi 22 oct. - dehors 12.4°", a["p3b41.text"]);
        Assert.Equal("Jeudi 22 oct.", Ecran.LigneDate(Jeudi, null));
    }

    [Fact]
    public void Les_sondes_remplissent_leur_arc_borne_et_colore()
    {
        var a = Attributs(Donnees.Etats(Donnees.Etat(Donnees.Exterieure, 47.0), Donnees.Etat("sensor.salon_temperature", 17.5)));

        Assert.Equal("Salon", a["p2b1.text"]);
        Assert.Equal("17.5°", a["p2b2.text"]);
        Assert.Equal("18", a["p2b20.val"]);
        Assert.Equal("#66BB6A", a["p2b20.line_color10"]);
        Assert.Equal("40", a["p2b24.val"]);                         // borné au maximum de l'arc
        Assert.Equal("--.-°", a["p2b4.text"]);                      // jamais reçue
        Assert.False(a.ContainsKey("p2b21.line_color10"));
    }

    [Fact]
    public void Un_interrupteur_allume_est_jaune_qu_il_soit_lumiere_ou_prise()
    {
        var a = Attributs(Donnees.Etats(
            Donnees.Etat(Donnees.Lampe, new { on = true, brightness = 40 }), Donnees.Etat(Donnees.Prise, false, nom: "Prise bureau")));

        Assert.Equal("Lampe", a["p3b10.text"]);
        Assert.Equal("#FFD600", a["p3b10.bg_color"]);
        Assert.Equal("Prise bureau", a["p3b11.text"]);            // sans libellé : le nom de l'entité
        Assert.Equal("#3A3A3C", a["p3b11.bg_color"]);
    }
}
