using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Persistence.Sqlite;
using Krissvia.Plugin.Ipp.Protocole;
using Krissvia.PluginHost;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.Plugin.Ipp.Tests;

/// <summary>
/// Toute la chaîne, sans rien de simulé côté cœur : fausse imprimante en HTTP → client IPP → plugin → CoreClient →
/// WebSocket sur une vraie connexion TCP locale → PluginHub → registre et magasin d'états sur SQLite jetable.
/// </summary>
internal sealed class BancBoutEnBout : IAsyncDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"krissvia-{Guid.NewGuid():N}.db");
    private readonly CancellationTokenSource _arret = new();
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly ConcurrentBag<IDisposable> _connexions = [];
    private readonly IppHttpClient _ipp = new();
    private Task? _plugin;

    public EntityRegistry Registre { get; private set; } = null!;
    public EntityStateStore Etats { get; private set; } = null!;
    public PluginHub Hub { get; private set; } = null!;
    public EventBus Bus { get; } = new();
    public CommandBus Commandeur { get; private set; } = null!;
    public FausseImprimante Imprimante { get; } = new();

    public static async Task<BancBoutEnBout> DemarrerAsync()
    {
        var banc = new BancBoutEnBout();
        var db = new SqliteDatabase($"Data Source={banc._fichier};Pooling=False");
        await db.InitializeAsync();

        banc.Registre = new EntityRegistry(new SqliteEntityRepository(db), banc.Bus);
        banc.Etats = new EntityStateStore(new SqliteEntityStateRepository(db), TimeProvider.System, banc.Bus);
        banc.Hub = new PluginHub(banc.Registre, banc.Etats, new PluginHostOptions { AllowUnregisteredPlugins = true },
            TimeProvider.System, NullLogger<PluginHub>.Instance, banc.Bus);
        banc.Commandeur = new CommandBus(banc.Registre, new PluginCommander(banc.Hub, NullLogger<PluginCommander>.Instance),
            banc.Bus, TimeProvider.System);

        // Relevé toutes les heures : seul le premier relevé et « Vérifier » interrogent l'imprimante pendant le test.
        var plugin = new IppPlugin(banc._ipp, null, TimeProvider.System, NullLogger<IppPlugin>.Instance,
            new OptionsIpp([banc.Imprimante.Url], false, TimeSpan.FromHours(1)));
        var client = new CoreClient(
            new CoreClientOptions { PluginId = "ipp", PluginVersion = "0.1.0", ReconnectDelays = [TimeSpan.FromMilliseconds(50)] },
            plugin, TimeProvider.System, NullLogger<CoreClient>.Instance, banc.ConnecterAsync);
        plugin.Coeur = client;

        banc._plugin = Task.WhenAll(plugin.RunAsync(banc._arret.Token), client.RunAsync(banc._arret.Token));
        return banc;
    }

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
            _sessions.Add(Task.Run(() => Hub.RunAsync(new WebSocketChannel(coteCoeur), _arret.Token)));
            return WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
        }
        finally
        {
            ecoute.Stop();
        }
    }

    public async Task<string> EntityIdAsync(string uniqueId)
    {
        string? id = null;
        await AttendreAsync(() => (id = Registre.FindByUniqueIdAsync("ipp", uniqueId).GetAwaiter().GetResult()?.EntityId) is not null,
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
        Commandeur?.Dispose();
        await Bus.DisposeAsync();
        try
        {
            await Task.WhenAll([.. _sessions, _plugin ?? Task.CompletedTask]).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        await Imprimante.DisposeAsync();
        _ipp.Dispose();
        foreach (var c in _connexions) c.Dispose();
        foreach (var f in new[] { _fichier, _fichier + "-wal", _fichier + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

/// <summary>Une imprimante jusqu'au registre et au magasin d'états du cœur, et « Vérifier » dans l'autre sens.</summary>
public class BoutEnBoutTests
{
    private const string Samsung = "16a65700-007c-1000-bb49-30cda71ce2f2";

    [Fact]
    public async Task L_imprimante_arrive_dans_le_coeur_par_une_seule_lecture()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();

        var imprimante = await banc.EntityIdAsync(Samsung);
        var toner = await banc.EntityIdAsync($"{Samsung}:conso:black_toner");
        var probleme = await banc.EntityIdAsync($"{Samsung}:probleme");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(toner) is { Available: true } && banc.Etats.Get(imprimante) is { Available: true },
            "relevé de l'imprimante");

        Assert.Equal(96, banc.Etats.Get(toner)!.Value.GetInt32());
        Assert.False(banc.Etats.Get(probleme)!.Value.GetBoolean());
        Assert.Equal("Prête", banc.Etats.Get(imprimante)!.Value.GetProperty("state_label").GetString());
        var entite = (await banc.Registre.ListAsync()).Single(e => e.EntityId == imprimante);
        Assert.Equal((EntityKind.Sensor, DeviceClasses.Printer), (entite.Kind, entite.DeviceClass));
        Assert.Equal(7, (await banc.Registre.ListAsync()).Count(e => e.IntegrationId == "ipp"));

        // Le plugin ne sait que lire : aucune autre opération n'a atteint l'imprimante.
        Assert.All(banc.Imprimante.Operations, o => Assert.Equal(IppCodec.GetPrinterAttributes, o));
    }

    [Fact]
    public async Task Verifier_depuis_le_coeur_releve_et_publie_le_bourrage()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        var imprimante = await banc.EntityIdAsync(Samsung);
        var probleme = await banc.EntityIdAsync($"{Samsung}:probleme");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(probleme) is { Available: true }, "premier relevé");
        var avant = banc.Imprimante.Operations.Count;

        banc.Imprimante.Trame = "samsung-m2020-bourrage.ipp.txt";
        var issue = await banc.Commandeur.SendAsync(imprimante, IppPlugin.Refresh);

        Assert.Equal(CommandOutcome.Accepted, issue);
        Assert.Equal(avant + 1, banc.Imprimante.Operations.Count);
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(probleme)?.Value.GetBoolean() == true, "problème levé");
        Assert.Equal("stopped", banc.Etats.Get(imprimante)!.Value.GetProperty("state").GetString());
        Assert.All(banc.Imprimante.Operations, o => Assert.Equal(IppCodec.GetPrinterAttributes, o));
    }

    [Fact]
    public async Task Une_erreur_IPP_de_l_imprimante_se_distingue_d_une_trame_illisible()
    {
        await using var imprimante = new FausseImprimante { Erreur = 0x0406 };
        using var client = new IppHttpClient();

        var ex = await Assert.ThrowsAsync<IppStatusException>(() => client.GetPrinterAttributesAsync(imprimante.Url, CancellationToken.None));

        Assert.Equal(0x0406, ex.Status);
        Assert.Contains("chemin", ex.Message, StringComparison.Ordinal);   // le remède : une adresse au mauvais chemin
    }
}
