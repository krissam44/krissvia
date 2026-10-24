using Krissvia.Plugin.MqttDiscovery.Discovery;

namespace Krissvia.Plugin.MqttDiscovery.Tests;

/// <summary>Lecture des configurations et des gabarits : ce qu'ESPHome, Tasmota et Zigbee2MQTT publient vraiment.</summary>
public class LectureTests
{
    private static ConfigHa Lire(string composant, string objet, string charge) =>
        ConfigParser.Lire(Esphome.Prefixe, Esphome.TopicConfig("etage", composant, objet), charge)
        ?? throw new Xunit.Sdk.XunitException("configuration non lue");

    [Fact]
    public void Les_abreviations_et_le_tilde_d_ESPHome_sont_developpes()
    {
        var c = Lire("sensor", "temperature", Esphome.Temperature("etage", "a1b2c3d4e5f6"));

        Assert.Equal("sensor", c.Composant);
        Assert.Equal("krissvia/etage/sensor/temperature/state", c.EtatTopic);
        Assert.Equal("temperature", c.ClasseHa);
        Assert.Equal("°C", c.Unite);
        Assert.Equal("measurement", c.ClasseEtat);
        var dispo = Assert.Single(c.Dispo);
        Assert.Equal("krissvia/etage/status", dispo.Topic);
        Assert.Equal(("online", "offline"), (dispo.EnLigne, dispo.HorsLigne));
        Assert.Equal(new AppareilHa("a1b2c3d4e5f6", "etage", "Espressif", "esp32dev"), c.Appareil);
    }

    [Fact]
    public void L_identifiant_est_prefixe_par_l_appareil()
    {
        // ESPHome donne « ESPsensortemperature » à la sonde de CHAQUE module : seul l'appareil les distingue.
        var c = Lire("sensor", "temperature", Esphome.Temperature("etage", "a1b2c3d4e5f6"));

        Assert.Equal("a1b2c3d4e5f6/ESPsensortemperature", c.UniqueId);
    }

    [Fact]
    public void Le_nom_de_l_appareil_est_retire_du_nom_de_l_entite()
    {
        // Anciens ESPHome : « etage Température » ; le cœur ajoute lui-même le nom de l'appareil.
        var c = Lire("sensor", "t", """{"name":"etage Température","stat_t":"x","dev":{"ids":["m1"],"name":"etage"}}""");

        Assert.Equal("Température", c.Nom);
    }

    [Fact]
    public void Sans_unique_id_ni_appareil_le_topic_de_configuration_sert_d_identite()
    {
        var c = Lire("binary_sensor", "porte", """{"stat_t":"porte/etat"}""");

        Assert.Equal("binary_sensor/etage/porte", c.UniqueId);
        Assert.Equal("binary_sensor/etage/porte", c.Appareil.Id);
    }

    [Fact]
    public void Une_charge_booleenne_vaut_ce_que_rend_un_gabarit()
    {
        // Zigbee2MQTT : payload_on: true, comparé au rendu Jinja d'un booléen, « True ».
        var c = Lire("binary_sensor", "contact", """{"stat_t":"z/porte","val_tpl":"{{ value_json.contact }}","pl_on":false,"pl_off":true}""");

        Assert.Equal(("False", "True"), (c.ChargeOn, c.ChargeOff));
        Assert.Equal("False", c.Gabarit!.Evaluer("""{"contact":false}"""));
    }

    [Fact]
    public void La_liste_de_joignabilite_et_son_mode_sont_lus()
    {
        var c = Lire("sensor", "t", """
            {"stat_t":"a/t","avty_mode":"all","avty":[{"t":"a/pont"},{"topic":"a/t/dispo","pl_avail":"1","pl_not_avail":"0"}]}
            """);

        Assert.Equal("all", c.ModeDispo);
        Assert.Equal(["a/pont", "a/t/dispo"], c.Dispo.Select(d => d.Topic));
        Assert.Equal(("1", "0"), (c.Dispo[1].EnLigne, c.Dispo[1].HorsLigne));
    }

    [Theory]
    [InlineData("homeassistant/sensor/etage/temperature/config", "sensor")]
    [InlineData("homeassistant/switch/relais/config", "switch")]
    [InlineData("homeassistant/sensor/etage/temperature/state", null)]
    [InlineData("homeassistant/status", null)]
    [InlineData("autre/sensor/etage/temperature/config", null)]
    public void Seuls_les_topics_de_configuration_sont_des_configurations(string topic, string? composant) =>
        Assert.Equal(composant, ConfigParser.Composant("homeassistant", topic));

    [Theory]
    [InlineData("{{ value }}", "21.5", "21.5")]
    [InlineData("{{ value_json.temperature }}", """{"temperature":21.5}""", "21.5")]
    [InlineData("{{value_json['temperature']}}", """{"temperature":21.5}""", "21.5")]
    [InlineData("{{ value_json.sht.temp }}", """{"sht":{"temp":19}}""", "19")]
    [InlineData("{{ value_json.temperature | float }}", """{"temperature":21}""", "21.0")]
    [InlineData("{{ value_json.temperature | round(1) }}", """{"temperature":21.46}""", "21.5")]
    [InlineData("{{ value_json.temperature | int }}", """{"temperature":21.9}""", "21")]
    [InlineData("{{ value_json.state | lower }}", """{"state":"ON"}""", "on")]
    [InlineData("{{ value_json.absente }}", """{"temperature":21}""", null)]
    [InlineData("{{ value_json.temperature }}", "pas du json", null)]
    public void Le_sous_ensemble_de_gabarits_rend_ce_que_rendrait_Home_Assistant(string gabarit, string charge, string? attendu) =>
        Assert.Equal(attendu, Gabarit.Lire(gabarit)!.Evaluer(charge));

    [Theory]
    [InlineData("{{ value_json.temperature * 10 }}")]
    [InlineData("{% if value == '1' %}ON{% else %}OFF{% endif %}")]
    [InlineData("{{ value_json.t | timestamp_custom('%H') }}")]
    public void Un_gabarit_hors_du_sous_ensemble_est_refuse(string gabarit)
    {
        var g = Gabarit.Lire(gabarit)!;

        Assert.False(g.Supporte);
        Assert.Null(g.Evaluer("""{"temperature":21}"""));
    }
}
