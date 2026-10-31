using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.Zigbee2Mqtt.Z2m;

namespace Krissvia.Plugin.Zigbee2Mqtt.Tests;

/// <summary>
/// Le module rail DIN SONOFF BASIC-ZB1GSP (32 A, mesure, coupure sur surcharge), d'après ses exposes documentés par
/// Zigbee2MQTT (zigbee2mqtt.io/devices/BASIC-ZB1GSP, 30/08/2026) — PAS encore relevés sur un vrai module : à refaire
/// avec son <c>bridge/devices</c> dès réception.
/// </summary>
public class DisjoncteurSonoffTests
{
    private const string Module = "0x08ddebfffe000010";

    private static readonly string Appareils = $$"""
        [
          {
            "ieee_address": "{{Module}}", "friendly_name": "Tableau circuit cuisine", "type": "Router", "disabled": false,
            "definition": { "vendor": "SONOFF", "model": "BASIC-ZB1GSP", "description": "Zigbee smart plug with power monitoring",
              "exposes": [
                { "type": "switch", "features": [
                  { "type": "binary", "name": "state", "property": "state", "access": 7, "value_on": "ON", "value_off": "OFF", "value_toggle": "TOGGLE" } ] },
                { "type": "enum", "name": "power_on_behavior", "property": "power_on_behavior", "access": 7, "values": ["off", "on", "toggle", "previous"] },
                { "type": "composite", "name": "inching_control_set", "property": "inching_control_set", "access": 2, "features": [] },
                { "type": "binary", "name": "network_indicator", "property": "network_indicator", "access": 7, "value_on": true, "value_off": false },
                { "type": "numeric", "name": "power", "property": "power", "unit": "W", "access": 5 },
                { "type": "numeric", "name": "current", "property": "current", "unit": "A", "access": 5 },
                { "type": "numeric", "name": "voltage", "property": "voltage", "unit": "V", "access": 5 },
                { "type": "numeric", "name": "energy_today", "property": "energy_today", "unit": "kWh", "access": 5 },
                { "type": "numeric", "name": "output_energy_today", "property": "output_energy_today", "unit": "kWh", "access": 5 },
                { "type": "numeric", "name": "energy_month", "property": "energy_month", "unit": "kWh", "access": 5 },
                { "type": "numeric", "name": "energy_yesterday", "property": "energy_yesterday", "unit": "kWh", "access": 5 },
                { "type": "numeric", "name": "total_energy", "property": "total_energy", "unit": "kWh", "access": 5 },
                { "type": "numeric", "name": "total_output_energy", "property": "total_output_energy", "unit": "kWh", "access": 5 },
                { "type": "binary", "name": "outlet_control_protect", "property": "outlet_control_protect", "access": 7, "value_on": true, "value_off": false },
                { "type": "binary", "name": "ac_current_max_overload_enable", "property": "ac_current_max_overload_enable", "access": 7, "value_on": "ON", "value_off": "OFF" },
                { "type": "numeric", "name": "ac_current_max_overload", "property": "ac_current_max_overload", "unit": "A", "access": 7, "value_min": 0.1, "value_max": 32 },
                { "type": "binary", "name": "ac_voltage_max_overload_enable", "property": "ac_voltage_max_overload_enable", "access": 7, "value_on": "ON", "value_off": "OFF" },
                { "type": "numeric", "name": "ac_voltage_max_overload", "property": "ac_voltage_max_overload", "unit": "V", "access": 7, "value_min": 85, "value_max": 277 },
                { "type": "binary", "name": "ac_power_max_overload_enable", "property": "ac_power_max_overload_enable", "access": 7, "value_on": "ON", "value_off": "OFF" },
                { "type": "numeric", "name": "ac_power_max_overload", "property": "ac_power_max_overload", "unit": "W", "access": 7, "value_min": 10, "value_max": 7680 },
                { "type": "text", "name": "consumption_records", "property": "consumption_records", "access": 1 },
                { "type": "enum", "name": "clear_history", "property": "clear_history", "access": 2, "values": ["clear"] },
                { "type": "numeric", "name": "linkquality", "property": "linkquality", "unit": "lqi", "access": 1 }
              ] }
          }
        ]
        """;

    private readonly Zigbee2MqttTranslator _t = new("zigbee2mqtt");

    private DeviceDescriptor Declarer() =>
        Assert.Single(Assert.Single(_t.TraiterMessage("zigbee2mqtt/bridge/devices", Appareils).OfType<Declare>()).Devices);

    [Fact]
    public void Le_module_donne_un_interrupteur_ses_mesures_et_son_index_mais_aucun_reglage()
    {
        var module = Declarer();

        // Ni les seuils de surcharge, ni les tranches d'énergie (aujourd'hui, mois, hier), ni l'énergie réinjectée.
        Assert.Equal(
            [$"{Module}:state", $"{Module}:power", $"{Module}:current", $"{Module}:voltage", $"{Module}:total_energy"],
            module.Entities.Select(e => e.UniqueId));
        Assert.Equal(EntityKind.Switch, module.Entities[0].Kind);
    }

    [Fact]
    public void Son_index_est_un_compteur_en_kWh_que_le_coeur_sait_totaliser()
    {
        var index = Declarer().Entities.Single(e => e.UniqueId == $"{Module}:total_energy");

        Assert.Equal(DeviceClasses.Energy, index.DeviceClass);
        Assert.Equal(Units.KilowattHour, index.Unit);
        Assert.Equal(StateClass.TotalIncreasing, index.StateClass);
    }

    [Fact]
    public void Un_etat_publie_donne_le_relais_la_puissance_le_courant_et_l_index()
    {
        Declarer();
        var etats = _t.TraiterMessage("zigbee2mqtt/Tableau circuit cuisine",
                """{ "state": "ON", "power": 2140.5, "current": 9.32, "voltage": 229.6, "energy_today": 3.1, "total_energy": 412.87, "ac_current_max_overload": 16 }""")
            .OfType<StateBatch>().SelectMany(b => b.States).ToDictionary(s => s.EntityUniqueId, s => s.Value);

        Assert.True(etats[$"{Module}:state"].GetBoolean());
        Assert.Equal(2140.5, etats[$"{Module}:power"].GetDouble());
        Assert.Equal(9.32, etats[$"{Module}:current"].GetDouble());
        Assert.Equal(412.87, etats[$"{Module}:total_energy"].GetDouble());
        Assert.DoesNotContain($"{Module}:energy_today", etats.Keys);
    }
}
