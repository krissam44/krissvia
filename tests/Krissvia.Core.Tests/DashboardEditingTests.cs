using Krissvia.Core.Widgets;

namespace Krissvia.Core.Tests;

/// <summary>Déplacer une carte d'une page du tableau de bord vers une autre.</summary>
public class DashboardEditingTests
{
    private static DashboardCard Carte(string id) => new() { Id = id, Entity = $"sensor.{id}", Cols = 6, Title = $"Carte {id}" };

    private static List<Dashboard> Pages() =>
    [
        new() { Id = "accueil", Name = "Accueil", Sections = [new() { Id = "s1", Title = "Salon", Cards = [Carte("a"), Carte("b")] }] },
        new() { Id = "technique", Name = "Technique", Sections = [new() { Id = "t1", Title = "Proxmox", Cards = [Carte("x")] },
                                                                   new() { Id = "t2", Title = "Réseau" }] },
        new() { Id = "vide", Name = "Vide" },
    ];

    private static IReadOnlyList<string> Ids(IReadOnlyList<Dashboard> pages, string page, string section) =>
        [.. pages.Single(p => p.Id == page).Sections.Single(s => s.Id == section).Cards.Select(c => c.Id)];

    [Fact]
    public void La_carte_quitte_sa_page_et_arrive_a_la_fin_de_la_section_choisie_avec_ses_reglages()
    {
        var avant = Pages();
        var pages = DashboardEditing.MoveCardToPage(avant, "accueil", "a", "technique", "t1", () => "neuve");

        Assert.Equal(["b"], Ids(pages, "accueil", "s1"));
        Assert.Equal(["x", "a"], Ids(pages, "technique", "t1"));
        // La carte elle-même, pas une copie : widget, liens, taille, nom gardés.
        Assert.Same(avant[0].Sections[0].Cards[0], pages.Single(p => p.Id == "technique").Sections[0].Cards[1]);
        Assert.Equal(["accueil", "technique", "vide"], pages.Select(p => p.Id));                  // ordre des pages gardé
    }

    [Fact]
    public void Une_page_sans_section_en_recoit_une()
    {
        var pages = DashboardEditing.MoveCardToPage(Pages(), "accueil", "b", "vide", null, () => "neuve");

        var section = Assert.Single(pages.Single(p => p.Id == "vide").Sections);
        Assert.Equal(("neuve", DashboardEditing.MovedCardsSection), (section.Id, section.Title));
        Assert.Equal(["b"], section.Cards.Select(c => c.Id));
    }

    [Fact]
    public void Une_section_disparue_entre_temps_ne_perd_pas_la_carte()
    {
        var pages = DashboardEditing.MoveCardToPage(Pages(), "accueil", "a", "technique", "supprimee", () => "neuve");

        Assert.Equal(["a"], Ids(pages, "technique", "neuve"));
        Assert.Equal(["b"], Ids(pages, "accueil", "s1"));
    }

    [Theory]
    [InlineData("accueil", "inconnue", "technique")]   // carte introuvable
    [InlineData("accueil", "a", "absente")]            // page d'arrivée introuvable
    [InlineData("accueil", "a", "accueil")]            // même page
    public void Rien_ne_change_quand_le_deplacement_est_impossible(string depart, string carte, string arrivee)
    {
        var avant = Pages();
        var apres = DashboardEditing.MoveCardToPage(avant, depart, carte, arrivee, "t1", () => "neuve");

        Assert.Same(avant, apres);
    }
}
