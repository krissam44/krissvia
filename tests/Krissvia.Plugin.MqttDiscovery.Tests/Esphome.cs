namespace Krissvia.Plugin.MqttDiscovery.Tests;

/// <summary>
/// Ce que publie un module ESPHome avec <c>discovery: true</c> : la forme abrégée, le <c>~</c>, et — piège du générateur
/// d'identifiants par défaut — le MÊME <c>uniq_id</c> pour la même sonde de deux modules.
/// </summary>
internal static class Esphome
{
    public const string Prefixe = "homeassistant";

    public static string TopicConfig(string noeud, string composant, string objet) => $"{Prefixe}/{composant}/{noeud}/{objet}/config";

    public static string Base(string noeud) => $"krissvia/{noeud}";

    public static string TopicEtat(string noeud, string composant, string objet) => $"{Base(noeud)}/{composant}/{objet}/state";

    public static string TopicStatut(string noeud) => $"{Base(noeud)}/status";

    private static string Appareil(string noeud, string mac) =>
        $$"""{"ids":"{{mac}}","name":"{{noeud}}","sw":"2024.10.0 (ESP-IDF: 5.1.4)","mdl":"esp32dev","mf":"Espressif"}""";

    public static string Temperature(string noeud, string mac) =>
        $$"""{"~":"{{Base(noeud)}}","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement","name":"Température","stat_t":"~/sensor/temperature/state","avty_t":"~/status","uniq_id":"ESPsensortemperature","dev":{{Appareil(noeud, mac)}}}""";

    public static string Humidite(string noeud, string mac) =>
        $$"""{"~":"{{Base(noeud)}}","dev_cla":"humidity","unit_of_meas":"%","stat_cla":"measurement","name":"Humidité","stat_t":"~/sensor/humidite/state","avty_t":"~/status","uniq_id":"ESPsensorhumidite","dev":{{Appareil(noeud, mac)}}}""";

    /// <summary>La sonde interne de la puce : une température, mais de diagnostic.</summary>
    public static string TemperaturePuce(string noeud, string mac) =>
        $$"""{"~":"{{Base(noeud)}}","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement","ent_cat":"diagnostic","name":"Puce","stat_t":"~/sensor/puce/state","avty_t":"~/status","uniq_id":"ESPsensorpuce","dev":{{Appareil(noeud, mac)}}}""";

    public static string Relais(string noeud, string mac) =>
        $$"""{"~":"{{Base(noeud)}}","name":"Relais","stat_t":"~/switch/relais/state","cmd_t":"~/switch/relais/command","avty_t":"~/status","uniq_id":"ESPswitchrelais","dev":{{Appareil(noeud, mac)}}}""";

    public static string Lampe(string noeud, string mac) =>
        $$"""{"~":"{{Base(noeud)}}","name":"Lampe","schema":"json","brightness":true,"stat_t":"~/light/lampe/state","cmd_t":"~/light/lampe/command","avty_t":"~/status","uniq_id":"ESPlightlampe","dev":{{Appareil(noeud, mac)}}}""";

    public static string Version(string noeud, string mac) =>
        $$"""{"~":"{{Base(noeud)}}","name":"Version","ent_cat":"diagnostic","stat_t":"~/sensor/version/state","avty_t":"~/status","uniq_id":"ESPsensorversion","dev":{{Appareil(noeud, mac)}}}""";
}
