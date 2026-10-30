using System.Net.WebSockets;
using System.Text;

namespace Krissvia.Mobile.Client;

/// <summary>
/// Tient <see cref="ThermostatsEnDirect"/> à jour : la liste par <c>/api/entities</c>, puis le flux <c>/api/live</c>
/// restreint aux thermostats. Se reconnecte seul, de plus en plus lentement (1 s à 30 s).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La liste est relue à chaque connexion : le flux restreint ne ferait jamais connaître un thermostat créé
/// depuis.</item>
/// <item><see cref="Reveiller"/> reprend tout de suite, depuis la liste : au retour au premier plan, Android a souvent
/// coupé la connexion sans que rien ne l'ait dit.</item>
/// <item>Aucun thermostat : pas de flux (sans filtre, le serveur enverrait toutes les entités), une relecture par
/// minute.</item>
/// </list>
/// </remarks>
public sealed class FluxEnDirect(KrissviaApi api, IConnexion connexion, ThermostatsEnDirect thermostats) : IAsyncDisposable
{
    private static readonly TimeSpan[] Delais =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private static readonly TimeSpan SansThermostat = TimeSpan.FromMinutes(1);

    private readonly Lock _verrou = new();
    private CancellationTokenSource? _arret;
    private CancellationTokenSource? _session;
    private Task? _boucle;
    private int _tentative;

    /// <summary>Raccourcit les attentes dans les tests.</summary>
    internal Func<int, TimeSpan> Delai { get; init; } = tentative => Delais[Math.Min(tentative, Delais.Length - 1)];

    /// <summary>Vrai tant que le flux est ouvert : les valeurs affichées sont à jour.</summary>
    public bool Connecte { get; private set; }

    /// <summary>Pourquoi le flux est fermé, null s'il est ouvert ou pas encore tenté.</summary>
    public string? Erreur { get; private set; }

    /// <summary>Levé quand <see cref="Connecte"/> ou <see cref="Erreur"/> change.</summary>
    public event Action? EtatChange;

    public void Demarrer()
    {
        lock (_verrou)
        {
            if (_boucle is not null) return;
            _arret = new CancellationTokenSource();
            _boucle = BoucleAsync(_arret.Token);
        }
    }

    /// <summary>Abandonne la connexion ou l'attente en cours, et repart tout de suite.</summary>
    public void Reveiller()
    {
        lock (_verrou) _session?.Cancel();
    }

    public async Task ArreterAsync()
    {
        Task? boucle;
        lock (_verrou)
        {
            boucle = _boucle;
            _arret?.Cancel();
            _boucle = null;
        }
        if (boucle is not null) await boucle;
        lock (_verrou)
        {
            _arret?.Dispose();
            _arret = null;
        }
    }

    public async ValueTask DisposeAsync() => await ArreterAsync();

    private async Task BoucleAsync(CancellationToken arret)
    {
        while (!arret.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(arret);
            lock (_verrou) _session = session;
            var ct = session.Token;

            TimeSpan attente;
            try
            {
                attente = await SessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (arret.IsCancellationRequested) break;
                _tentative = 0;
                continue;   // Réveillé : on repart sans attendre.
            }
            catch (Exception ex)
            {
                Signaler(false, KrissviaApi.MessageLisible(ex));
                attente = Delai(_tentative++);
            }

            try
            {
                await Task.Delay(attente, ct);
            }
            catch (OperationCanceledException)
            {
                // Réveillé ou arrêté : la boucle décide.
            }
        }

        Signaler(false, Erreur);
    }

    /// <summary>Une connexion complète. Rend l'attente avant la suivante ; une erreur lève.</summary>
    private async Task<TimeSpan> SessionAsync(CancellationToken ct)
    {
        var (entites, erreur) = await api.EntitesAsync(ct);
        if (erreur is not null) throw new FluxException(erreur);
        thermostats.Remplacer(entites!.Value);

        if (thermostats.EntityIds.Count == 0)
        {
            Signaler(false, null);
            return SansThermostat;
        }

        using var socket = new ClientWebSocket();
        if (connexion.ApiKey.Length > 0) socket.Options.SetRequestHeader(KrissviaApi.EnTeteCle, connexion.ApiKey);
        // Un téléphone qui change de réseau laisse la connexion morte sans fermeture : le ping finit par le voir.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(AdresseServeur.Flux(connexion.BaseUrl, thermostats.EntityIds), ct);
        _tentative = 0;
        Signaler(true, null);

        await LireAsync(socket, ct);
        // Fermé par le serveur (arrêt, redémarrage) : on se reconnecte vite, comme le tableau de bord.
        throw new FluxException("Connexion fermée par le serveur.");
    }

    private async Task LireAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var tampon = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var recu = await socket.ReceiveAsync(tampon, ct);
            if (recu.MessageType == WebSocketMessageType.Close) return;

            message.Write(tampon, 0, recu.Count);
            if (!recu.EndOfMessage) continue;

            thermostats.Appliquer(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void Signaler(bool connecte, string? erreur)
    {
        if (Connecte == connecte && Erreur == erreur) return;
        Connecte = connecte;
        Erreur = erreur;
        EtatChange?.Invoke();
    }

    private sealed class FluxException(string message) : Exception(message);
}
