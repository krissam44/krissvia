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
            await Dapper.SqlMapper.ExecuteAsync(connexion, "ALTER TABLE entities DROP COLUMN widget; ALTER TABLE entities DROP COLUMN nominal_power; PRAGMA user_version = 3;");
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
            await Dapper.SqlMapper.ExecuteAsync(connexion, "ALTER TABLE entities DROP COLUMN widget; PRAGMA user_version = 4;");
        }

        var registre = await OuvrirRegistreAsync();
        await registre.RegisterAsync("test", [Prise()]);
        await registre.SetWidgetAsync("switch.prise_salon", "ma-prise");
        var apres = await OuvrirRegistreAsync();
        await apres.RegisterAsync("test", [Prise()]);   // le plugin redéclare : le choix de l'utilisateur reste

        Assert.Equal("ma-prise", (await (await OuvrirRegistreAsync()).GetAsync("switch.prise_salon"))!.Widget);
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
