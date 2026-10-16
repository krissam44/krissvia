namespace Krissvia.Climate.Thermostats;

/// <summary>
/// Libellés des modes et des statuts : une seule source pour les écrans, l'API et l'état publié aux widgets. Deux
/// endroits qui calculent le même libellé finissent toujours par se contredire (le statut recalculé par l'appli
/// mobile de DomoMud).
/// </summary>
public static class ThermostatLabels
{
    /// <summary>Du plus chaud au plus froid : l'ordre des boutons de mode.</summary>
    public static readonly IReadOnlyList<ThermostatMode> Modes =
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

    public static string Status(ThermostatStatus status) => status switch
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
}
