using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Krissvia.Contracts;
using Krissvia.PluginHost.Services;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Le SDK C# du contrat 0.3, de bout en bout : un vrai <see cref="CoreClient"/> (la platine), sur une vraie connexion
/// WebSocket locale, face au hub et à la prise du plugin Zigbee du banc.
/// </summary>
public class SdkContrat03Tests
{
    private sealed class Platine : ICorePlugin
    {
        public IReadOnlyList<Message> Snapshot() => [new Declare { Devices = [] }];

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct) =>
            Task.FromResult(new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed });
    }

    private static async Task<(CoreClient Client, Task Fin)> ConnecterAsync(BancPlugins banc, CancellationToken arret)
    {
        var client = new CoreClient(new CoreClientOptions { PluginId = "openhasp", PluginVersion = "0.1.0", ReconnectDelays = [TimeSpan.FromMilliseconds(50)] },
            new Platine(), TimeProvider.System, NullLogger<CoreClient>.Instance, async ct =>
            {
                var ecoute = new TcpListener(IPAddress.Loopback, 0);
                ecoute.Start();
                var tcp = new TcpClient();
                var connexion = tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)ecoute.LocalEndpoint).Port, ct);
                var serveur = await ecoute.AcceptTcpClientAsync(ct);
                await connexion;
                ecoute.Stop();
                var coteCoeur = WebSocket.CreateFromStream(serveur.GetStream(), new WebSocketCreationOptions { IsServer = true });
                _ = Task.Run(() => banc.Hub.RunAsync(new WebSocketChannel(coteCoeur), arret), CancellationToken.None);
                return WebSocket.CreateFromStream(tcp.GetStream(), new WebSocketCreationOptions { IsServer = false });
            });
        return (client, Task.CompletedTask);
    }

    [Fact]
    public async Task La_platine_suit_l_interrupteur_et_le_commande_par_le_coeur()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (zigbee, _) = await banc.ConnecterAvecPriseAsync();
        banc.Droits[("openhasp", BancPlugins.Interrupteur)] = EntityAccess.Command;
        using var arret = new CancellationTokenSource();
        var (client, _) = await ConnecterAsync(banc, arret.Token);

        var recus = new ConcurrentQueue<EntityStateUpdate>();
        client.EntityStateReceived += recus.Enqueue;
        // Avant la connexion : l'abonnement part de lui-même dès la session ouverte.
        await client.SetSubscriptionsAsync([BancPlugins.Interrupteur]);
        var session = client.RunAsync(arret.Token);

        await BancPlugins.AttendreAsync(() => recus.Any(e => e.Value?.GetBoolean() == true), "état initial de l'interrupteur");

        var ordre = client.CommandEntityAsync(BancPlugins.Interrupteur, StandardCommands.TurnOff);
        var recu = await zigbee.RecevoirAsync<CommandRequest>();
        await zigbee.EnvoyerAsync(new CommandResult { Id = recu.Id, Outcome = CommandOutcome.Accepted });
        await zigbee.EnvoyerAsync(BancPlugins.Mesures(("0x01:switch", false)));

        var reponse = await ordre.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CommandOutcome.Accepted, reponse.Outcome);
        Assert.Equal("confirmed", reponse.Verification);
        await BancPlugins.AttendreAsync(() => recus.Any(e => e.Value?.GetBoolean() == false), "état poussé après l'ordre");

        await arret.CancelAsync();
        await session.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hors_session_un_ordre_est_injoignable_jamais_un_faux_succes()
    {
        var client = new CoreClient(new CoreClientOptions { PluginId = "openhasp" }, new Platine(), TimeProvider.System, NullLogger<CoreClient>.Instance);

        var reponse = await client.CommandEntityAsync("switch.lampe", StandardCommands.TurnOn);

        Assert.Equal(CommandOutcome.Unreachable, reponse.Outcome);
    }
}
