using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Le vrai graphe (registre, magasin d'états, hub, commandeur) sur une base SQLite jetable. Seules l'horloge et
/// la connexion sont simulées : la connexion par une paire de canaux en mémoire.
/// </summary>
internal sealed class BancPlugins : IAsyncDisposable
{
    private readonly string _fichier;
    private readonly CancellationTokenSource _arret = new();
    private readonly List<Task> _sessions = [];

    public FakeTimeProvider Horloge { get; } = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
    public PluginHostOptions Options { get; } = new() { AllowUnregisteredPlugins = true };
    public EntityRegistry Registre { get; }
    public EntityStateStore Etats { get; }
    public PluginHub Hub { get; }
    public EventBus Bus { get; } = new();
    public CommandBus Commandeur { get; }

    /// <summary>Contrat 0.3 : ce que chaque plugin a le droit de lire ou de commander (d'ordinaire, ses réglages).</summary>
    public Dictionary<(string Plugin, string Entite), Services.EntityAccess> Droits { get; } = [];

    private BancPlugins(SqliteDatabase db, string fichier)
    {
        _fichier = fichier;
        Registre = new EntityRegistry(new SqliteEntityRepository(db), Bus);
        Etats = new EntityStateStore(new SqliteEntityStateRepository(db), Horloge, Bus);
        Hub = new PluginHub(Registre, Etats, Options, Horloge, NullLogger<PluginHub>.Instance, Bus);
        Commandeur = new CommandBus(Registre, new PluginCommander(Hub, NullLogger<PluginCommander>.Instance), Bus, Horloge);
        Hub.Gateway = new PluginGateway(Registre, Etats, Commandeur, Bus, (p, e) => Droits.GetValueOrDefault((p, e), Services.EntityAccess.None));
    }

    public static async Task<BancPlugins> CreerAsync()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");
        var db = new SqliteDatabase($"Data Source={fichier};Pooling=False");
        await db.InitializeAsync();
        return new BancPlugins(db, fichier);
    }

    /// <summary>Ouvre une connexion sans rien envoyer : la session tourne en tâche de fond.</summary>
    public (CanalMemoire Plugin, Task Session) Ouvrir()
    {
        var (coeur, plugin) = CanalMemoire.Paire();
        var session = Task.Run(() => Hub.RunAsync(coeur, _arret.Token));
        _sessions.Add(session);
        return (plugin, session);
    }

    /// <summary>Connexion complète : hello, puis lecture du welcome.</summary>
    public async Task<(CanalMemoire Plugin, Task Session)> ConnecterAsync(string pluginId = "zigbee2mqtt", string? jeton = null)
    {
        var (plugin, session) = Ouvrir();
        await plugin.EnvoyerAsync(Hello(pluginId, jeton));
        await plugin.RecevoirAsync<Welcome>();
        return (plugin, session);
    }

    /// <summary>Connexion, puis déclaration de la prise de test et une mesure de chacune de ses entités.</summary>
    public async Task<(CanalMemoire Plugin, Task Session)> ConnecterAvecPriseAsync()
    {
        var connexion = await ConnecterAsync();
        await connexion.Plugin.EnvoyerAsync(new Declare { Devices = [Prise()] });
        await connexion.Plugin.EnvoyerAsync(Mesures(("0x01:switch", true), ("0x01:power", 1200.5)));
        await AttendreAsync(() => Etats.Get(Interrupteur) is { Available: true } && Etats.Get(Puissance) is { Available: true });
        return connexion;
    }

    public static Hello Hello(string pluginId = "zigbee2mqtt", string? jeton = null, string? version = null) => new()
    {
        PluginId = pluginId,
        PluginVersion = "0.1.0",
        ContractVersion = version ?? ContractInfo.Version,
        Token = jeton,
    };

    public const string Interrupteur = "switch.prise_salon";
    public const string Puissance = "sensor.prise_salon_power";

    public static DeviceDescriptor Prise() => new()
    {
        UniqueId = "0x01",
        Name = "Prise salon",
        Entities =
        [
            new EntityDescriptor
            {
                UniqueId = "0x01:switch",
                Kind = EntityKind.Switch,
                Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff],
            },
            new EntityDescriptor
            {
                UniqueId = "0x01:power",
                Kind = EntityKind.Sensor,
                DeviceClass = DeviceClasses.Power,
                Unit = Units.Watt,
                StateClass = StateClass.Measurement,
            },
        ],
    };

    public static StateBatch Mesures(params (string UniqueId, object Valeur)[] valeurs) => new()
    {
        States = valeurs
            .Select(v => new EntityState { EntityUniqueId = v.UniqueId, Value = JsonSerializer.SerializeToElement(v.Valeur) })
            .ToList(),
    };

    /// <summary>
    /// Attend qu'une condition devienne vraie : la session traite les messages sur son propre fil. Échoue au
    /// bout de 5 s (temps réel, indépendant de l'horloge simulée).
    /// </summary>
    public static async Task AttendreAsync(Func<bool> condition, string? quoi = null)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limite) Assert.Fail($"Condition jamais remplie{(quoi is null ? "" : $" : {quoi}")}.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Avance l'horloge par pas, en laissant aux minuteries le temps de se réarmer entre deux pas : avancer
    /// d'un coup ne déclencherait chaque minuterie qu'une fois.
    /// </summary>
    public async Task AvancerAsync(TimeSpan duree, TimeSpan pas)
    {
        for (var ecoule = TimeSpan.Zero; ecoule < duree; ecoule += pas)
        {
            Horloge.Advance(pas);
            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _arret.CancelAsync();
        Commandeur?.Dispose();
        await Bus.DisposeAsync();
        try
        {
            await Task.WhenAll(_sessions).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
