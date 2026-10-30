using MudBlazor;

namespace Krissvia.Mobile.Components.Thermostats;

/// <summary>
/// Couleurs et icônes de la carte, reprises du widget <c>thermostat</c> du tableau de bord (thermostat.js) pour que le
/// téléphone et le web se lisent pareil. Les TEXTES, eux, viennent tous du serveur : ici on ne fait que colorer, à
/// partir des codes (<c>heating</c>, <c>eco</c>…), jamais des libellés.
/// </summary>
internal static class PresentationThermostat
{
    public const string Chauffe = "#ff5722";
    public const string Inertie = "#26c6da";
    public const string Planning = "#00b4d8";
    public const string Gris = "#78909c";

    public static string CouleurMode(string? mode) => mode switch
    {
        "comfort" => "#ff5722",
        "comfort1" => "#ffa726",
        "comfort2" => "#ffd60a",
        "eco" => "#66bb6a",
        "frost_protection" => "#42a5f5",
        "off" => "#546e7a",
        _ => Gris,
    };

    /// <summary>Une icône par mode ; les deux paliers Confort s'écrivent « −1 » / « −2 » (<see cref="Palier"/>).</summary>
    public static string IconeMode(string mode) => mode switch
    {
        "comfort" or "comfort1" or "comfort2" => Icons.Material.Filled.LocalFireDepartment,
        "eco" => Icons.Material.Filled.EnergySavingsLeaf,
        "frost_protection" => Icons.Material.Filled.AcUnit,
        "off" => Icons.Material.Filled.PowerSettingsNew,
        _ => Icons.Material.Filled.Thermostat,
    };

    /// <summary>« −1 » et « −2 » parlent mieux qu'une flamme répétée trois fois (choix de l'appli DomoMud).</summary>
    public static string? Palier(string mode) => mode switch
    {
        "comfort1" => "−1",
        "comfort2" => "−2",
        _ => null,
    };

    public static string CouleurStatut(string? statut) => statut switch
    {
        "heating" => "#ff5722",
        "preheating" => "#ab47bc",
        "coasting" => Inertie,
        "idle" => "#66bb6a",
        "off" => "#546e7a",
        "inhibited" => "#ffa726",
        "window_open" => "#42a5f5",
        "sensor_lost" or "unknown_switch" => "#ef5350",
        _ => Gris,
    };

    /// <summary>L'ambiante par paliers, comme DomoMud : froid, bien, tiède, chaud.</summary>
    public static string CouleurAmbiante(double? t) => t switch
    {
        null => "var(--mud-palette-text-secondary)",
        < 14 => "#42a5f5",
        < 18 => "#66bb6a",
        < 22 => "#ffa726",
        _ => "#ef5350",
    };

    public static string Nombre(double? valeur, int decimales = 1) =>
        valeur is { } v ? v.ToString("F" + decimales, System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) : "—";
}
