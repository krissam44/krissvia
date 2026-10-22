using Krissvia.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>Les appuis sur la platine, traduits en ordres ; et le filtre de republication.</summary>
public class EvenementsTests
{
    private static Reaction? Appui(string objet, string charge = """{"event":"up"}""", params EntityStateUpdate[] etats) =>
        Evenements.Interpreter(Donnees.Salon(), Donnees.Reglages(), objet, charge, Donnees.Etats(etats));

    [Fact]
    public void Un_bouton_de_mode_choisit_le_mode_au_relache_seulement()
    {
        var ordre = Assert.IsType<Ordre>(Appui("p1b10"));
        Assert.Equal((Donnees.Thermostat, StandardCommands.SetMode), (ordre.Entite, ordre.Commande));
        Assert.Equal("off", ordre.Args!.Value.GetProperty("mode").GetString());

        Assert.Null(Appui("p1b10", """{"event":"down"}"""));
    }

    [Fact]
    public void Plus_et_moins_ajustent_la_consigne_d_un_demi_degre()
    {
        Assert.Equal(-0.5, Assert.IsType<Ordre>(Appui("p1b5")).Args!.Value.GetProperty("delta").GetDouble());
        Assert.Equal(0.5, Assert.IsType<Ordre>(Appui("p1b8")).Args!.Value.GetProperty("delta").GetDouble());
    }

    [Fact]
    public void Le_bouton_planning_inverse_l_etat_publie()
    {
        Assert.False(Assert.IsType<Ordre>(Appui("p1b21", etats: Donnees.Climat(planning: true))).Args!.Value.GetProperty("active").GetBoolean());
        Assert.True(Assert.IsType<Ordre>(Appui("p1b21", etats: Donnees.Climat(planning: false))).Args!.Value.GetProperty("active").GetBoolean());
    }

    [Fact]
    public void Un_interrupteur_s_eteint_s_il_est_allume_et_s_allume_sinon()
    {
        Assert.Equal(StandardCommands.TurnOff, Assert.IsType<Ordre>(Appui("p3b10", etats: Donnees.Etat(Donnees.Lampe, new { on = true }))).Commande);
        Assert.Equal(StandardCommands.TurnOn, Assert.IsType<Ordre>(Appui("p3b11", etats: Donnees.Etat(Donnees.Prise, false))).Commande);
        Assert.Equal(Donnees.Prise, Assert.IsType<Ordre>(Appui("p3b11")).Entite);
        Assert.Null(Appui("p3b12"));   // pas de troisième interrupteur sur cette platine
    }

    [Fact]
    public void Le_curseur_suit_le_doigt_sans_descendre_sous_le_plancher()
    {
        Assert.Equal(new ChangerLuminosite(120), Appui("p3b20", """{"event":"changed","val":120}"""));
        Assert.Equal(new ChangerLuminosite(Luminosite.Minimum), Appui("p3b20", """{"event":"up","val":1}"""));
    }

    [Fact]
    public void La_veille_est_comprise_sous_ses_deux_formes()
    {
        Assert.Equal(new Veille("long"), Appui("idle", "long"));
        Assert.Equal(new Veille("off"), Appui("idle", """{"idle":"OFF"}"""));
        Assert.Null(Appui("p1b10", "pas du json"));
    }

    [Fact]
    public void Les_touches_du_digicode_ne_portent_que_la_touche_jamais_le_code()
    {
        Assert.Equal(new ToucheClavier("7"), Appui("p4b17"));
        Assert.Equal(new ToucheClavier("0"), Appui("p4b10"));
        Assert.Equal(new ToucheClavier("C"), Appui("p4b20"));
        Assert.Equal(new ToucheClavier("<"), Appui("p4b21"));
        Assert.Equal(new CommandeAlarme(StandardCommands.Arm), Appui("p4b30"));
        Assert.Equal(new CommandeAlarme(StandardCommands.Disarm), Appui("p4b31"));
        Assert.Null(Appui("p4b17", """{"event":"down"}"""));
    }

    [Fact]
    public void Sans_digicode_les_touches_de_la_page_4_ne_font_rien()
    {
        var platine = Donnees.Salon();
        platine.Digicode = false;

        Assert.Null(Evenements.Interpreter(platine, Donnees.Reglages(), "p4b31", """{"event":"up"}""", Donnees.Etats()));
        var sansAlarme = Donnees.Reglages();
        sansAlarme.Alarme.Entite = null;
        Assert.Null(Evenements.Interpreter(Donnees.Salon(), sansAlarme, "p4b31", """{"event":"up"}""", Donnees.Etats()));
    }

    [Fact]
    public void Une_valeur_identique_ne_repart_qu_apres_deux_minutes_et_un_redemarrage_n_oublie_que_sa_platine()
    {
        var horloge = new FakeTimeProvider();
        var filtre = new Republication(horloge);
        filtre.Noter("appart1", "p1b2.text", "19.5°");
        filtre.Noter("appart2", "p1b2.text", "19.5°");

        Assert.False(filtre.AEnvoyer("appart1", "p1b2.text", "19.5°"));
        Assert.True(filtre.AEnvoyer("appart1", "p1b2.text", "19.6°"));
        horloge.Advance(Republication.RafraichissementForce);
        Assert.True(filtre.AEnvoyer("appart1", "p1b2.text", "19.5°"));

        filtre.Noter("appart1", "p1b2.text", "19.5°");
        filtre.Noter("appart2", "p1b2.text", "19.5°");
        filtre.OublierNoeud("appart1");
        Assert.True(filtre.AEnvoyer("appart1", "p1b2.text", "19.5°"));
        Assert.False(filtre.AEnvoyer("appart2", "p1b2.text", "19.5°"));
    }
}
