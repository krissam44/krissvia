using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.MqttDiscovery.Discovery;

namespace Krissvia.Plugin.MqttDiscovery.Tests;

public class TraducteurTests
{
    private const string Etage = "esp32-sht30-etage", MacEtage = "a1b2c3d4e5f6";
    private const string Salon = "esp32-sht30-salon", MacSalon = "f6e5d4c3b2a1";

    private static readonly string IdTempEtage = $"{MacEtage}/ESPsensortemperature";

    private readonly DiscoveryTranslator _t = new(Esphome.Prefixe);

    private void Config(string noeud, string composant, string objet, string charge) =>
        Assert.Empty(_t.TraiterMessage(Esphome.TopicConfig(noeud, composant, objet), charge));

    private void SondeEtage()
    {
        Config(Etage, "sensor", "temperature", Esphome.Temperature(Etage, MacEtage));
        Config(Etage, "sensor", "humidite", Esphome.Humidite(Etage, MacEtage));
    }

    private static IEnumerable<EntityState> Etats(IEnumerable<Message> messages) => messages.OfType<StateBatch>().SelectMany(b => b.States);

    private static Declare Declaration(IEnumerable<Message> messages) => Assert.Single(messages.OfType<Declare>());

    // ── Déclaration ──────────────────────────────────────────────────────────

    [Fact]
    public void Les_configurations_ne_partent_qu_ensemble_dans_une_declaration_complete()
    {
        SondeEtage();
        Assert.True(_t.DeclarationEnAttente);

        var appareil = Assert.Single(Declaration(_t.Declarer()).Devices);
        Assert.Equal(MacEtage, appareil.UniqueId);
        Assert.Equal(Etage, appareil.Name);
        Assert.Equal(("Espressif", "esp32dev"), (appareil.Manufacturer, appareil.Model));
        Assert.Equal([$"{MacEtage}/ESPsensorhumidite", IdTempEtage], appareil.Entities.Select(e => e.UniqueId));
        Assert.False(_t.DeclarationEnAttente);
    }

    [Fact]
    public void Deux_modules_aux_memes_unique_id_donnent_deux_sondes_distinctes()
    {
        SondeEtage();
        Config(Salon, "sensor", "temperature", Esphome.Temperature(Salon, MacSalon));
        _t.Declarer();

        _t.TraiterMessage(Esphome.TopicEtat(Etage, "sensor", "temperature"), "17.2");
        var salon = _t.TraiterMessage(Esphome.TopicEtat(Salon, "sensor", "temperature"), "21.4");

        var etat = Assert.Single(Etats(salon));
        Assert.Equal($"{MacSalon}/ESPsensortemperature", etat.EntityUniqueId);
        Assert.Equal(21.4, etat.Value.GetDouble());
    }

    [Fact]
    public void Les_classes_et_unites_de_la_sonde_sont_celles_du_contrat()
    {
        SondeEtage();
        var entites = Declaration(_t.Declarer()).Devices.Single().Entities.ToDictionary(e => e.UniqueId);

        var temperature = entites[IdTempEtage];
        Assert.Equal((EntityKind.Sensor, DeviceClasses.Temperature, Units.Celsius, StateClass.Measurement),
            (temperature.Kind, temperature.DeviceClass, temperature.Unit, temperature.StateClass));
        Assert.Equal("Température", temperature.Name);
        Assert.Equal((DeviceClasses.Humidity, Units.Percent), (entites[$"{MacEtage}/ESPsensorhumidite"].DeviceClass, entites[$"{MacEtage}/ESPsensorhumidite"].Unit));
    }

    [Fact]
    public void La_temperature_de_la_puce_n_est_pas_une_sonde_de_piece()
    {
        Config(Etage, "sensor", "puce", Esphome.TemperaturePuce(Etage, MacEtage));

        var puce = Declaration(_t.Declarer()).Devices.Single().Entities.Single();
        Assert.Equal(DeviceClasses.DeviceTemperature, puce.DeviceClass);
    }

    [Fact]
    public void Un_capteur_sans_unite_ni_classe_publie_un_libelle()
    {
        Config(Etage, "sensor", "version", Esphome.Version(Etage, MacEtage));
        var version = Declaration(_t.Declarer()).Devices.Single().Entities.Single();
        Assert.Equal((DeviceClasses.Enum, (string?)null), (version.DeviceClass, version.Unit));

        var etat = Assert.Single(Etats(_t.TraiterMessage(Esphome.TopicEtat(Etage, "sensor", "version"), "2024.10.0")));
        Assert.Equal("2024.10.0", etat.Value.GetString());
    }

    [Fact]
    public void Une_configuration_republiee_a_l_identique_ne_redeclare_rien()
    {
        SondeEtage();
        _t.Declarer();

        _t.TraiterMessage(Esphome.TopicConfig(Etage, "sensor", "temperature"), Esphome.Temperature(Etage, MacEtage));

        Assert.False(_t.DeclarationEnAttente);
    }

    [Fact]
    public void Une_configuration_videe_retire_l_entite_de_la_declaration()
    {
        SondeEtage();
        _t.Declarer();

        Config(Etage, "sensor", "humidite", "");

        Assert.Equal([IdTempEtage], Declaration(_t.Declarer()).Devices.Single().Entities.Select(e => e.UniqueId));
    }

    [Fact]
    public void Une_configuration_a_gabarit_inconnu_n_est_pas_declaree()
    {
        SondeEtage();
        Config(Etage, "sensor", "calcul", """{"name":"Calcul","stat_t":"x/calcul","val_tpl":"{{ value_json.t * 10 }}","unit_of_meas":"°C","dev":{"ids":"a1b2c3d4e5f6"}}""");

        Assert.Equal(2, Declaration(_t.Declarer()).Devices.Single().Entities.Count);
    }

    // ── États ────────────────────────────────────────────────────────────────

    [Fact]
    public void Un_etat_recu_avant_la_declaration_part_avec_elle()
    {
        SondeEtage();
        Assert.Empty(_t.TraiterMessage(Esphome.TopicEtat(Etage, "sensor", "temperature"), "17.2"));

        var etat = Assert.Single(Etats(_t.Declarer()));
        Assert.Equal((IdTempEtage, 17.2), (etat.EntityUniqueId, etat.Value.GetDouble()));
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Une_mesure_illisible_n_est_pas_transmise(string charge)
    {
        SondeEtage();
        _t.Declarer();

        Assert.Empty(_t.TraiterMessage(Esphome.TopicEtat(Etage, "sensor", "temperature"), charge));
    }

    [Fact]
    public void Les_degres_Fahrenheit_sont_convertis()
    {
        Config("t", "sensor", "ext", """{"stat_t":"t/ext","unit_of_meas":"°F","dev_cla":"temperature"}""");
        var entite = Declaration(_t.Declarer()).Devices.Single().Entities.Single();
        Assert.Equal(Units.Celsius, entite.Unit);

        Assert.Equal(20.0, Etats(_t.TraiterMessage("t/ext", "68")).Single().Value.GetDouble());
    }

    [Fact]
    public void Un_gabarit_lit_la_valeur_dans_le_json()
    {
        Config("t", "sensor", "temp", """{"stat_t":"tasmota/tele/SENSOR","val_tpl":"{{ value_json.SHT3X.Temperature }}","unit_of_meas":"°C","dev_cla":"temperature"}""");
        _t.Declarer();

        var etat = Etats(_t.TraiterMessage("tasmota/tele/SENSOR", """{"Time":"2026-10-24T10:00:00","SHT3X":{"Temperature":19.6,"Humidity":54}}""")).Single();
        Assert.Equal(19.6, etat.Value.GetDouble());
    }

    [Fact]
    public void Un_contact_suit_payload_on_sans_inversion_propre()
    {
        Config("z", "binary_sensor", "porte", """{"stat_t":"z2m/porte","val_tpl":"{{ value_json.contact }}","pl_on":false,"pl_off":true,"dev_cla":"door"}""");
        var porte = Declaration(_t.Declarer()).Devices.Single().Entities.Single();
        Assert.Equal((EntityKind.BinarySensor, DeviceClasses.Opening), (porte.Kind, porte.DeviceClass));

        // contact: false = aimant éloigné = OUVERT, que la configuration déclare par payload_on: false.
        Assert.True(Etats(_t.TraiterMessage("z2m/porte", """{"contact":false}""")).Single().Value.GetBoolean());
        Assert.False(Etats(_t.TraiterMessage("z2m/porte", """{"contact":true}""")).Single().Value.GetBoolean());
    }

    // ── Joignabilité ─────────────────────────────────────────────────────────

    [Fact]
    public void Le_statut_d_un_module_rend_ses_entites_injoignables()
    {
        SondeEtage();
        _t.Declarer();

        var messages = _t.TraiterMessage(Esphome.TopicStatut(Etage), "offline");

        Assert.Equal([$"{MacEtage}/ESPsensorhumidite", IdTempEtage],
            messages.OfType<Availability>().Where(a => !a.Online).Select(a => a.EntityUniqueId).Order());
        Assert.Empty(_t.TraiterMessage(Esphome.TopicStatut(Etage), "offline"));   // rien de neuf : rien à dire
    }

    [Fact]
    public void L_etat_retenu_d_un_module_hors_ligne_ne_le_fait_pas_passer_pour_joignable()
    {
        // À la connexion, le broker rejoue l'état retenu d'un module mort : le cœur le rendrait disponible.
        SondeEtage();
        _t.Declarer();
        _t.TraiterMessage(Esphome.TopicStatut(Etage), "offline");

        var messages = _t.TraiterMessage(Esphome.TopicEtat(Etage, "sensor", "temperature"), "17.2");

        Assert.IsType<StateBatch>(messages[0]);
        var dispo = Assert.IsType<Availability>(messages[1]);
        Assert.Equal((IdTempEtage, false), (dispo.EntityUniqueId, dispo.Online));
    }

    [Fact]
    public void Le_hors_ligne_recu_avant_la_declaration_part_avec_elle()
    {
        SondeEtage();
        _t.TraiterMessage(Esphome.TopicStatut(Etage), "offline");

        Assert.All(_t.Declarer().OfType<Availability>(), a => Assert.False(a.Online));
    }

    [Fact]
    public void En_mode_all_une_seule_source_hors_ligne_suffit()
    {
        Config("t", "sensor", "t", """{"stat_t":"a/t","unit_of_meas":"°C","avty_mode":"all","avty":[{"t":"a/pont"},{"t":"a/t/dispo"}]}""");
        _t.Declarer();

        Assert.Empty(_t.TraiterMessage("a/pont", "online"));   // l'autre source n'a encore rien dit
        Assert.True(Assert.Single(_t.TraiterMessage("a/t/dispo", "online").OfType<Availability>()).Online);
        Assert.False(Assert.Single(_t.TraiterMessage("a/pont", "offline").OfType<Availability>()).Online);
    }

    [Fact]
    public void Le_plugin_reconnecte_redit_les_entites_hors_ligne()
    {
        SondeEtage();
        _t.Declarer();
        _t.TraiterMessage(Esphome.TopicStatut(Etage), "offline");

        var messages = _t.MqttConnecte();

        Assert.True(Assert.IsType<Availability>(messages[0]).Online);
        Assert.Equal(2, messages.Skip(1).OfType<Availability>().Count(a => !a.Online && a.EntityUniqueId is not null));
    }

    // ── Abonnements ──────────────────────────────────────────────────────────

    [Fact]
    public void Les_topics_d_etat_et_de_statut_sont_suivis_une_fois_par_connexion()
    {
        _t.MqttConnecte();
        SondeEtage();

        Assert.Equal(
            [Esphome.TopicEtat(Etage, "sensor", "humidite"), Esphome.TopicEtat(Etage, "sensor", "temperature"), Esphome.TopicStatut(Etage)],
            _t.AbonnementsAFaire().Order());
        Assert.Empty(_t.AbonnementsAFaire());

        _t.MqttConnecte();   // nouvelle connexion : les abonnements dynamiques sont perdus
        Assert.Equal(3, _t.AbonnementsAFaire().Count);
    }

    // ── Ordres ───────────────────────────────────────────────────────────────

    private static CommandRequest Ordre(string uniqueId, string commande, string? args = null) => new()
    {
        Id = "o1",
        EntityUniqueId = uniqueId,
        Command = commande,
        Args = args is null ? null : JsonDocument.Parse(args).RootElement.Clone(),
    };

    [Fact]
    public void Un_relais_se_commande_sur_son_command_topic()
    {
        Config(Etage, "switch", "relais", Esphome.Relais(Etage, MacEtage));
        var relais = Declaration(_t.Declarer()).Devices.Single().Entities.Single();
        Assert.Equal([StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle], relais.Commands);

        var (publication, _) = _t.Traduire(Ordre(relais.UniqueId, StandardCommands.TurnOn));

        Assert.Equal(new PublicationMqtt("krissvia/esp32-sht30-etage/switch/relais/command", "ON"), publication);
    }

    [Fact]
    public void Une_bascule_se_calcule_sur_l_etat_connu_et_se_refuse_sans()
    {
        Config(Etage, "switch", "relais", Esphome.Relais(Etage, MacEtage));
        var id = Declaration(_t.Declarer()).Devices.Single().Entities.Single().UniqueId;

        var (aucune, erreur) = _t.Traduire(Ordre(id, StandardCommands.Toggle));
        Assert.Null(aucune);
        Assert.Contains("inconnu", erreur);

        _t.TraiterMessage(Esphome.TopicEtat(Etage, "switch", "relais"), "ON");
        Assert.Equal("OFF", _t.Traduire(Ordre(id, StandardCommands.Toggle)).Publication!.Charge);
    }

    [Fact]
    public void Une_sonde_ne_se_commande_pas()
    {
        SondeEtage();
        _t.Declarer();

        var (publication, erreur) = _t.Traduire(Ordre(IdTempEtage, StandardCommands.TurnOn));

        Assert.Null(publication);
        Assert.Contains("lecture seule", erreur);
    }

    [Fact]
    public void Une_lampe_json_lit_et_commande_sa_luminosite_en_pourcentage()
    {
        Config(Etage, "light", "lampe", Esphome.Lampe(Etage, MacEtage));
        var lampe = Declaration(_t.Declarer()).Devices.Single().Entities.Single();
        Assert.Contains(StandardCommands.SetBrightness, lampe.Commands);

        var etat = Etats(_t.TraiterMessage(Esphome.TopicEtat(Etage, "light", "lampe"), """{"state":"ON","brightness":128,"color_mode":"brightness"}""")).Single();
        Assert.Equal("""{"on":true,"brightness":50}""", etat.Value.GetRawText());

        var publication = _t.Traduire(Ordre(lampe.UniqueId, StandardCommands.SetBrightness, """{"brightness":20}""")).Publication!;
        Assert.Equal("""{"state":"ON","brightness":51}""", publication.Charge);
        Assert.Equal("""{"state":"OFF"}""", _t.Traduire(Ordre(lampe.UniqueId, StandardCommands.Toggle)).Publication!.Charge);
    }

    [Fact]
    public void La_luminosite_suit_l_echelle_annoncee_par_l_appareil()
    {
        // Tasmota compte de 0 à 100 (bri_scl), pas de 0 à 255.
        Config("t", "light", "bureau", """{"schema":"json","brightness":true,"bri_scl":100,"stat_t":"t/bureau","cmd_t":"t/bureau/set"}""");
        var id = Declaration(_t.Declarer()).Devices.Single().Entities.Single().UniqueId;

        Assert.Equal("""{"on":true,"brightness":40}""", Etats(_t.TraiterMessage("t/bureau", """{"state":"ON","brightness":40}""")).Single().Value.GetRawText());
        Assert.Equal("""{"state":"ON","brightness":75}""", _t.Traduire(Ordre(id, StandardCommands.SetBrightness, """{"brightness":75}""")).Publication!.Charge);
    }

    [Fact]
    public void Une_entite_retiree_ne_se_commande_plus()
    {
        Config(Etage, "switch", "relais", Esphome.Relais(Etage, MacEtage));
        var id = Declaration(_t.Declarer()).Devices.Single().Entities.Single().UniqueId;
        Config(Etage, "switch", "relais", "");
        _t.Declarer();

        Assert.Null(_t.Traduire(Ordre(id, StandardCommands.TurnOn)).Publication);
    }
}
