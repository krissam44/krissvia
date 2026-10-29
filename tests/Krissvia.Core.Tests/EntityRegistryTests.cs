using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Tests;

public class EntityRegistryTests
{
    private const string Plugin = "test";

    private static EntityRegistry NouveauRegistre() => new(new InMemoryEntityRepository());

    private static DeviceDescriptor Prise(string uniqueId = "0x01", string nom = "Prise salon") => new()
    {
        UniqueId = uniqueId,
        Name = nom,
        Entities =
        [
            new EntityDescriptor
            {
                UniqueId = uniqueId + ":switch",
                Kind = EntityKind.Switch,
                Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff],
            },
            new EntityDescriptor
            {
                UniqueId = uniqueId + ":power",
                Kind = EntityKind.Sensor,
                DeviceClass = DeviceClasses.Power,
                Unit = Units.Watt,
                StateClass = StateClass.Measurement,
            },
        ],
    };

    [Fact]
    public async Task Une_entite_disparue_se_supprime_et_son_appareil_part_avec_la_derniere()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise()]);
        var ids = (await registre.ListAsync()).Select(e => e.EntityId).ToList();
        await registre.AddAliasAsync("ancienne_prise", ids[0]);
        await registre.RegisterAsync(Plugin, []);   // le plugin ne déclare plus la prise : ses deux entités disparaissent

        Assert.True(await registre.DeleteAsync(ids[0]));
        Assert.Single(await registre.ListDevicesAsync());   // l'appareil garde son autre entité
        Assert.Null(await registre.ResolveAliasAsync("ancienne_prise"));

        Assert.True(await registre.DeleteAsync(ids[1]));
        Assert.Empty(await registre.ListAsync());
        Assert.Empty(await registre.ListDevicesAsync());
        Assert.False(await registre.DeleteAsync(ids[1]));   // déjà partie
    }

    [Fact]
    public async Task Une_entite_encore_declaree_ne_se_supprime_pas_elle_reviendrait()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise()]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => registre.DeleteAsync("switch.prise_salon"));

        Assert.Contains("Désactivez-la", ex.Message);
        Assert.Equal(2, (await registre.ListAsync()).Count);
    }

    [Fact]
    public async Task La_puissance_declaree_se_regle_s_efface_et_refuse_le_negatif()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise()]);

        await registre.SetNominalPowerAsync("switch.prise_salon", 1499.6);
        Assert.Equal(1500, (await registre.GetAsync("switch.prise_salon"))!.NominalPower);

        await registre.SetNominalPowerAsync("switch.prise_salon", 0);
        Assert.Null((await registre.GetAsync("switch.prise_salon"))!.NominalPower);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registre.SetNominalPowerAsync("switch.prise_salon", -5));
    }

    [Fact]
    public async Task Le_widget_de_l_entite_est_un_champ_de_l_utilisateur()
    {
        var registre = new EntityRegistry(new InMemoryEntityRepository());
        await registre.RegisterAsync("zigbee2mqtt", [Prise()]);

        await registre.SetWidgetAsync("switch.prise_salon", " ma-prise ");
        await registre.RegisterAsync("zigbee2mqtt", [Prise()]);
        Assert.Equal("ma-prise", (await registre.GetAsync("switch.prise_salon"))!.Widget);

        await registre.SetWidgetAsync("switch.prise_salon", "");
        Assert.Null((await registre.GetAsync("switch.prise_salon"))!.Widget);
    }

    [Fact]
    public async Task Les_entites_chargees_sont_lisibles_sans_attente()
    {
        var registre = NouveauRegistre();
        Assert.Empty(registre.Loaded);

        await registre.RegisterAsync(Plugin, [Prise()]);
        await registre.SetNominalPowerAsync("switch.prise_salon", 800);

        Assert.Equal(800, Assert.Single(registre.Loaded, e => e.EntityId == "switch.prise_salon").NominalPower);
    }

    [Fact]
    public async Task Une_declaration_attribue_des_identifiants_lisibles()
    {
        var registre = NouveauRegistre();

        var resultat = await registre.RegisterAsync(Plugin, [Prise()]);

        Assert.Equal(new[] { "switch.prise_salon", "sensor.prise_salon_power" }, resultat.Created);
        Assert.Empty(resultat.NowMissing);
    }

    [Fact]
    public async Task Redeclarer_la_meme_chose_ne_change_rien()
    {
        var publies = new List<Krissvia.Core.Events.CoreEvent>();
        var registre = new EntityRegistry(new InMemoryEntityRepository(), new PublieurEnListe(publies));

        await registre.RegisterAsync(Plugin, [Prise()]);
        var seconde = await registre.RegisterAsync(Plugin, [Prise()]);

        Assert.Empty(seconde.Created);
        Assert.Empty(seconde.NowMissing);
        Assert.Equal(2, (await registre.ListAsync()).Count);
        Assert.IsType<Krissvia.Core.Events.RegistryChanged>(Assert.Single(publies));
    }

    [Fact]
    public async Task Les_identifiants_ne_changent_pas_quand_le_plugin_renomme_l_appareil()
    {
        var registre = NouveauRegistre();

        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise salon")]);
        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise séjour")]);

        Assert.NotNull(await registre.GetAsync("switch.prise_salon"));
        Assert.Null(await registre.GetAsync("switch.prise_sejour"));
        Assert.Equal(2, (await registre.ListAsync()).Count);
    }

    [Fact]
    public async Task Les_champs_de_l_utilisateur_survivent_a_une_nouvelle_declaration()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise()]);

        await registre.RenameAsync("switch.prise_salon", "Lampe du coin");
        await registre.SetUsageAsync("switch.prise_salon", "eclairage");
        await registre.SetEnabledAsync("switch.prise_salon", false);
        await registre.RegisterAsync(Plugin, [Prise()]);

        var entite = await registre.GetAsync("switch.prise_salon");
        Assert.NotNull(entite);
        Assert.Equal("Lampe du coin", entite.DisplayName);
        Assert.Equal("eclairage", entite.Usage);
        Assert.False(entite.Enabled);
    }

    [Fact]
    public async Task Le_nom_affiche_suit_le_plugin_tant_que_l_utilisateur_ne_l_a_pas_change()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise salon")]);

        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise séjour")]);

        Assert.Equal("Prise séjour", Assert.Single(await registre.ListDevicesAsync()).DisplayName);
        Assert.Equal("Prise séjour power", (await registre.GetAsync("sensor.prise_salon_power"))!.DisplayName);
        Assert.Equal("Prise séjour", (await registre.GetAsync("switch.prise_salon"))!.DisplayName);
    }

    [Fact]
    public async Task Un_nom_choisi_par_l_utilisateur_ne_suit_plus_le_plugin()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise salon")]);
        await registre.RenameAsync("switch.prise_salon", "Lampe du coin");

        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise séjour")]);
        await registre.RegisterAsync(Plugin, [Prise("0x01", "Prise bureau")]);

        Assert.Equal("Lampe du coin", (await registre.GetAsync("switch.prise_salon"))!.DisplayName);
        Assert.Equal("Prise bureau power", (await registre.GetAsync("sensor.prise_salon_power"))!.DisplayName);
    }

    [Fact]
    public async Task Un_appareil_dont_l_identifiant_change_avec_toutes_ses_entites_est_le_meme_renomme()
    {
        // Entités manuelles de MQTT Discovery : l'identifiant de l'appareil est tiré de son nom.
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise("manuel:Sonde salon", "Sonde salon")]);
        var avant = Assert.Single(await registre.ListDevicesAsync());
        await registre.SetRoomAsync(avant.DeviceId, "Salon");

        var renomme = Prise("manuel:Sonde salon", "Sonde du séjour") with { UniqueId = "manuel:Sonde du séjour" };
        var resultat = await registre.RegisterAsync(Plugin, [renomme]);

        var apres = Assert.Single(await registre.ListDevicesAsync());
        Assert.Equal((avant.DeviceId, "manuel:Sonde du séjour", "Sonde du séjour", "Salon"), (apres.DeviceId, apres.UniqueId, apres.DisplayName, apres.Room));
        Assert.Empty(resultat.Created);
        Assert.Equal("Sonde du séjour power", (await registre.GetAsync("sensor.sonde_salon_power"))!.DisplayName);
    }

    [Fact]
    public async Task Un_appareil_coupe_en_deux_garde_son_identite_pour_le_premier_seulement()
    {
        var registre = NouveauRegistre();
        var origine = Prise("manuel:A", "A");
        await registre.RegisterAsync(Plugin, [origine]);
        var ancien = Assert.Single(await registre.ListDevicesAsync()).DeviceId;

        await registre.RegisterAsync(Plugin,
        [
            new DeviceDescriptor { UniqueId = "manuel:B", Name = "B", Entities = [origine.Entities[0]] },
            new DeviceDescriptor { UniqueId = "manuel:C", Name = "C", Entities = [origine.Entities[1]] },
        ]);

        var appareils = await registre.ListDevicesAsync();
        Assert.Equal(2, appareils.Count);
        Assert.Equal(ancien, appareils.Single(a => a.UniqueId == "manuel:B").DeviceId);
        Assert.NotEqual(ancien, appareils.Single(a => a.UniqueId == "manuel:C").DeviceId);
    }

    [Fact]
    public async Task Un_appareil_encore_declare_n_est_jamais_pris_pour_un_renomme()
    {
        var registre = NouveauRegistre();
        var origine = Prise("0x01", "Prise");
        await registre.RegisterAsync(Plugin, [origine]);

        // Une entité passe d'un appareil à un autre, l'ancien restant déclaré : rien n'est renommé.
        await registre.RegisterAsync(Plugin,
        [
            origine with { Entities = [origine.Entities[0]] },
            new DeviceDescriptor { UniqueId = "0x02", Name = "Autre", Entities = [origine.Entities[1]] },
        ]);

        Assert.Equal(["0x01", "0x02"], (await registre.ListDevicesAsync()).Select(a => a.UniqueId).Order());
    }

    [Fact]
    public async Task Deux_appareils_de_meme_nom_obtiennent_des_identifiants_distincts()
    {
        var registre = NouveauRegistre();

        await registre.RegisterAsync(Plugin, [Prise("0x01"), Prise("0x02")]);

        Assert.NotNull(await registre.GetAsync("switch.prise_salon"));
        Assert.NotNull(await registre.GetAsync("switch.prise_salon_2"));
        Assert.NotNull(await registre.GetAsync("sensor.prise_salon_power_2"));
    }

    [Fact]
    public async Task Une_entite_qui_disparait_est_marquee_manquante_puis_retrouvee()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise("0x01"), Prise("0x02", "Prise cuisine")]);

        var disparition = await registre.RegisterAsync(Plugin, [Prise("0x01")]);

        Assert.Equal(
            new[] { "sensor.prise_cuisine_power", "switch.prise_cuisine" },
            disparition.NowMissing.Order().ToArray());
        var manquante = await registre.GetAsync("switch.prise_cuisine");
        Assert.NotNull(manquante);
        Assert.True(manquante.Missing);

        await registre.RegisterAsync(Plugin, [Prise("0x01"), Prise("0x02", "Prise cuisine")]);

        var retrouvee = await registre.GetAsync("switch.prise_cuisine");
        Assert.NotNull(retrouvee);
        Assert.False(retrouvee.Missing);
    }

    [Fact]
    public async Task Un_plugin_ne_touche_pas_aux_entites_d_un_autre()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync("zigbee", [Prise("0x01")]);

        await registre.RegisterAsync("shelly", [Prise("192.168.1.20", "Shelly cave")]);

        var zigbee = await registre.GetAsync("switch.prise_salon");
        Assert.NotNull(zigbee);
        Assert.False(zigbee.Missing);
    }

    [Fact]
    public async Task Une_entite_se_retrouve_par_l_identifiant_du_plugin()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise("0x01")]);

        var entite = await registre.FindByUniqueIdAsync(Plugin, "0x01:power");

        Assert.NotNull(entite);
        Assert.Equal("sensor.prise_salon_power", entite.EntityId);
    }

    [Fact]
    public async Task Une_entite_declaree_deux_fois_est_refusee_sans_rien_ecrire()
    {
        var registre = NouveauRegistre();
        var doublon = new DeviceDescriptor
        {
            UniqueId = "0x09",
            Entities =
            [
                new EntityDescriptor { UniqueId = "x", Kind = EntityKind.Switch },
                new EntityDescriptor { UniqueId = "x", Kind = EntityKind.Switch },
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => registre.RegisterAsync(Plugin, [doublon]));

        Assert.Empty(await registre.ListAsync());
    }

    [Fact]
    public async Task Les_alias_retrouvent_l_entite_et_refusent_les_conflits()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise("0x01"), Prise("0x02", "Prise cuisine")]);

        await registre.AddAliasAsync("domomud:topic:zigbee2mqtt/Prise salon", "switch.prise_salon");
        await registre.AddAliasAsync("domomud:topic:zigbee2mqtt/Prise salon", "switch.prise_salon");

        Assert.Equal("switch.prise_salon", await registre.ResolveAliasAsync("domomud:topic:zigbee2mqtt/Prise salon"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registre.AddAliasAsync("domomud:topic:zigbee2mqtt/Prise salon", "switch.prise_cuisine"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => registre.AddAliasAsync("autre", "switch.fantome"));
    }

    [Fact]
    public async Task Modifier_une_entite_inconnue_est_une_erreur_explicite()
    {
        var registre = NouveauRegistre();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => registre.RenameAsync("switch.fantome", "X"));
    }

    [Fact]
    public async Task Un_nom_vide_est_refuse()
    {
        var registre = NouveauRegistre();
        await registre.RegisterAsync(Plugin, [Prise()]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => registre.RenameAsync("switch.prise_salon", "   "));
    }
}

/// <summary>Publieur synchrone pour les tests : garde les événements dans l'ordre.</summary>
internal sealed class PublieurEnListe(List<Krissvia.Core.Events.CoreEvent> liste) : Krissvia.Core.Events.IEventPublisher
{
    public void Publish(Krissvia.Core.Events.CoreEvent e)
    {
        lock (liste) liste.Add(e);
    }
}
