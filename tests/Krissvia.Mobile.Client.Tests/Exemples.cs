namespace Krissvia.Mobile.Client.Tests;

/// <summary>Messages tels que le serveur les écrit (EntityApi, ThermostatEntities) : snake_case, énumérations en texte.</summary>
internal static class Exemples
{
    public const string EtatSalon = """
        {
          "value": {
            "temperature": 19.2, "humidity": 54, "outdoor_temperature": 8.5, "target": 20, "target_adjusted": false,
            "mode": "comfort", "schedule_active": true, "heating": true, "power": 0.42, "cycle_seconds": 600,
            "coasting_high": 1, "coasting_low": 0.3, "window_open": false, "status": "heating", "status_label": "Chauffe",
            "away": false, "next_change": { "at": "2026-10-30T22:00:00", "mode": "eco", "label": "Éco" }
          },
          "available": true, "measured_at": "2026-10-30T17:12:03+00:00"
        }
        """;

    public const string Modes = """
        [ { "id": "comfort", "label": "Confort" }, { "id": "comfort1", "label": "Confort −1" },
          { "id": "comfort2", "label": "Confort −2" }, { "id": "eco", "label": "Éco" },
          { "id": "frost_protection", "label": "Hors-gel" }, { "id": "off", "label": "Arrêt" } ]
        """;

    public static string Climat(string id, string nom, string? etat = EtatSalon, bool enabled = true, bool missing = false) => $$"""
        {
          "entity_id": "{{id}}", "name": "{{nom}}", "kind": "climate", "unit": "°C",
          "commands": ["set_mode", "set_target", "adjust_target", "set_schedule"], "room": "Séjour",
          "enabled": {{(enabled ? "true" : "false")}}, "missing": {{(missing ? "true" : "false")}}, "integration_id": "coeur.thermostats",
          "extra": { "modes": {{Modes}}, "min_target": 5, "max_target": 30, "target_step": 0.5, "radiators": 2,
                     "configuration": "/thermostats/salon" },
          "state": {{etat ?? "null"}}
        }
        """;

    public const string Interrupteur = """
        {
          "entity_id": "switch.cave_chauffe_eau", "name": "Chauffe-eau", "kind": "switch", "commands": ["turn_on", "turn_off"],
          "enabled": true, "missing": false, "integration_id": "zigbee2mqtt",
          "state": { "value": true, "available": true, "measured_at": "2026-10-16T07:08:38+00:00" }
        }
        """;

    public static string MessageEntites(params string[] entites) =>
        $$"""{ "type": "entities", "entities": [ {{string.Join(",", entites)}} ] }""";

    public static string Liste(params string[] entites) => $"[ {string.Join(",", entites)} ]";
}
