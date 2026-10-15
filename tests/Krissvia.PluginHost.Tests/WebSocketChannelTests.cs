using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Krissvia.PluginHost.Tests;

/// <summary>Le canal WebSocket sur une vraie connexion TCP locale : découpage des trames, refus, fermetures.</summary>
public sealed class WebSocketChannelTests : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private TcpClient? _serveur;

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _serveur?.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>Deux WebSocket reliées par TCP : l'une côté cœur (serveur), l'autre côté plugin.</summary>
    private async Task<(WebSocket Coeur, WebSocket Plugin)> RelierAsync()
    {
        var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        try
        {
            var connexion = _client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)ecoute.LocalEndpoint).Port);
            _serveur = await ecoute.AcceptTcpClientAsync();
            await connexion;
        }
        finally
        {
            ecoute.Stop();
        }

        return (
            WebSocket.CreateFromStream(_serveur.GetStream(), new WebSocketCreationOptions { IsServer = true }),
            WebSocket.CreateFromStream(_client.GetStream(), new WebSocketCreationOptions { IsServer = false }));
    }

    private static CancellationToken Delai() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    [Fact]
    public async Task Une_trame_fait_l_aller_retour_accents_compris()
    {
        var (coeur, plugin) = await RelierAsync();
        var canalCoeur = new WebSocketChannel(coeur);
        var canalPlugin = new WebSocketChannel(plugin);

        await canalPlugin.SendAsync("""{"type":"shutdown","reason":"mise à jour"}""", Delai());
        Assert.Equal("""{"type":"shutdown","reason":"mise à jour"}""", await canalCoeur.ReceiveAsync(Delai()));

        await canalCoeur.SendAsync("""{"type":"ping"}""", Delai());
        Assert.Equal("""{"type":"ping"}""", await canalPlugin.ReceiveAsync(Delai()));
    }

    [Fact]
    public async Task Une_trame_envoyee_en_plusieurs_fragments_est_reassemblee()
    {
        var (coeur, plugin) = await RelierAsync();
        var canal = new WebSocketChannel(coeur);
        var texte = "{\"x\":\"" + new string('é', 10_000) + "\"}";
        var octets = Encoding.UTF8.GetBytes(texte);

        // Coupé au milieu d'un caractère accentué : le décodage ne doit se faire qu'une fois la trame complète.
        await plugin.SendAsync(octets.AsMemory(0, 7), WebSocketMessageType.Text, endOfMessage: false, Delai());
        await plugin.SendAsync(octets.AsMemory(7), WebSocketMessageType.Text, endOfMessage: true, Delai());

        Assert.Equal(texte, await canal.ReceiveAsync(Delai()));
    }

    [Fact]
    public async Task Une_trame_binaire_ferme_le_canal()
    {
        var (coeur, plugin) = await RelierAsync();
        var canal = new WebSocketChannel(coeur);

        await plugin.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, Delai());

        Assert.Null(await canal.ReceiveAsync(Delai()));
        var fermeture = await plugin.ReceiveAsync(new byte[256], Delai());
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, fermeture.CloseStatus);
    }

    [Fact]
    public async Task Une_trame_trop_grande_ferme_le_canal()
    {
        var (coeur, plugin) = await RelierAsync();
        var canal = new WebSocketChannel(coeur, maxFrameBytes: 1000);

        await plugin.SendAsync(new byte[5000], WebSocketMessageType.Text, endOfMessage: true, Delai());

        Assert.Null(await canal.ReceiveAsync(Delai()));
        var fermeture = await plugin.ReceiveAsync(new byte[256], Delai());
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, fermeture.CloseStatus);
    }

    [Fact]
    public async Task Une_fermeture_par_l_autre_cote_termine_la_reception()
    {
        var (coeur, plugin) = await RelierAsync();
        var canal = new WebSocketChannel(coeur);

        await plugin.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "au revoir", Delai());

        Assert.Null(await canal.ReceiveAsync(Delai()));
    }

    [Fact]
    public async Task Une_coupure_brutale_termine_la_reception()
    {
        var (coeur, _) = await RelierAsync();
        var canal = new WebSocketChannel(coeur);

        _client.Dispose();

        Assert.Null(await canal.ReceiveAsync(Delai()));
    }

    [Fact]
    public async Task Fermer_deux_fois_ne_leve_pas_d_exception()
    {
        var (coeur, _) = await RelierAsync();
        var canal = new WebSocketChannel(coeur);

        await canal.CloseAsync("fin", Delai());
        await canal.CloseAsync("fin", Delai());
    }
}
