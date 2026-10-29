namespace Krissvia.Core.Widgets;

/// <summary>
/// La largeur d'une carte selon l'écran. Sur grand écran, ses 24ᵉ tels quels ; sur tablette, la grille n'a que
/// <see cref="TabletColumns"/> colonnes et sur téléphone <see cref="PhoneColumns"/> : une carte y prend la part qui
/// lui ressemble le plus, arrondie au-dessus, sans jamais dépasser la ligne. C'est la règle des classes CSS d'avant
/// les 24 colonnes (c1 à c12), écrite une fois ici au lieu de vingt-quatre fois dans la feuille de style.
/// </summary>
public static class ResponsiveGrid
{
    public const int TabletColumns = 4;
    public const int PhoneColumns = 2;

    /// <summary>Jusqu'à 1/6 : une colonne de tablette ; jusqu'à 1/3 : deux ; jusqu'à 1/2 : trois ; au-delà, la ligne.</summary>
    public static int Tablet(int cols) => cols switch { <= 4 => 1, <= 8 => 2, <= 12 => 3, _ => TabletColumns };

    /// <summary>Jusqu'à 1/6 : une demi-ligne de téléphone (deux icônes côte à côte) ; au-delà, la ligne.</summary>
    public static int Phone(int cols) => cols <= 4 ? 1 : PhoneColumns;

    /// <summary>Les trois largeurs, en variables CSS : la feuille de style choisit celle de l'écran.</summary>
    public static string Style(int cols) => $"--c: {cols}; --ct: {Tablet(cols)}; --cp: {Phone(cols)}";
}
