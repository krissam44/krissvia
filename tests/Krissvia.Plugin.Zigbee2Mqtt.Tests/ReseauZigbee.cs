namespace Krissvia.Plugin.Zigbee2Mqtt.Tests;

/// <summary>
/// Un réseau Zigbee type, au format de <c>bridge/devices</c> de Zigbee2MQTT (version 1.x/2.x) : un appareil de
/// chaque famille rencontrée dans DomoMud, avec les réglages et diagnostics qui NE doivent PAS devenir des entités.
/// </summary>
internal static class ReseauZigbee
{
    public const string Sonde = "0x00158d0001000001";
    public const string Prise = "0xa4c1380000000002";
    public const string Porte = "0x00158d0001000003";
    public const string Lampe = "0x000d6ffffe000004";
    public const string Inter = "0xa4c1380000000005";
    public const string Bouton = "0x00158d0001000006";
    public const string Volet = "0xa4c1380000000007";
    public const string Tete = "0x0c4314fffe000008";
    public const string Desactive = "0x00158d0001000009";

    /// <summary><c>bridge/devices</c> ; <paramref name="nomPrise"/> permet de simuler un renommage.</summary>
    public static string Appareils(string nomPrise = "Prise lave-linge") => $$"""
        [
          { "ieee_address": "0x00124b0000000000", "friendly_name": "Coordinator", "type": "Coordinator", "disabled": false, "definition": null },
          {
            "ieee_address": "{{Sonde}}", "friendly_name": "Sonde salon", "type": "EndDevice", "disabled": false,
            "definition": { "vendor": "Aqara", "model": "WSDCGQ11LM", "description": "Temperature, humidity and pressure sensor",
              "exposes": [
                { "type": "numeric", "name": "battery", "property": "battery", "unit": "%", "access": 1 },
                { "type": "numeric", "name": "temperature", "property": "temperature", "unit": "°C", "access": 1 },
                { "type": "numeric", "name": "humidity", "property": "humidity", "unit": "%", "access": 1 },
                { "type": "numeric", "name": "pressure", "property": "pressure", "unit": "hPa", "access": 1 },
                { "type": "numeric", "name": "voltage", "property": "voltage", "unit": "mV", "access": 1 },
                { "type": "numeric", "name": "linkquality", "property": "linkquality", "unit": "lqi", "access": 1 }
              ] }
          },
          {
            "ieee_address": "{{Prise}}", "friendly_name": "{{nomPrise}}", "type": "Router", "disabled": false,
            "definition": { "vendor": "Tuya", "model": "TS011F_plug_1", "description": "Smart plug (with power monitoring)",
              "exposes": [
                { "type": "switch", "features": [
                  { "type": "binary", "name": "state", "property": "state", "access": 7, "value_on": "ON", "value_off": "OFF", "value_toggle": "TOGGLE" } ] },
                { "type": "numeric", "name": "power", "property": "power", "unit": "W", "access": 5 },
                { "type": "numeric", "name": "current", "property": "current", "unit": "A", "access": 5 },
                { "type": "numeric", "name": "voltage", "property": "voltage", "unit": "V", "access": 5 },
                { "type": "numeric", "name": "energy", "property": "energy", "unit": "kWh", "access": 5 },
                { "type": "enum", "name": "power_on_behavior", "property": "power_on_behavior", "access": 7, "values": ["off", "on", "previous"] },
                { "type": "binary", "name": "child_lock", "property": "child_lock", "access": 3, "value_on": "LOCK", "value_off": "UNLOCK" },
                { "type": "numeric", "name": "linkquality", "property": "linkquality", "unit": "lqi", "access": 1 }
              ] }
          },
          {
            "ieee_address": "{{Porte}}", "friendly_name": "Porte entrée", "type": "EndDevice", "disabled": false,
            "definition": { "vendor": "Aqara", "model": "MCCGQ11LM", "description": "Door and window sensor",
              "exposes": [
                { "type": "binary", "name": "contact", "property": "contact", "access": 1, "value_on": false, "value_off": true },
                { "type": "numeric", "name": "battery", "property": "battery", "unit": "%", "access": 1 }
              ] }
          },
          {
            "ieee_address": "{{Lampe}}", "friendly_name": "Lampe bureau", "type": "Router", "disabled": false,
            "definition": { "vendor": "IKEA", "model": "LED1836G9", "description": "TRADFRI bulb E27",
              "exposes": [
                { "type": "light", "features": [
                  { "type": "binary", "name": "state", "property": "state", "access": 7, "value_on": "ON", "value_off": "OFF", "value_toggle": "TOGGLE" },
                  { "type": "numeric", "name": "brightness", "property": "brightness", "access": 7, "value_min": 0, "value_max": 254 } ] },
                { "type": "enum", "name": "effect", "property": "effect", "access": 2, "values": ["blink", "breathe"] }
              ] }
          },
          {
            "ieee_address": "{{Inter}}", "friendly_name": "Inter cuisine", "type": "Router", "disabled": false,
            "definition": { "vendor": "Tuya", "model": "TS0012", "description": "2 gang switch",
              "exposes": [
                { "type": "switch", "endpoint": "l1", "features": [
                  { "type": "binary", "name": "state", "property": "state_l1", "endpoint": "l1", "access": 7, "value_on": "ON", "value_off": "OFF", "value_toggle": "TOGGLE" } ] },
                { "type": "switch", "endpoint": "l2", "features": [
                  { "type": "binary", "name": "state", "property": "state_l2", "endpoint": "l2", "access": 7, "value_on": "ON", "value_off": "OFF", "value_toggle": "TOGGLE" } ] }
              ] }
          },
          {
            "ieee_address": "{{Bouton}}", "friendly_name": "Bouton chambre", "type": "EndDevice", "disabled": false,
            "definition": { "vendor": "Aqara", "model": "WXKG11LM", "description": "Wireless mini switch",
              "exposes": [
                { "type": "enum", "name": "action", "property": "action", "access": 1, "values": ["single", "double", "hold", "release"] },
                { "type": "numeric", "name": "battery", "property": "battery", "unit": "%", "access": 1 }
              ] }
          },
          {
            "ieee_address": "{{Volet}}", "friendly_name": "Volet séjour", "type": "Router", "disabled": false,
            "definition": { "vendor": "Tuya", "model": "TS130F", "description": "Curtain/blind switch",
              "exposes": [
                { "type": "cover", "features": [
                  { "type": "enum", "name": "state", "property": "state", "access": 3, "values": ["OPEN", "CLOSE", "STOP"] },
                  { "type": "numeric", "name": "position", "property": "position", "access": 7, "value_min": 0, "value_max": 100 } ] }
              ] }
          },
          {
            "ieee_address": "{{Tete}}", "friendly_name": "Tête chambre", "type": "EndDevice", "disabled": false,
            "definition": { "vendor": "Danfoss", "model": "014G2461", "description": "Ally thermostat",
              "exposes": [
                { "type": "climate", "features": [
                  { "type": "numeric", "name": "occupied_heating_setpoint", "property": "occupied_heating_setpoint", "unit": "°C", "access": 7 },
                  { "type": "numeric", "name": "local_temperature", "property": "local_temperature", "unit": "°C", "access": 5 } ] }
              ] }
          },
          {
            "ieee_address": "{{Desactive}}", "friendly_name": "Vieille sonde", "type": "EndDevice", "disabled": true,
            "definition": { "vendor": "Aqara", "model": "WSDCGQ11LM", "description": "Temperature sensor",
              "exposes": [ { "type": "numeric", "name": "temperature", "property": "temperature", "unit": "°C", "access": 1 } ] }
          }
        ]
        """;
}
