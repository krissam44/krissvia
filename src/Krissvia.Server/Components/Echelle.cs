using System.Globalization;

namespace Krissvia.Server.Components;

/// <summary>
/// Couleurs des températures et des batteries : une seule échelle pour toutes les cartes. DomoMud en avait deux
/// (17/21 °C pour les groupes, 16/20 °C pour les sondes), si bien qu'une pièce et sa sonde ne prenaient pas
/// toujours la même couleur.
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

    public static string Nombre(double valeur, int decimales = 1) =>
        valeur.ToString(decimales == 0 ? "0" : "0.0", CultureInfo.GetCultureInfo("fr-FR"));
}
