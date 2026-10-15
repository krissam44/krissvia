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
