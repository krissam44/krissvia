using Krissvia.Core.Navigation;

namespace Krissvia.Core.Tests;

public class MenuLayoutTests
{
    private static string Cles(IEnumerable<MenuItemSetting> reglages) =>
        string.Join(",", reglages.Select(r => r.Hidden ? $"({r.Key})" : r.Key));

    [Fact]
    public void Sans_reglage_le_menu_suit_le_catalogue()
    {
        Assert.Equal("a,b,c", Cles(MenuLayout.Arrange(["a", "b", "c"], [])));
    }

    [Fact]
    public void L_ordre_et_le_masquage_enregistres_sont_appliques()
    {
        var reglages = new[] { new MenuItemSetting("c"), new MenuItemSetting("a", Hidden: true), new MenuItemSetting("b") };

        Assert.Equal("c,(a),b", Cles(MenuLayout.Arrange(["a", "b", "c"], reglages)));
    }

    [Fact]
    public void Une_page_nouvelle_arrive_en_fin_de_menu_et_visible()
    {
        var reglages = new[] { new MenuItemSetting("b"), new MenuItemSetting("a") };

        Assert.Equal("b,a,c", Cles(MenuLayout.Arrange(["a", "b", "c"], reglages)));
    }

    [Fact]
    public void L_entree_d_un_service_desactive_disparait()
    {
        var reglages = new[] { new MenuItemSetting("zigbee"), new MenuItemSetting("a"), new MenuItemSetting("b") };

        Assert.Equal("a,b", Cles(MenuLayout.Arrange(["a", "b"], reglages)));
    }

    [Fact]
    public void Un_reglage_en_double_ne_duplique_pas_l_entree()
    {
        var reglages = new[] { new MenuItemSetting("b"), new MenuItemSetting("a"), new MenuItemSetting("b", Hidden: true) };

        Assert.Equal("b,a", Cles(MenuLayout.Arrange(["a", "b"], reglages)));
    }

    [Fact]
    public void Reordonner_les_entrees_disponibles_garde_la_place_d_un_service_desactive()
    {
        // zigbee est désactivé : la personnalisation ne l'a pas vu, il doit retrouver sa deuxième place.
        var enregistres = new[] { new MenuItemSetting("a"), new MenuItemSetting("zigbee"), new MenuItemSetting("b"), new MenuItemSetting("c") };
        var editees = new[] { new MenuItemSetting("c"), new MenuItemSetting("a", Hidden: true), new MenuItemSetting("b") };

        var fusion = MenuLayout.Merge(enregistres, editees);

        Assert.Equal("c,zigbee,(a),b", Cles(fusion));
        Assert.Equal("c,zigbee,(a),b", Cles(MenuLayout.Arrange(["a", "b", "c", "zigbee"], fusion)));
    }

    [Fact]
    public void Une_entree_jamais_enregistree_va_en_fin_a_la_fusion()
    {
        var enregistres = new[] { new MenuItemSetting("a"), new MenuItemSetting("zigbee") };
        var editees = new[] { new MenuItemSetting("d"), new MenuItemSetting("a") };

        Assert.Equal("d,zigbee,a", Cles(MenuLayout.Merge(enregistres, editees)));
    }

    [Fact]
    public void Une_premiere_personnalisation_enregistre_l_ordre_choisi()
    {
        var editees = new[] { new MenuItemSetting("b"), new MenuItemSetting("a", Hidden: true) };

        Assert.Equal("b,(a)", Cles(MenuLayout.Merge([], editees)));
    }
}
