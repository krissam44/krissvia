using Krissvia.Core.Widgets;

namespace Krissvia.Server.Components.Widgets;

/// <summary>
/// Largeur d'une carte, en douzièmes de la grille d'un grand écran (WIDGETS.md §3), dite comme on la pense : « 1/4 »
/// plutôt que « 3 colonnes ».
/// </summary>
public static class Largeurs
{
    /// <summary>Celles du menu de la carte : les fractions qui tombent juste, de six à une carte par ligne.</summary>
    public static readonly int[] Usuelles = [2, 3, 4, 6, 8, 9, WidgetCatalog.MaxCols];

    public static string Libelle(int cols)
    {
        if (cols >= WidgetCatalog.MaxCols) return "Pleine largeur";
        var d = Pgcd(cols, WidgetCatalog.MaxCols);
        var fraction = $"{cols / d}/{WidgetCatalog.MaxCols / d}";
        // Une fraction de numérateur 1 dit combien de cartes tiennent sur une ligne.
        return cols / d == 1 ? $"{fraction} ({WidgetCatalog.MaxCols / cols} par ligne)" : fraction;
    }

    private static int Pgcd(int a, int b) => b == 0 ? a : Pgcd(b, a % b);
}
