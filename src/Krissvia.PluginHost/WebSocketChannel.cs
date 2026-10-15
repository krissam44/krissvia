using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Krissvia.PluginHost;

/// <summary>
/// Canal WebSocket : un message JSON par trame texte. Une trame binaire ou trop grande ferme la connexion — un
/// plugin qui envoie cela ne parle pas le contrat, mieux vaut le savoir tout de suite.
/// </summary>
public sealed class WebSocketChannel(WebSocket socket, int maxFrameBytes = WebSocketChannel.DefaultMaxFrameBytes) : IMessageChannel
{
    /// <summary>1 Mio : un <c>declare</c> de deux cents appareils tient en quelques dizaines de kilo-octets.</summary>
    public const int DefaultMaxFrameBytes = 1024 * 1024;

    // WebSocket interdit deux envois simultanés : commandes et battements partent de fils différents.
    private readonly SemaphoreSlim _envoi = new(1, 1);

    public async ValueTask<string?> ReceiveAsync(CancellationToken ct)
    {
        var tampon = new ArrayBufferWriter<byte>(4096);
        try
        {
            while (true)
            {
                var resultat = await socket.ReceiveAsync(tampon.GetMemory(4096), ct);

                if (resultat.MessageType == WebSocketMessageType.Close) return null;
                if (resultat.MessageType == WebSocketMessageType.Binary)
                {
                    await FermerAsync(WebSocketCloseStatus.InvalidMessageType, "trames texte uniquement", ct);
                    return null;
                }

                tampon.Advance(resultat.Count);
                if (tampon.WrittenCount > maxFrameBytes)
                {
                    await FermerAsync(WebSocketCloseStatus.MessageTooBig, $"trame de plus de {maxFrameBytes} octets", ct);
                    return null;
                }

                if (resultat.EndOfMessage) return Encoding.UTF8.GetString(tampon.WrittenSpan);
            }
        }
        catch (WebSocketException)
        {
            return null;   // connexion coupée sans fermeture propre : c'est une fin de canal comme une autre
        }
    }

    public async ValueTask SendAsync(string frame, CancellationToken ct)
    {
        var octets = Encoding.UTF8.GetBytes(frame);
        await _envoi.WaitAsync(ct);
        try
        {
            await socket.SendAsync(octets, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _envoi.Release();
        }
    }

    public async ValueTask CloseAsync(string reason, CancellationToken ct) =>
        await FermerAsync(WebSocketCloseStatus.NormalClosure, reason, ct);

    private async Task FermerAsync(WebSocketCloseStatus statut, string motif, CancellationToken ct)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            await socket.CloseOutputAsync(statut, Tronquer(motif), ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Fermeture au mieux : la connexion est peut-être déjà morte.
        }
    }

    /// <summary>Le motif de fermeture est limité à 123 octets par le protocole.</summary>
    private static string Tronquer(string motif)
    {
        if (Encoding.UTF8.GetByteCount(motif) <= 123) return motif;

        var court = motif;
        while (Encoding.UTF8.GetByteCount(court) > 120) court = court[..^1];
        return court + "...";
    }
}
