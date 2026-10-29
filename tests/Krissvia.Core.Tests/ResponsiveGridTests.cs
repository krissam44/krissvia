using Krissvia.Core.Widgets;

namespace Krissvia.Core.Tests;

/// <summary>La largeur d'une carte sur tablette et téléphone : la règle des classes c1 à c12 d'avant les 24 colonnes.</summary>
public class ResponsiveGridTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]     // 1/12 : une icône, deux par ligne de téléphone
    [InlineData(3, 1, 1)]     // 1/8
    [InlineData(4, 1, 1)]     // 1/6
    [InlineData(5, 2, 2)]
    [InlineData(6, 2, 2)]     // 1/4
    [InlineData(8, 2, 2)]     // 1/3
    [InlineData(9, 3, 2)]     // 3/8
    [InlineData(12, 3, 2)]    // 1/2
    [InlineData(13, 4, 2)]
    [InlineData(24, 4, 2)]
    public void Une_carte_prend_sur_petit_ecran_la_part_qui_lui_ressemble(int cols, int tablette, int telephone)
    {
        Assert.Equal(tablette, ResponsiveGrid.Tablet(cols));
        Assert.Equal(telephone, ResponsiveGrid.Phone(cols));
    }

    [Fact]
    public void Les_trois_largeurs_partent_en_variables_CSS() =>
        Assert.Equal("--c: 9; --ct: 3; --cp: 2", ResponsiveGrid.Style(9));
}
