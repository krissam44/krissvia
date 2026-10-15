using Krissvia.Climate;

namespace Krissvia.Server.Components.Thermostats;

/// <summary>Libellés et couleurs des thermostats : une seule source pour la liste et la fiche.</summary>
internal static class LibellesThermostat
{
    /// <summary>Du plus chaud au plus froid : l'ordre des boutons de mode.</summary>
    public static readonly ThermostatMode[] Modes =
    [
        ThermostatMode.Comfort, ThermostatMode.Comfort1, ThermostatMode.Comfort2,
        ThermostatMode.Eco, ThermostatMode.FrostProtection, ThermostatMode.Off,
    ];

    public static string Mode(ThermostatMode mode) => mode switch
    {
        ThermostatMode.Comfort => "Confort",
        ThermostatMode.Comfort1 => "Confort −1",
        ThermostatMode.Comfort2 => "Confort −2",
        ThermostatMode.Eco => "Éco",
        ThermostatMode.FrostProtection => "Hors-gel",
        _ => "Arrêt",
    };

    public static string Statut(ThermostatStatus statut) => statut switch
    {
        ThermostatStatus.SensorLost => "Sonde perdue",
        ThermostatStatus.UnknownSwitch => "Radiateur inconnu",
        ThermostatStatus.Inhibited => "Délesté",
        ThermostatStatus.WindowOpen => "Fenêtre ouverte",
        ThermostatStatus.Coasting => "Inertie",
        ThermostatStatus.Off => "Arrêté",
        ThermostatStatus.Heating => "Chauffe",
        _ => "Attente",
    };

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
