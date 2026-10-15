namespace Krissvia.Server.Components.Thermostats;

/// <summary>Une couleur par mode, du plus chaud au plus froid : l'aperçu du planning se lit d'un coup d'œil.</summary>
internal static class CouleursModes
{
    public static string Fond(ThermostatMode mode) => mode switch
    {
        ThermostatMode.Comfort => "#e4572e",
        ThermostatMode.Comfort1 => "#f08a4b",
        ThermostatMode.Comfort2 => "#f6b26b",
        ThermostatMode.Eco => "#4f8fc0",
        ThermostatMode.FrostProtection => "#9cc9e8",
        _ => "#b0b4bd",
    };
}
