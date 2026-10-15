using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.Zigbee2Mqtt.Z2m;
using static Krissvia.Plugin.Zigbee2Mqtt.Tests.ReseauZigbee;

namespace Krissvia.Plugin.Zigbee2Mqtt.Tests;

/// <summary>La traduction Zigbee2MQTT ↔ contrat, sans broker ni cœur.</summary>
public class TraducteurTests
{
    private readonly Zigbee2MqttTranslator _t = new("zigbee2mqtt");

    private Declare Declarer(string? appareils = null) =>
        Assert.Single(_t.TraiterMessage("zigbee2mqtt/bridge/devices", appareils ?? Appareils()).OfType<Declare>());

    private Dictionary<string, JsonElement> Etat(string nom, string json) =>
        _t.TraiterMessage($"zigbee2mqtt/{nom}", json).OfType<StateBatch>().SelectMany(b => b.States)
            .ToDictionary(s => s.EntityUniqueId, s => s.Value);

    private static EntityDescriptor Entite(Declare d, string uniqueId) =>
        d.Devices.SelectMany(a => a.Entities).Single(e => e.UniqueId == uniqueId);

    // ── Déclaration ──────────────────────────────────────────────────────────

    [Fact]
    public void Le_coordinateur_et_les_appareils_desactives_ne_sont_pas_declares()
    {
        var d = Declarer();

        Assert.Equal([Sonde, Prise, Porte, Lampe, Inter, Bouton, Volet, Tete], d.Devices.Select(a => a.UniqueId));
    }

    [Fact]
    public void Les_reglages_et_diagnostics_ne_deviennent_pas_des_entites()
    {
        var prise = Declarer().Devices.Single(a => a.UniqueId == Prise);

        Assert.Equal(
            [$"{Prise}:state", $"{Prise}:power", $"{Prise}:current", $"{Prise}:voltage", $"{Prise}:energy"],
            prise.Entities.Select(e => e.UniqueId));
    }

    [Fact]
    public void Les_familles_et_classes_sont_celles_du_contrat()
    {
        var d = Declarer();

        Assert.Equal(EntityKind.Switch, Entite(d, $"{Prise}:state").Kind);
        Assert.Equal(StateClass.TotalIncreasing, Entite(d, $"{Prise}:energy").StateClass);
        Assert.Equal(DeviceClasses.Opening, Entite(d, $"{Porte}:contact").DeviceClass);
        Assert.Equal(EntityKind.Light, Entite(d, $"{Lampe}:light").Kind);
        Assert.Contains(StandardCommands.SetBrightness, Entite(d, $"{Lampe}:light").Commands);
        Assert.Equal(EntityKind.Event, Entite(d, $"{Bouton}:action").Kind);
        Assert.Equal(EntityKind.Cover, Entite(d, $"{Volet}:cover").Kind);
        Assert.Equal(DeviceClasses.Temperature, Entite(d, $"{Tete}:local_temperature").DeviceClass);
        Assert.DoesNotContain(d.Devices.Single(a => a.UniqueId == Tete).Entities, e => e.UniqueId.EndsWith("setpoint"));
    }

    [Fact]
    public void Une_propriete_modifiable_est_un_reglage_jamais_une_mesure()
    {
        // Certains thermostats Tuya exposent leur consigne au premier niveau, sous un nom de mesure.
        const string appareils = """
            [ { "ieee_address": "0x01", "friendly_name": "Thermostat", "type": "Router",
                "definition": { "vendor": "Tuya", "model": "X", "exposes": [
                  { "type": "numeric", "name": "temperature", "property": "temperature", "unit": "°C", "access": 7 },
                  { "type": "numeric", "name": "humidity", "property": "humidity", "unit": "%", "access": 5 } ] } } ]
            """;

        var d = Declarer(appareils);

        Assert.Equal(["0x01:humidity"], d.Devices.Single().Entities.Select(e => e.UniqueId));
    }

    [Fact]
    public void La_tension_des_piles_en_millivolts_est_convertie_en_volts()
    {
        var d = Declarer();
        Assert.Equal(Units.Volt, Entite(d, $"{Sonde}:voltage").Unit);

        Assert.Equal(3.005, Etat("Sonde salon", """{"voltage":3005}""")[$"{Sonde}:voltage"].GetDouble());
    }

    [Fact]
    public void Chaque_voie_d_un_interrupteur_multiple_est_une_entite_nommee()
    {
        var d = Declarer();

        Assert.Equal("l1", Entite(d, $"{Inter}:state_l1").Name);
        Assert.Equal("l2", Entite(d, $"{Inter}:state_l2").Name);
    }

    [Fact]
    public void Une_republication_identique_ne_redeclare_rien()
    {
        Declarer();

        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/bridge/devices", Appareils()));
    }

    [Fact]
    public void Un_renommage_dans_Zigbee2MQTT_ne_change_aucune_entite()
    {
        var avant = Declarer().Devices.Single(a => a.UniqueId == Prise).Entities.Select(e => e.UniqueId).ToList();

        var apres = Declarer(Appareils(nomPrise: "Prise cellier"));

        Assert.Equal(avant, apres.Devices.Single(a => a.UniqueId == Prise).Entities.Select(e => e.UniqueId));
        Assert.True(Etat("Prise cellier", """{"state":"ON"}""")[$"{Prise}:state"].GetBoolean());
        Assert.Empty(Etat("Prise lave-linge", """{"state":"OFF"}"""));
        Assert.Equal("zigbee2mqtt/Prise cellier/set", Ordre($"{Prise}:state", StandardCommands.TurnOff).Publication!.Topic);
    }

    // ── États ────────────────────────────────────────────────────────────────

    [Fact]
    public void Une_mesure_arrive_avec_sa_valeur_et_les_proprietes_inutiles_sont_ignorees()
    {
        Declarer();

        var etats = Etat("Sonde salon", """{"temperature":19.5,"humidity":48,"linkquality":120,"last_seen":"2026-10-15T10:00:00Z"}""");

        Assert.Equal(2, etats.Count);
        Assert.Equal(19.5, etats[$"{Sonde}:temperature"].GetDouble());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Un_contact_ferme_n_est_pas_une_ouverture(bool contact, bool ouvert)
    {
        Declarer();

        var etats = Etat("Porte entrée", $$"""{"contact":{{(contact ? "true" : "false")}}}""");

        Assert.Equal(ouvert, etats[$"{Porte}:contact"].GetBoolean());
    }

    [Fact]
    public void Un_contact_sans_value_on_suit_la_convention_inverse()
    {
        var sansValueOn = Appareils().Replace("""
            "access": 1, "value_on": false, "value_off": true }
            """.Trim(), """ "access": 1 }""");
        Assert.DoesNotContain("\"value_on\": false", sansValueOn);
        Declarer(sansValueOn);

        Assert.True(Etat("Porte entrée", """{"contact":false}""")[$"{Porte}:contact"].GetBoolean());
    }

    [Fact]
    public void Une_lumiere_se_reconstitue_par_morceaux_et_sa_luminosite_passe_en_pourcentage()
    {
        Declarer();

        var premier = Etat("Lampe bureau", """{"state":"ON","brightness":127}""")[$"{Lampe}:light"];
        var second = Etat("Lampe bureau", """{"brightness":254}""")[$"{Lampe}:light"];

        Assert.True(premier.GetProperty("on").GetBoolean());
        Assert.Equal(50, premier.GetProperty("brightness").GetInt32());
        Assert.True(second.GetProperty("on").GetBoolean());
        Assert.Equal(100, second.GetProperty("brightness").GetInt32());
    }

    [Fact]
    public void Les_voies_d_un_interrupteur_multiple_sont_lues_separement()
    {
        Declarer();

        var etats = Etat("Inter cuisine", """{"state_l1":"ON","state_l2":"OFF"}""");

        Assert.True(etats[$"{Inter}:state_l1"].GetBoolean());
        Assert.False(etats[$"{Inter}:state_l2"].GetBoolean());
    }

    [Fact]
    public void Un_appui_est_transmis_mais_jamais_rejoue_a_la_resynchronisation()
    {
        Declarer();

        Assert.Equal("double", Etat("Bouton chambre", """{"action":"double","battery":90}""")[$"{Bouton}:action"].GetString());
        Assert.False(Etat("Bouton chambre", """{"action":""}""").ContainsKey($"{Bouton}:action"));

        var rejoues = _t.Instantane().OfType<StateBatch>().SelectMany(b => b.States).Select(s => s.EntityUniqueId);
        Assert.Contains($"{Bouton}:battery", rejoues);
        Assert.DoesNotContain($"{Bouton}:action", rejoues);
    }

    [Fact]
    public void L_etat_d_un_appareil_inconnu_ou_une_charge_illisible_sont_ignores()
    {
        Declarer();

        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/Appareil fantôme", """{"temperature":20}"""));
        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/Sonde salon", "{pas du json"));
        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/Sonde salon", "42"));
        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/bridge/devices", "{pas du json"));
        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/Prise lave-linge/set", """{"state":"ON"}"""));
        Assert.Empty(_t.TraiterMessage("autre/Sonde salon", """{"temperature":20}"""));
    }

    // ── Joignabilité ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("offline")]
    [InlineData("""{"state":"offline"}""")]
    public void Un_appareil_hors_ligne_est_signale_sous_ses_deux_formats(string charge)
    {
        Declarer();

        var dispo = Assert.IsType<Availability>(Assert.Single(_t.TraiterMessage("zigbee2mqtt/Sonde salon/availability", charge)));

        Assert.Equal(Sonde, dispo.DeviceUniqueId);
        Assert.False(dispo.Online);
    }

    [Fact]
    public void Une_joignabilite_recue_avant_les_appareils_part_avec_la_declaration()
    {
        Assert.Empty(_t.TraiterMessage("zigbee2mqtt/Porte entrée/availability", """{"state":"offline"}"""));

        var messages = _t.TraiterMessage("zigbee2mqtt/bridge/devices", Appareils());

        Assert.IsType<Declare>(messages[0]);
        var dispo = Assert.IsType<Availability>(Assert.Single(messages.Skip(1)));
        Assert.Equal(Porte, dispo.DeviceUniqueId);
    }

    [Fact]
    public void Zigbee2MQTT_arrete_ou_broker_perdu_rendent_tout_le_plugin_hors_ligne()
    {
        var pont = Assert.IsType<Availability>(Assert.Single(_t.TraiterMessage("zigbee2mqtt/bridge/state", """{"state":"offline"}""")));
        Assert.Null(pont.DeviceUniqueId);
        Assert.False(pont.Online);

        var broker = Assert.IsType<Availability>(Assert.Single(_t.MqttDeconnecte()));
        Assert.Null(broker.DeviceUniqueId);
        Assert.False(broker.Online);
    }

    [Fact]
    public void L_instantane_contient_declaration_etats_et_joignabilites()
    {
        _t.MqttConnecte();
        Declarer();
        _t.TraiterMessage("zigbee2mqtt/bridge/state", "online");
        _t.TraiterMessage("zigbee2mqtt/Sonde salon/availability", "offline");
        Etat("Sonde salon", """{"temperature":19.5}""");

        var instantane = _t.Instantane();

        Assert.IsType<Declare>(instantane[0]);
        Assert.Equal(19.5, Assert.IsType<StateBatch>(instantane[1]).States.Single().Value.GetDouble());
        Assert.True(Assert.IsType<Availability>(instantane[2]).Online);
        Assert.Equal(Sonde, Assert.IsType<Availability>(instantane[3]).DeviceUniqueId);
    }

    [Fact]
    public void Sans_Zigbee2MQTT_l_instantane_annonce_le_plugin_hors_ligne()
    {
        _t.MqttConnecte();
        Declarer();

        Assert.False(_t.Instantane().OfType<Availability>().Single(a => a.DeviceUniqueId is null).Online);
    }

    // ── Ordres ───────────────────────────────────────────────────────────────

    private (PublicationMqtt? Publication, string? Erreur) Ordre(string uniqueId, string commande, object? args = null) =>
        _t.Traduire(new CommandRequest
        {
            Id = "1",
            EntityUniqueId = uniqueId,
            Command = commande,
            Args = args is null ? null : JsonSerializer.SerializeToElement(args),
        });

    [Theory]
    [InlineData(StandardCommands.TurnOn, """{"state":"ON"}""")]
    [InlineData(StandardCommands.TurnOff, """{"state":"OFF"}""")]
    [InlineData(StandardCommands.Toggle, """{"state":"TOGGLE"}""")]
    public void Un_ordre_de_prise_part_sur_le_topic_set(string commande, string charge)
    {
        Declarer();

        var publication = Ordre($"{Prise}:state", commande).Publication;

        Assert.Equal("zigbee2mqtt/Prise lave-linge/set", publication!.Topic);
        Assert.Equal(charge, publication.Charge);
    }

    [Fact]
    public void Un_ordre_sur_une_voie_vise_sa_propriete()
    {
        Declarer();

        Assert.Equal("""{"state_l2":"OFF"}""", Ordre($"{Inter}:state_l2", StandardCommands.TurnOff).Publication!.Charge);
    }

    [Theory]
    [InlineData(50, """{"state":"ON","brightness":127}""")]
    [InlineData(1, """{"state":"ON","brightness":3}""")]
    [InlineData(0, """{"state":"OFF"}""")]
    public void La_luminosite_en_pourcentage_est_convertie(int pourcent, string charge)
    {
        Declarer();

        Assert.Equal(charge, Ordre($"{Lampe}:light", StandardCommands.SetBrightness, new { brightness = pourcent }).Publication!.Charge);
    }

    [Fact]
    public void Un_volet_recoit_position_et_ordres_de_mouvement()
    {
        Declarer();

        Assert.Equal("""{"position":30}""", Ordre($"{Volet}:cover", StandardCommands.SetPosition, new { position = 30 }).Publication!.Charge);
        Assert.Equal("""{"state":"STOP"}""", Ordre($"{Volet}:cover", StandardCommands.Stop).Publication!.Charge);
    }

    [Fact]
    public void Les_ordres_impossibles_sont_refuses_avec_une_raison()
    {
        Declarer();

        Assert.NotNull(Ordre("0xinconnu:state", StandardCommands.TurnOn).Erreur);
        Assert.NotNull(Ordre($"{Sonde}:temperature", StandardCommands.TurnOn).Erreur);
        Assert.NotNull(Ordre($"{Lampe}:light", StandardCommands.SetBrightness, new { brightness = 150 }).Erreur);
        Assert.NotNull(Ordre($"{Lampe}:light", StandardCommands.SetBrightness).Erreur);
        Assert.Null(Ordre($"{Prise}:state", StandardCommands.SetBrightness, new { brightness = 50 }).Publication);
    }
}
