using Krissvia.Climate;

namespace Krissvia.Server.Components.Thermostats;

/// <summary>Libellés et couleurs des thermostats : une seule source pour la liste et la fiche.</summary>
internal static class LibellesThermostat
{
    // Modes et statuts : partagés avec l'état publié aux widgets (ThermostatLabels, module Climate).
    public static IReadOnlyList<ThermostatMode> Modes => ThermostatLabels.Modes;

    public static string Mode(ThermostatMode mode) => ThermostatLabels.Mode(mode);

    public static string Statut(ThermostatStatus statut) => ThermostatLabels.Status(statut);

    public static Color Couleur(ThermostatStatus statut) => statut switch
    {
        ThermostatStatus.SensorLost or ThermostatStatus.UnknownSwitch => Color.Error,
        ThermostatStatus.Inhibited or ThermostatStatus.WindowOpen => Color.Warning,
        ThermostatStatus.Heating => Color.Secondary,
        ThermostatStatus.Coasting => Color.Info,
        _ => Color.Default,
    };

    public static string Motif(string motif) => motif switch
    {
        InhibitionReasons.Delestage => "délestage",
        InhibitionReasons.FenetreOuverte => "fenêtre ouverte",
        InhibitionReasons.SondePerdue => "sonde perdue",
        _ => motif,
    };
}
