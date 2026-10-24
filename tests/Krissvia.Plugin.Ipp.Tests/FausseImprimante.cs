using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Krissvia.Plugin.Ipp.Tests;

/// <summary>
/// Une imprimante IPP sur la boucle locale, en HTTP/1.1 brut : elle répond à <c>Get-Printer-Attributes</c> par une
/// trame de <c>Trames/</c> et refuse toute autre opération (« operation-not-supported »). Elle note chaque opération
/// reçue : le test vérifie que le plugin n'a jamais rien envoyé d'autre qu'une lecture.
/// </summary>
internal sealed class FausseImprimante : IAsyncDisposable
{
    private readonly TcpListener _ecoute = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _arret = new();
    private readonly Task _boucle;

    public ConcurrentQueue<ushort> Operations { get; } = new();

    /// <summary>La trame servie ; modifiable en cours de test (bourrage, toner bas…).</summary>
    public string Trame { get; set; } = "samsung-m2020-prete.ipp.txt";

    /// <summary>Un statut IPP d'erreur à répondre à la place de la trame (0x0406 : imprimante introuvable à ce chemin).</summary>
    public ushort? Erreur { get; init; }

    public Uri Url { get; }

    public FausseImprimante()
    {
        _ecoute.Start();
        Url = new Uri($"ipp://127.0.0.1:{((IPEndPoint)_ecoute.LocalEndpoint).Port}/ipp/print");
        _boucle = Task.Run(EcouterAsync);
    }

    private async Task EcouterAsync()
    {
        var connexions = new List<Task>();
        try
        {
            while (true)
            {
                var client = await _ecoute.AcceptTcpClientAsync(_arret.Token);
                connexions.Add(Task.Run(() => ServirAsync(client)));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(connexions);
    }

    private async Task ServirAsync(TcpClient client)
    {
        using var _ = client;
        var flux = client.GetStream();
        try
        {
            while (!_arret.IsCancellationRequested)
            {
                var corps = await LireRequeteAsync(flux);
                if (corps is null) return;
                var operation = BinaryPrimitives.ReadUInt16BigEndian(corps.AsSpan(2));
                var requete = BinaryPrimitives.ReadInt32BigEndian(corps.AsSpan(4));
                Operations.Enqueue(operation);

                byte[] reponse;
                if (operation == 0x000B && Erreur is null)
                {
                    reponse = Trames.Lire(Trame);
                    BinaryPrimitives.WriteInt32BigEndian(reponse.AsSpan(4), requete);
                }
                else
                {
                    // IPP/1.1, server-error-operation-not-supported (ou l'erreur imposée), avec le jeu de caractères.
                    reponse = Trames.Hex("01 01 05 01 00 00 00 00 01 47 00 12 61 74 74 72 69 62 75 74 65 73 2d 63 68 61 72 73 65 74 00 05 75 74 66 2d 38 03");
                    BinaryPrimitives.WriteUInt16BigEndian(reponse.AsSpan(2), Erreur ?? 0x0501);
                    BinaryPrimitives.WriteInt32BigEndian(reponse.AsSpan(4), requete);
                }

                var entete = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/ipp\r\nContent-Length: {reponse.Length}\r\n\r\n");
                await flux.WriteAsync(entete, _arret.Token);
                await flux.WriteAsync(reponse, _arret.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>En-têtes jusqu'à la ligne vide, puis <c>Content-Length</c> octets. Null : connexion fermée.</summary>
    private async Task<byte[]?> LireRequeteAsync(NetworkStream flux)
    {
        var entetes = new List<byte>();
        var octet = new byte[1];
        while (entetes.Count < 4 || !(entetes[^4] == '\r' && entetes[^3] == '\n' && entetes[^2] == '\r' && entetes[^1] == '\n'))
        {
            if (await flux.ReadAsync(octet, _arret.Token) == 0) return null;
            entetes.Add(octet[0]);
        }

        var texte = Encoding.ASCII.GetString([.. entetes]);
        var longueur = texte.Split("\r\n").Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)).Single();
        var corps = new byte[longueur];
        await flux.ReadExactlyAsync(corps, _arret.Token);
        return corps;
    }

    public async ValueTask DisposeAsync()
    {
        await _arret.CancelAsync();
        _ecoute.Stop();
        await _boucle.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
