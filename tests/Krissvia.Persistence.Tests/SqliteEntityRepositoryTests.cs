using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Persistence.Tests;

/// <summary>Base SQLite dans un fichier temporaire. Pooling désactivé : le fichier est libéré dès la fermeture.</summary>
public sealed class SqliteEntityRepositoryTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private async Task<SqliteDatabase> OuvrirAsync()
    {
        var db = new SqliteDatabase($"Data Source={_fichier};Pooling=False");
        await db.InitializeAsync();
        return db;
    }

    /// <summary>Chaque appel recrée tous les objets : on simule un redémarrage de l'application.</summary>
    private async Task<EntityRegistry> OuvrirRegistreAsync() =>
        new(new SqliteEntityRepository(await OuvrirAsync()));

    private static DeviceDescriptor Prise() => new()
    {
        UniqueId = "0x01",
        Name = "Prise salon",
        Manufacturer = "Tuya",
        Entities =
        [
            new EntityDescriptor
            {
                UniqueId = "0x01:switch",
                Kind = EntityKind.Switch,
                Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle],
            },
            new EntityDescriptor
            {
                UniqueId = "0x01:energy",
                Kind = EntityKind.Sensor,
                DeviceClass = DeviceClasses.Energy,
                Unit = Units.KilowattHour,
                StateClass = StateClass.TotalIncreasing,
            },
        ],
    };

    [Fact]
    public async Task Supprimer_une_entite_efface_alias_dernier_etat_et_appareil_orphelin_et_survit_au_redemarrage()
    {
        var registre = await OuvrirRegistreAsync();
        await registre.RegisterAsync("z2m", [Prise()]);
        var ids = (await registre.ListAsync()).Select(e => e.EntityId).ToList();
        await registre.AddAliasAsync("vieille_prise", ids[0]);
        var db = await OuvrirAsync();
        await using (var c = await db.OpenAsync())
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO entity_states (entity_id, value_json, measured_at_ticks) VALUES ('{ids[0]}', 'true', 1)";
            await cmd.ExecuteNonQueryAsync();
        }

        await registre.RegisterAsync("z2m", []);
        foreach (var id in ids) Assert.True(await registre.DeleteAsync(id));   // l'alias (clé étrangère) ne bloque pas

        var relu = await OuvrirRegistreAsync();
        Assert.Empty(await relu.ListAsync());
        Assert.Empty(await relu.ListDevicesAsync());
        Assert.Null(await relu.ResolveAliasAsync("vieille_prise"));
        await using (var c = await db.OpenAsync())
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM entity_states";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Le_schema_s_initialise_deux_fois_sans_erreur()
    {
        await OuvrirAsync();
        await OuvrirAsync();
    }

    [Fact]
    public async Task Le_registre_survit_a_un_redemarrage()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);
        await avant.RenameAsync("switch.prise_salon", "Lampe du coin");
        await avant.SetUsageAsync("switch.prise_salon", "eclairage");

        var apres = await OuvrirRegistreAsync();

        var ids = (await apres.ListAsync()).Select(e => e.EntityId).ToArray();
        Assert.Equal(new[] { "sensor.prise_salon_energy", "switch.prise_salon" }, ids);

        var interrupteur = await apres.GetAsync("switch.prise_salon");
        Assert.NotNull(interrupteur);
        Assert.Equal("Lampe du coin", interrupteur.DisplayName);
        Assert.Equal("eclairage", interrupteur.Usage);
        Assert.Equal(EntityKind.Switch, interrupteur.Kind);
        Assert.Equal(new[] { "turn_on", "turn_off", "toggle" }, interrupteur.Commands);

        var energie = await apres.GetAsync("sensor.prise_salon_energy");
        Assert.NotNull(energie);
        Assert.Equal(StateClass.TotalIncreasing, energie.StateClass);
        Assert.Equal("kWh", energie.Unit);
    }

    [Fact]
    public async Task Redeclarer_apres_redemarrage_ne_cree_pas_de_doublon()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);

        var apres = await OuvrirRegistreAsync();
        var resultat = await apres.RegisterAsync("test", [Prise()]);

        Assert.Empty(resultat.Created);
        Assert.Equal(2, (await apres.ListAsync()).Count);
    }

    [Fact]
    public async Task La_puissance_declaree_survit_au_redemarrage_et_a_une_redeclaration()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);
        await avant.SetNominalPowerAsync("switch.prise_salon", 2000);

        var apres = await OuvrirRegistreAsync();
        await apres.RegisterAsync("test", [Prise()]);

        Assert.Equal(2000, (await apres.GetAsync("switch.prise_salon"))!.NominalPower);
        Assert.Null((await apres.GetAsync("sensor.prise_salon_energy"))!.NominalPower);
    }

    [Fact]
    public async Task La_piece_d_un_appareil_survit_au_redemarrage_et_a_une_redeclaration()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);
        var appareil = Assert.Single(await avant.ListDevicesAsync());
        await avant.SetRoomAsync(appareil.DeviceId, " Salon ");

        var apres = await OuvrirRegistreAsync();
        await apres.RegisterAsync("test", [Prise()]);

        Assert.Equal("Salon", Assert.Single(await apres.ListDevicesAsync()).Room);
    }

    [Fact]
    public async Task Une_base_existante_gagne_la_colonne_de_puissance()
    {
        // Base au schéma v3, telle qu'elle existait avant la puissance déclarée.
        var db = await OuvrirAsync();
        await using (var connexion = await db.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(connexion, $"{SansNomPropose} ALTER TABLE entities DROP COLUMN widget; ALTER TABLE entities DROP COLUMN nominal_power; PRAGMA user_version = 3;");
        }

        var registre = await OuvrirRegistreAsync();
        await registre.RegisterAsync("test", [Prise()]);
        await registre.SetNominalPowerAsync("switch.prise_salon", 1500);

        Assert.Equal(1500, (await (await OuvrirRegistreAsync()).GetAsync("switch.prise_salon"))!.NominalPower);
    }

    [Fact]
    public async Task Une_base_v4_gagne_la_colonne_du_widget_qui_survit_a_une_redeclaration()
    {
        // Base au schéma v4, telle qu'elle existait avant le widget par entité.
        var db = await OuvrirAsync();
        await using (var connexion = await db.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(connexion, $"{SansNomPropose} ALTER TABLE entities DROP COLUMN widget; PRAGMA user_version = 4;");
        }

        var registre = await OuvrirRegistreAsync();
        await registre.RegisterAsync("test", [Prise()]);
        await registre.SetWidgetAsync("switch.prise_salon", "ma-prise");
        var apres = await OuvrirRegistreAsync();
        await apres.RegisterAsync("test", [Prise()]);   // le plugin redéclare : le choix de l'utilisateur reste

        Assert.Equal("ma-prise", (await (await OuvrirRegistreAsync()).GetAsync("switch.prise_salon"))!.Widget);
    }

    /// <summary>Les colonnes des schémas v7 et v8, retirées pour rejouer une base plus ancienne.</summary>
    private const string SansNomPropose = "ALTER TABLE devices DROP COLUMN proposed_name; ALTER TABLE entities DROP COLUMN proposed_name; " +
                                          "ALTER TABLE entity_states DROP COLUMN changed_at_ticks;";

    [Fact]
    public async Task Le_nom_affiche_suit_le_nom_propose_apres_redemarrage_tant_qu_il_n_a_pas_ete_change()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);

        var apres = await OuvrirRegistreAsync();
        await apres.RegisterAsync("test", [Prise() with { Name = "Prise séjour" }]);

        Assert.Equal("Prise séjour", (await apres.GetAsync("switch.prise_salon"))!.DisplayName);
        Assert.Equal("Prise séjour", Assert.Single(await (await OuvrirRegistreAsync()).ListDevicesAsync()).DisplayName);
    }

    [Fact]
    public async Task Une_base_v6_garde_ses_noms_le_plugin_ne_sachant_pas_s_ils_ont_ete_choisis()
    {
        // Base au schéma v6 : rien ne dit si « Prise salon » vient du plugin ou de l'utilisateur.
        var db = await OuvrirAsync();
        await using (var connexion = await db.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(connexion, $"{SansNomPropose} PRAGMA user_version = 6;");
        }

        await (await OuvrirRegistreAsync()).RegisterAsync("test", [Prise()]);
        await using (var connexion = await db.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(connexion, "UPDATE entities SET proposed_name = NULL; UPDATE devices SET proposed_name = NULL;");
        }

        var registre = await OuvrirRegistreAsync();
        await registre.RegisterAsync("test", [Prise() with { Name = "Prise séjour" }]);

        Assert.Equal("Prise salon", (await registre.GetAsync("switch.prise_salon"))!.DisplayName);
    }

    [Fact]
    public async Task Le_statut_manquant_est_persiste()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);
        await avant.RegisterAsync("test", []);

        var apres = await OuvrirRegistreAsync();

        var entite = await apres.GetAsync("switch.prise_salon");
        Assert.NotNull(entite);
        Assert.True(entite.Missing);
    }

    [Fact]
    public async Task Les_alias_survivent_a_un_redemarrage()
    {
        var avant = await OuvrirRegistreAsync();
        await avant.RegisterAsync("test", [Prise()]);
        await avant.AddAliasAsync("domomud:topic:zigbee2mqtt/Prise salon", "switch.prise_salon");

        var apres = await OuvrirRegistreAsync();

        Assert.Equal("switch.prise_salon", await apres.ResolveAliasAsync("domomud:topic:zigbee2mqtt/Prise salon"));
        Assert.Null(await apres.ResolveAliasAsync("inconnu"));
    }

    [Fact]
    public async Task Une_ecriture_est_atomique()
    {
        var depot = new SqliteEntityRepository(await OuvrirAsync());
        var appareil = new RegisteredDevice
        {
            DeviceId = "d1",
            IntegrationId = "test",
            UniqueId = "u",
            DisplayName = "Appareil",
        };
        var orpheline = new RegisteredEntity
        {
            EntityId = "switch.x",
            DeviceId = "appareil-inexistant",
            IntegrationId = "test",
            UniqueId = "u:switch",
            Kind = EntityKind.Switch,
            DisplayName = "X",
        };

        await Assert.ThrowsAnyAsync<Exception>(() => depot.SaveAsync([appareil], [orpheline]));

        Assert.Empty(await depot.ListDevicesAsync());
    }
}
