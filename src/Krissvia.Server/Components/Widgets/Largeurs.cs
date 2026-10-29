using Krissvia.Core.Widgets;

namespace Krissvia.Server.Components.Widgets;

/// <summary>
/// Largeur d'une carte, en 24ᵉ de la grille d'un grand écran (WIDGETS.md §3), dite comme on la pense : « 1/4 »
/// plutôt que « 6 colonnes ».
/// </summary>
public static class Largeurs
{
    /// <summary>
    /// Celles du menu de la carte : les fractions qui tombent juste, de douze à une carte par ligne. 1/12 pour les
    /// petites cartes à icône (un spot, un interrupteur), qu'on aligne en rangée ; 1/8 et 3/8, que seule la grille de
    /// 24 donne (huit cartes par ligne, ou une grande et une petite qui se complètent avec 5/8). Pas de 1/24 : à
    /// 1600 px, 54 px de large, moins qu'un bouton.
    /// </summary>
    public static readonly int[] Usuelles = [2, 3, 4, 6, 8, 9, 12, 15, 16, 18, WidgetCatalog.MaxCols];

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

/// <summary>Hauteur d'une carte, en lignes de la grille, par demi-ligne (WIDGETS.md §3).</summary>
public static class Hauteurs
{
    /// <summary>De 1 à 4 lignes, par demi-ligne.</summary>
    public static readonly double[] Toutes = [.. Enumerable.Range(2, WidgetCatalog.MaxRows * 2 - 1).Select(n => n / 2.0)];

    /// <summary>« 1 ligne », « 1,5 ligne », « 2 lignes ».</summary>
    public static string Libelle(double rows) =>
        $"{rows.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} {(rows >= 2 ? "lignes" : "ligne")}";

    /// <summary>La classe CSS de la hauteur : la grille compte en demi-lignes (<c>h3</c> = 1,5 ligne).</summary>
    public static string Classe(double rows) => $"h{(int)Math.Round(rows * 2)}";
}
