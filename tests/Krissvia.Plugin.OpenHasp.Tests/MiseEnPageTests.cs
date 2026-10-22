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
            if (o.TryGetProperty("text_font", out var police)) Assert.Contains(police.GetInt32(), new[] { 16, 24, 32, 48 });
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

        Assert.Equal([(15, 40), (170, 40), (325, 40), (92, 215), (247, 215)], arcs.Select(a => (Int(a, "x"), Int(a, "y"))));
        Assert.Equal(-10, Int(arcs[4], "min"));
        Assert.Equal(40, Int(arcs[4], "max"));
    }

    [Fact]
    public void Sans_thermostat_ni_sonde_la_barre_ne_propose_que_l_horloge()
    {
        var platine = Donnees.Salon();
        platine.Thermostat = null;
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
    public void Le_curseur_de_luminosite_ne_descend_pas_sous_le_plancher()
    {
        var curseur = Objets(Donnees.Salon(), Donnees.Reglages()).Single(o => o.GetProperty("obj").GetString() == "slider");

        Assert.Equal(Luminosite.Minimum, Int(curseur, "min"));
        Assert.True(Luminosite.Minimum >= 20);
    }
}
