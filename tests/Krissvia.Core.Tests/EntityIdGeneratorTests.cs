using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Tests;

public class EntityIdGeneratorTests
{
    [Theory]
    [InlineData("Prise salon", "prise_salon")]
    [InlineData("Radiateur Chambre RdC", "radiateur_chambre_rdc")]
    [InlineData("Cuisine éclairée", "cuisine_eclairee")]
    [InlineData("  Sonde -- Cœur (étage 1) ", "sonde_coeur_etage_1")]
    [InlineData("---", "entite")]
    public void Le_slug_est_en_minuscules_sans_accents_ni_separateurs_parasites(string texte, string attendu)
    {
        Assert.Equal(attendu, EntityIdGenerator.Slugify(texte));
    }

    [Fact]
    public void L_identifiant_commence_par_la_famille_de_l_entite()
    {
        var id = EntityIdGenerator.Generate(EntityKind.BinarySensor, "Porte garage", null, new HashSet<string>());

        Assert.Equal("binary_sensor.porte_garage", id);
    }

    [Fact]
    public void Un_identifiant_deja_pris_recoit_un_numero()
    {
        var pris = new HashSet<string> { "switch.prise_salon", "switch.prise_salon_2" };

        var id = EntityIdGenerator.Generate(EntityKind.Switch, "Prise salon", null, pris);

        Assert.Equal("switch.prise_salon_3", id);
    }

    [Fact]
    public void Le_suffixe_est_ajoute_au_nom_de_l_appareil()
    {
        var id = EntityIdGenerator.Generate(EntityKind.Switch, "Interrupteur entrée", "Gauche", new HashSet<string>());

        Assert.Equal("switch.interrupteur_entree_gauche", id);
    }
}
