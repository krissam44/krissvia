using MudBlazor;

namespace Krissvia.Mobile.Components;

/// <summary>
/// Couleurs des températures et des piles de l'écran Sondes : celles de l'écran web (<c>Krissvia.Server.Components.Echelle</c>),
/// pour qu'une pièce ait la même couleur sur le téléphone et sur le PC.
/// </summary>
internal static class Echelle
{
    public const string Eteinte = "#78909c";

    public static string Couleur(double temperature) => temperature switch
    {
        < 16 => "#42a5f5",
        < 20 => "#66bb6a",
        < 25 => "#ffa726",
        _ => "#ef5350",
    };

    public static string CouleurPile(double pourcentage) => pourcentage switch
    {
        >= 50 => "#66bb6a",
        >= 20 => "#ffa726",
        _ => "#ef5350",
    };

    public static string IconePile(double pourcentage) => pourcentage switch
    {
        >= 80 => Icons.Material.Filled.BatteryFull,
        >= 50 => Icons.Material.Filled.Battery5Bar,
        >= 20 => Icons.Material.Filled.Battery2Bar,
        _ => Icons.Material.Filled.BatteryAlert,
    };
}
