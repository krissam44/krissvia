using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Krissvia.Sdk;

/// <summary>Une trame texte WebSocket = un message JSON du contrat.</summary>
internal static class TextFrames
{
    /// <summary>Même limite que le cœur : au-delà, il ferme la connexion.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>Trame suivante, ou null quand la connexion est fermée. Les trames binaires sont ignorées.</summary>
    public static async Task<string?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var tampon = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var resultat = await ws.ReceiveAsync(tampon.GetMemory(4096), ct);
            if (resultat.MessageType == WebSocketMessageType.Close) return null;

            tampon.Advance(resultat.Count);
            if (tampon.WrittenCount > MaxFrameBytes)
                throw new WebSocketException($"Trame du cœur de plus de {MaxFrameBytes} octets.");

            if (!resultat.EndOfMessage) continue;
            if (resultat.MessageType == WebSocketMessageType.Text) return Encoding.UTF8.GetString(tampon.WrittenSpan);

            tampon.Clear();
        }
    }

    public static async Task SendAsync(WebSocket ws, string frame, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, ct);
}
