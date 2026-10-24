using System.Text.Json;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>Les pages générées pour une Guition 480×480.</summary>
public class MiseEnPageTests
{
    private static List<JsonElement> Objets(Platine platine, Reglages reglages) =>
        [.. MiseEnPage.Generer(platine, reglages).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    private static int Int(JsonElement o, string nom) => o.GetProperty(nom).GetInt32();

    [Fact]
    public void Chaque_ligne_est_un_objet_unique_dans_l_ecran_et_dans_les_polices_de_la_platine()
    {
        var objets = Objets(Donnees.Salon(), Donnees.Reglages());

        Assert.All(objets, o =>
        {
            Assert.InRange(Int(o, "x"), 0, 480);
            Assert.InRange(Int(o, "x") + Int(o, "w"), 0, 480);
            Assert.InRange(Int(o, "y") + Int(o, "h"), 0, 480);
            if (o.TryGetProperty("text_font", out var police)) Assert.Contains(police.GetInt32(), MiseEnPage.Polices);
        });
        var doublons = objets.GroupBy(o => (Int(o, "page"), Int(o, "id"))).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(doublons);
    }

    [Fact]
    public void Le_fond_est_le_premier_objet_de_chaque_page_et_uni()
    {
        var reglages = Donnees.Reglages();
        reglages.Affichage.CouleurFond = "#133B34";
        var objets = Objets(Donnees.Salon(), reglages);

        foreach (var page in new[] { 1, 2, 3 })
        {
            var premier = objets.First(o => Int(o, "page") == page);
            Assert.Equal(global::Krissvia.Plugin.OpenHasp.Objets.Fond, Int(premier, "id"));
            Assert.Equal("#133B34", premier.GetProperty("bg_color").GetString());
            Assert.Equal("#133B34", premier.GetProperty("bg_grad_color").GetString());
            Assert.False(premier.GetProperty("click").GetBoolean());
        }
    }

    [Fact]
    public void Cinq_sondes_font_trois_arcs_puis_deux_centres_comme_la_platine_deployee()
    {
        var arcs = Objets(Donnees.Salon(), Donnees.Reglages()).Where(o => o.GetProperty("obj").GetString() == "arc").ToList();

        // Centrées en hauteur entre le titre (44) et la barre (435) : 25 px au-dessus, 25 px au-dessous.
        Assert.Equal([(15, 69), (170, 69), (325, 69), (92, 244), (247, 244)], arcs.Select(a => (Int(a, "x"), Int(a, "y"))));
        Assert.Equal(-10, Int(arcs[4], "min"));
        Assert.Equal(40, Int(arcs[4], "max"));
    }

    [Fact]
    public void Sans_thermostat_ni_sonde_la_barre_ne_propose_que_l_horloge()
    {
        var platine = Donnees.Salon();
        platine.Thermostat = null;
        platine.Digicode = false;
        var reglages = Donnees.Reglages(platine);
        reglages.Sondes.Clear();

        var objets = Objets(platine, reglages);

        Assert.DoesNotContain(objets, o => Int(o, "page") is 1 or 2);
        var navigation = Assert.Single(objets, o => Int(o, "page") == 0);
        Assert.Equal("p3", navigation.GetProperty("action").GetString());
    }

    [Fact]
    public void Au_plus_trois_interrupteurs_et_seulement_ceux_qui_ont_une_entite()
    {
        var platine = Donnees.Salon();
        platine.Interrupteurs =
        [
            new Interrupteur { Entite = "switch.a" }, new Interrupteur { Entite = "" }, new Interrupteur { Entite = "switch.b" },
            new Interrupteur { Entite = "switch.c" }, new Interrupteur { Entite = "switch.d" },
        ];

        var boutons = Objets(platine, Donnees.Reglages(platine))
            .Where(o => Int(o, "page") == 3 && o.GetProperty("obj").GetString() == "btn").ToList();

        Assert.Equal([10, 11, 12], boutons.Select(b => Int(b, "id")));
    }

    [Fact]
    public void Avec_le_digicode_la_barre_a_quatre_pages_et_les_temperatures_deviennent_sondes()
    {
        var navigation = Objets(Donnees.Salon(), Donnees.Reglages()).Where(o => Int(o, "page") == 0).ToList();

        Assert.Equal(["Thermostat", "Sondes", "Horloge", "Alarme"], navigation.Select(n => n.GetProperty("text").GetString()));
        Assert.Equal(["p1", "p2", "p3", "p4"], navigation.Select(n => n.GetProperty("action").GetString()));
    }

    [Fact]
    public void Le_digicode_a_dix_chiffres_effacer_retour_armer_et_desarmer()
    {
        var boutons = Objets(Donnees.Salon(), Donnees.Reglages())
            .Where(o => Int(o, "page") == 4 && o.GetProperty("obj").GetString() == "btn")
            .ToDictionary(b => Int(b, "id"), b => b.GetProperty("text").GetString());

        Assert.Equal([.. Enumerable.Range(0, 10).Select(d => 10 + d), 20, 21, 30, 31], boutons.Keys.Order());
        Assert.All(Enumerable.Range(0, 10), d => Assert.Equal(d.ToString(), boutons[10 + d]));
        Assert.Equal("Désarmer", boutons[31]);
    }

    [Fact]
    public void Une_seule_rangee_de_sondes_descend_au_milieu_de_l_ecran()
    {
        Assert.Equal([(15, 156), (170, 156), (325, 156)], MiseEnPage.Grille(3).Select(g => (g.X, g.Y)));
    }

    [Fact]
    public void Les_modes_vont_de_Confort_en_haut_a_gauche_a_Off_en_bas_a_droite_avec_leur_icone()
    {
        var modes = Objets(Donnees.Salon(), Donnees.Reglages())
            .Where(o => Int(o, "page") == 1 && Int(o, "id") is >= 10 and <= 15)
            .OrderBy(o => Int(o, "y")).ThenBy(o => Int(o, "x"))
            .Select(o => o.GetProperty("text").GetString())
            .ToList();

        Assert.Equal(
        [
            $"{Icones.Flamme} Confort", $"{Icones.Flamme} Conf. -1", $"{Icones.Flamme} Conf. -2",
            $"{Icones.Feuille} Éco", $"{Icones.Flocon} Hors-gel", $"{Icones.Arret} Off",
        ], modes);
    }

    [Fact]
    public void Le_panneau_de_consigne_est_un_ton_au_dessus_du_fond()
    {
        var reglages = Donnees.Reglages();
        reglages.Affichage.CouleurFond = "#133B34";

        var panneau = Objets(Donnees.Salon(), reglages).Single(o => Int(o, "page") == 1 && Int(o, "id") == global::Krissvia.Plugin.OpenHasp.Objets.Panneau);

        var couleur = panneau.GetProperty("bg_color").GetString()!;
        Assert.True(CouleurOpenHasp.Luminance(couleur) > CouleurOpenHasp.Luminance("#133B34"));
        Assert.Equal(couleur, panneau.GetProperty("bg_grad_color").GetString());   // uni : pas de dégradé du thème
    }

    [Fact]
    public void Le_pourcentage_de_luminosite_est_affiche_des_le_depart()
    {
        var reglages = Donnees.Reglages();
        reglages.Affichage.LuminositeDefaut = 128;

        var pourcentage = Objets(Donnees.Salon(), reglages)
            .Single(o => Int(o, "page") == 3 && Int(o, "id") == global::Krissvia.Plugin.OpenHasp.Objets.Pourcentage);

        Assert.Equal("50 %", pourcentage.GetProperty("text").GetString());
    }

    [Fact]
    public void Le_curseur_de_luminosite_ne_descend_pas_sous_le_plancher()
    {
        var curseur = Objets(Donnees.Salon(), Donnees.Reglages()).Single(o => o.GetProperty("obj").GetString() == "slider");

        Assert.Equal(Luminosite.Minimum, Int(curseur, "min"));
        Assert.True(Luminosite.Minimum >= 20);
    }
}
