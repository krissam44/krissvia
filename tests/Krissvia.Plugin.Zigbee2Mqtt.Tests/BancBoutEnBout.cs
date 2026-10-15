using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Persistence.Sqlite;
using Krissvia.Plugin.Zigbee2Mqtt.Z2m;
using Krissvia.PluginHost;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.Plugin.Zigbee2Mqtt.Tests;

/// <summary>
/// Toute la chaîne, sans rien de simulé côté cœur : Zigbee2MQTT simulé → plugin → CoreClient → WebSocket sur
/// une vraie connexion TCP locale → PluginHub → registre et magasin d'états sur SQLite jetable.
/// </summary>
internal sealed class BancBoutEnBout : IAsyncDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");
    private readonly CancellationTokenSource _arret = new();
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly ConcurrentBag<IDisposable> _connexions = [];
    private readonly ConcurrentQueue<WebSocket> _cotesCoeur = new();
    private Task? _plugin;

    public EntityRegistry Registre { get; private set; } = null!;
    public EntityStateStore Etats { get; private set; } = null!;
    public PluginHub Hub { get; private set; } = null!;
    public PluginCommander Commandeur { get; private set; } = null!;
    public SimulateurZ2m Z2m { get; } = new();

    public static async Task<BancBoutEnBout> DemarrerAsync()
    {
        var banc = new BancBoutEnBout();
        var db = new SqliteDatabase($"Data Source={banc._fichier};Pooling=False");
        await db.InitializeAsync();

        banc.Registre = new EntityRegistry(new SqliteEntityRepository(db));
        banc.Etats = new EntityStateStore(new SqliteEntityStateRepository(db), TimeProvider.System);
        banc.Hub = new PluginHub(banc.Registre, banc.Etats, new PluginHostOptions { AllowUnregisteredPlugins = true },
            TimeProvider.System, NullLogger<PluginHub>.Instance);
        banc.Commandeur = new PluginCommander(banc.Hub, banc.Registre, NullLogger<PluginCommander>.Instance);

        var plugin = new Zigbee2MqttPlugin(new Zigbee2MqttTranslator("zigbee2mqtt"), banc.Z2m, NullLogger<Zigbee2MqttPlugin>.Instance);
        var client = new CoreClient(
            new CoreClientOptions { PluginId = "zigbee2mqtt", PluginVersion = "0.1.0", ReconnectDelays = [TimeSpan.FromMilliseconds(50)] },
            plugin, TimeProvider.System, NullLogger<CoreClient>.Instance, banc.ConnecterAsync);
        plugin.Coeur = client;

        banc._plugin = Task.WhenAll(plugin.RunMqttAsync(banc._arret.Token), client.RunAsync(banc._arret.Token));
        return banc;
    }

    /// <summary>Chaque connexion du plugin ouvre une session réelle dans le hub.</summary>
    private async Task<WebSocket> ConnecterAsync(CancellationToken ct)
    {
        var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        var client = new TcpClient();
        _connexions.Add(client);
        try
        {
            var connexion = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)ecoute.LocalEndpoint).Port, ct);
            var serveur = await ecoute.AcceptTcpClientAsync(ct);
            _connexions.Add(serveur);
            await connexion;

            var coteCoeur = WebSocket.CreateFromStream(serveur.GetStream(), new WebSocketCreationOptions { IsServer = true });
            _cotesCoeur.Enqueue(coteCoeur);
            _sessions.Add(Task.Run(() => Hub.RunAsync(new WebSocketChannel(coteCoeur), _arret.Token)));

            return WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
        }
        finally
        {
            ecoute.Stop();
        }
    }

    /// <summary>Coupe brutalement la connexion en cours, côté cœur.</summary>
    public void CouperLaConnexion()
    {
        foreach (var ws in _cotesCoeur) ws.Abort();
    }

    public async Task<string> EntityIdAsync(string uniqueId)
    {
        string? id = null;
        await AttendreAsync(() => (id = Registre.FindByUniqueIdAsync("zigbee2mqtt", uniqueId).GetAwaiter().GetResult()?.EntityId) is not null,
            $"entité {uniqueId} jamais déclarée");
        return id!;
    }

    public static async Task AttendreAsync(Func<bool> condition, string quoi)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > limite) Assert.Fail($"Condition jamais remplie : {quoi}.");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _arret.CancelAsync();
        try
        {
            await Task.WhenAll([.. _sessions, _plugin ?? Task.CompletedTask]).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        foreach (var c in _connexions) c.Dispose();
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
