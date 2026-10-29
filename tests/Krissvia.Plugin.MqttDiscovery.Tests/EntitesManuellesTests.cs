using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.MqttDiscovery.Discovery;

namespace Krissvia.Plugin.MqttDiscovery.Tests;

/// <summary>
/// Les entités manuelles, sur les topics que publie Rfx2Mqtt (<c>domomud/sensor/th/{nom}</c> en JSON, plus un topic plat
/// par attribut) : déclarées sans qu'aucun appareil s'annonce, lues et commandées comme les entités découvertes.
/// </summary>
public class EntitesManuellesTests
{
    private const string Sonde = "domomud/sensor/th/salon";

    private static EntiteManuelle Temperature => new()
    {
        Appareil = "Sonde salon", Nom = "Température", Topic = $"{Sonde}/temperature", Classe = "temperature", Unite = "°C",
    };

    private static EntiteManuelle PileFaible => new()
    {
        Appareil = "Sonde salon", Nom = "Pile faible", Type = "binary_sensor", Topic = $"{Sonde}/battery_low", Classe = "battery",
    };

    private static DiscoveryTranslator Traducteur(params EntiteManuelle[] manuelles) => new(Esphome.Prefixe, manuelles);

    private static Declare Declaration(IEnumerable<Message> messages) => Assert.Single(messages.OfType<Declare>());

    private static JsonElement? Valeur(DiscoveryTranslator t, string topic, string charge) =>
        t.TraiterMessage(topic, charge).OfType<StateBatch>().SelectMany(b => b.States).SingleOrDefault()?.Value;

    [Fact]
    public void Une_entite_manuelle_est_declaree_sans_qu_aucun_appareil_s_annonce()
    {
        var t = Traducteur(Temperature, PileFaible);
        Assert.True(t.DeclarationEnAttente);

        var appareil = Assert.Single(Declaration(t.Declarer()).Devices);
        Assert.Equal(("manuel:Sonde salon", "Sonde salon"), (appareil.UniqueId, appareil.Name));
        var temperature = appareil.Entities.Single(e => e.Kind == EntityKind.Sensor);
        Assert.Equal(($"manuel:{Sonde}/temperature", "Température", DeviceClasses.Temperature, Units.Celsius, StateClass.Measurement),
            (temperature.UniqueId, temperature.Name, temperature.DeviceClass, temperature.Unit, temperature.StateClass));
        Assert.Empty(temperature.Commands);
    }

    [Fact]
    public void Le_plugin_s_abonne_aux_topics_des_entites_manuelles()
    {
        var t = Traducteur(Temperature, PileFaible);
        t.MqttConnecte();

        Assert.Equal([$"{Sonde}/battery_low", $"{Sonde}/temperature"], t.AbonnementsAFaire().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Un_topic_plat_donne_la_charge_entiere_et_les_unites_sont_converties()
    {
        var t = Traducteur(Temperature, new EntiteManuelle { Appareil = "Vieux module", Topic = "atelier/temp", Classe = "temperature", Unite = "°F" });
        t.Declarer();

        Assert.Equal(21.5, Valeur(t, $"{Sonde}/temperature", "21.5")!.Value.GetDouble());
        Assert.Equal(20, Valeur(t, "atelier/temp", "68")!.Value.GetDouble());
        Assert.Null(Valeur(t, $"{Sonde}/temperature", "nan"));   // une sonde en panne n'est pas une mesure
    }

    [Fact]
    public void Un_champ_se_lit_dans_la_charge_JSON_y_compris_dans_un_objet_ou_une_cle_a_tiret()
    {
        var t = Traducteur(
            new EntiteManuelle { Appareil = "Pluviomètre", Nom = "Intensité", Topic = "domomud/sensor/rain/jardin", Champ = "rain_rate", Unite = "mm/h" },
            new EntiteManuelle { Appareil = "Pluviomètre", Nom = "Signal", Topic = "domomud/sensor/rain/jardin", Champ = "radio.signal-level", Unite = "dBm" });
        t.Declarer();

        var etats = t.TraiterMessage("domomud/sensor/rain/jardin", """{"rain_rate": 2.4, "radio": {"signal-level": -71}}""")
            .OfType<StateBatch>().SelectMany(b => b.States).ToDictionary(e => e.EntityUniqueId, e => e.Value.GetDouble());

        Assert.Equal(2.4, etats["manuel:domomud/sensor/rain/jardin#rain_rate"]);
        Assert.Equal(-71, etats["manuel:domomud/sensor/rain/jardin#radio.signal-level"]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("ON", true)]
    [InlineData("0", false)]
    public void Sans_valeurs_saisies_un_binaire_comprend_true_ON_et_1(string charge, bool attendu)
    {
        var t = Traducteur(PileFaible);
        t.Declarer();

        Assert.Equal(attendu, Valeur(t, $"{Sonde}/battery_low", charge)!.Value.GetBoolean());
    }

    [Fact]
    public void Des_valeurs_saisies_sont_seules_reconnues()
    {
        var porte = new EntiteManuelle { Appareil = "Porte", Type = "binary_sensor", Topic = "garage/porte", Classe = "opening", ValeurOn = "open", ValeurOff = "closed" };
        var t = Traducteur(porte);
        t.Declarer();

        Assert.True(Valeur(t, "garage/porte", "open")!.Value.GetBoolean());
        Assert.Null(Valeur(t, "garage/porte", "true"));
    }

    [Fact]
    public void Un_capteur_sans_unite_ni_classe_est_un_libelle()
    {
        var t = Traducteur(new EntiteManuelle { Appareil = "Passerelle", Nom = "Version", Topic = "rfx2mqtt/version" });
        var version = Declaration(t.Declarer()).Devices.Single().Entities.Single();

        Assert.Equal((DeviceClasses.Enum, (string?)null, (StateClass?)null), (version.DeviceClass, version.Unit, version.StateClass));
        Assert.Equal("1.3.0", Valeur(t, "rfx2mqtt/version", "1.3.0")!.Value.GetString());
    }

    [Fact]
    public void Un_index_d_energie_est_un_compteur_croissant()
    {
        var t = Traducteur(new EntiteManuelle { Appareil = "OWL", Topic = "domomud/sensor/energy/owl/total", Classe = "energy", Unite = "Wh" });
        var index = Declaration(t.Declarer()).Devices.Single().Entities.Single();

        Assert.Equal((StateClass.TotalIncreasing, Units.KilowattHour), (index.StateClass, index.Unit));
    }

    [Fact]
    public void Un_interrupteur_avec_topic_de_commande_se_commande_et_bascule_sur_l_etat_connu()
    {
        var t = Traducteur(new EntiteManuelle
        {
            Appareil = "Prise Chacon", Type = "switch", Topic = "domomud/sensor/chacon/prise/state", TopicCommande = "domomud/chacon/prise/set",
        });
        var prise = Declaration(t.Declarer()).Devices.Single().Entities.Single();
        Assert.Equal([StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle], prise.Commands);

        Assert.Equal(new PublicationMqtt("domomud/chacon/prise/set", "ON"), Ordre(t, prise.UniqueId, StandardCommands.TurnOn).Publication);
        Assert.NotNull(Ordre(t, prise.UniqueId, StandardCommands.Toggle).Erreur);   // état inconnu : pas de bascule à l'aveugle

        Valeur(t, "domomud/sensor/chacon/prise/state", "on");
        Assert.Equal("OFF", Ordre(t, prise.UniqueId, StandardCommands.Toggle).Publication!.Charge);
    }

    [Fact]
    public void Sans_topic_de_commande_un_interrupteur_est_en_lecture_seule()
    {
        var t = Traducteur(new EntiteManuelle { Appareil = "Relais", Type = "switch", Topic = "relais/etat" });
        var relais = Declaration(t.Declarer()).Devices.Single().Entities.Single();

        Assert.Empty(relais.Commands);
        Assert.Null(Ordre(t, relais.UniqueId, StandardCommands.TurnOn).Publication);
    }

    [Fact]
    public void Renommer_une_entite_ou_son_appareil_garde_son_identite()
    {
        var avant = Declaration(Traducteur(Temperature).Declarer()).Devices.Single().Entities.Single().UniqueId;
        var renommee = Temperature;
        renommee.Nom = "Temp.";
        renommee.Appareil = "Sonde du séjour";

        Assert.Equal(avant, Declaration(Traducteur(renommee).Declarer()).Devices.Single().Entities.Single().UniqueId);
    }

    [Fact]
    public void Les_lignes_inutilisables_ou_en_double_sont_ecartees_sans_rien_casser()
    {
        var t = Traducteur(
            Temperature,
            Temperature,                                                               // même topic, même champ
            new EntiteManuelle { Appareil = "Toutes", Topic = "domomud/sensor/th/+" },  // un joker ne recevrait rien
            new EntiteManuelle { Topic = "sans/appareil" },
            new EntiteManuelle { Appareil = "Volet", Type = "cover", Topic = "volet/etat" });

        var entite = Assert.Single(Declaration(t.Declarer()).Devices.SelectMany(d => d.Entities));
        Assert.Equal($"manuel:{Sonde}/temperature", entite.UniqueId);
    }

    [Fact]
    public void Une_entite_manuelle_cohabite_avec_les_appareils_decouverts()
    {
        var t = Traducteur(Temperature);
        t.TraiterMessage(Esphome.TopicConfig("esp32-sht30-etage", "sensor", "temperature"), Esphome.Temperature("esp32-sht30-etage", "a1b2c3d4e5f6"));

        var appareils = Declaration(t.Declarer()).Devices;
        Assert.Equal(["a1b2c3d4e5f6", "manuel:Sonde salon"], appareils.Select(a => a.UniqueId));
    }

    private static (PublicationMqtt? Publication, string? Erreur) Ordre(DiscoveryTranslator t, string uniqueId, string commande) =>
        t.Traduire(new CommandRequest { Id = "1", EntityUniqueId = uniqueId, Command = commande });
}
