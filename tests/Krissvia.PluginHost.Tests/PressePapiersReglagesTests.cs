using System.Text.Json.Nodes;
using Krissvia.PluginHost.Services;

namespace Krissvia.PluginHost.Tests;

/// <summary>Copier une liste de réglages (la page perso d'une platine) et la coller sous la même liste d'un autre élément.</summary>
public class PressePapiersReglagesTests
{
    private static SettingField Liste(string cle = "AffichagesPerso", int? plafond = null, params string[] champs) => new()
    {
        Key = cle,
        Type = SettingType.Array,
        Title = cle,
        MaxItems = plafond,
        Properties = [.. (champs.Length > 0 ? champs : ["Objet", "Entite"]).Select(c => new SettingField { Key = c, Type = SettingType.String, Title = c })],
    };

    private static JsonArray Elements(int n) => [.. Enumerable.Range(1, n).Select(i => (JsonNode)new JsonObject { ["Objet"] = $"{10 + i}", ["Entite"] = $"binary_sensor.c{i}" })];

    [Fact]
    public void Ce_qui_est_copie_se_colle_dans_la_meme_liste_tel_quel()
    {
        var pp = new PressePapiersReglages();
        pp.Copier(Liste(), Elements(3));

        var collees = pp.Coller(Liste());

        Assert.Equal(3, pp.Disponible(Liste()));
        Assert.Equal(["binary_sensor.c1", "binary_sensor.c2", "binary_sensor.c3"], collees.Select(e => e["Entite"]!.GetValue<string>()));
    }

    [Fact]
    public void Rien_ne_se_colle_dans_une_autre_liste()
    {
        var pp = new PressePapiersReglages();
        Assert.Null(pp.Disponible(Liste()));   // rien de copié
        Assert.Empty(pp.Coller(Liste()));

        pp.Copier(Liste(), Elements(2));

        Assert.Null(pp.Disponible(Liste("Sondes")));                       // autre nom
        Assert.Null(pp.Disponible(Liste("AffichagesPerso", null, "Objet")));   // même nom, autres champs
        Assert.Empty(pp.Coller(Liste("Sondes")));
    }

    [Fact]
    public void La_copie_ne_suit_pas_la_liste_et_chaque_collage_est_neuf()
    {
        var pp = new PressePapiersReglages();
        var source = Elements(2);
        pp.Copier(Liste(), source);

        source.RemoveAt(0);   // modifiée après la copie
        var premier = pp.Coller(Liste());
        premier[0]["Entite"] = "changé";   // collée puis modifiée
        var second = pp.Coller(Liste());

        Assert.Equal(2, premier.Count);
        Assert.Equal("binary_sensor.c1", second[0]["Entite"]!.GetValue<string>());
    }

    [Fact]
    public void Un_collage_respecte_le_plafond_de_la_liste()
    {
        var pp = new PressePapiersReglages();
        pp.Copier(Liste(plafond: 3), Elements(5));

        Assert.Equal(3, pp.Coller(Liste(plafond: 3)).Count);
    }
}
