using System.Threading.Channels;

namespace Krissvia.Core.Events;

/// <summary>Publication d'événements : ce dont ont besoin le magasin d'états, le registre, le PluginHub.</summary>
public interface IEventPublisher
{
    /// <summary>Ne bloque jamais, ne lève jamais d'exception, n'appelle aucun abonné sur le fil de l'appelant.</summary>
    void Publish(CoreEvent e);
}

public interface IEventBus : IEventPublisher
{
    /// <summary>
    /// Abonne <paramref name="handler"/> aux événements de type <typeparamref name="T"/> (ou dérivés). Les
    /// événements lui sont remis dans l'ordre de publication, un à la fois, sur sa propre boucle. Le nom sert au
    /// journal et au diagnostic. Libérer le résultat désabonne.
    /// </summary>
    IDisposable Subscribe<T>(string subscriber, Func<T, CancellationToken, Task> handler) where T : CoreEvent;

    /// <summary>Événements en attente par abonné : un nombre qui grandit désigne un abonné trop lent.</summary>
    IReadOnlyList<(string Subscriber, int Pending)> Diagnostics();
}

/// <summary>
/// Bus d'événements en mémoire. Remplace les événements C# de chaque service de DomoMud, et leurs deux pièges :
/// <list type="bullet">
/// <item>un abonné appelé sur le fil du publieur, qui ouvrait la base pendant que le publieur tenait le bail
/// SQLite : le circuit Blazor gelait (HaDiscoveryService). Ici, Publish ne fait qu'écrire dans une file ;</item>
/// <item>un abonné qui lève une exception et prive les suivants de l'événement. Ici, chaque abonné a sa file et
/// sa boucle ; une exception est signalée à <paramref name="onHandlerError"/> et la boucle continue.</item>
/// </list>
/// Les files sont non bornées : perdre un changement d'état casserait un déclencheur sur front, et le volume
/// (quelques messages par seconde) ne justifie pas de jeter quoi que ce soit. <see cref="Diagnostics"/> montre
/// un abonné qui prend du retard.
/// </summary>
public sealed class EventBus(Action<string, Exception>? onHandlerError = null) : IEventBus, IAsyncDisposable
{
    private readonly Lock _verrou = new();
    private Abonnement[] _abonnes = [];

    public void Publish(CoreEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (var abonne in Volatile.Read(ref _abonnes))
            if (abonne.Accepte(e)) abonne.File.Writer.TryWrite(e);
    }

    public IDisposable Subscribe<T>(string subscriber, Func<T, CancellationToken, Task> handler) where T : CoreEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        ArgumentNullException.ThrowIfNull(handler);

        var abonne = new Abonnement(this, subscriber, typeof(T), (e, ct) => handler((T)e, ct));
        lock (_verrou) _abonnes = [.. _abonnes, abonne];
        abonne.Demarrer();
        return abonne;
    }

    public IReadOnlyList<(string Subscriber, int Pending)> Diagnostics() =>
        Volatile.Read(ref _abonnes).Select(a => (a.Nom, a.File.Reader.Count)).ToList();

    public async ValueTask DisposeAsync()
    {
        Abonnement[] tous;
        lock (_verrou)
        {
            tous = _abonnes;
            _abonnes = [];
        }

        foreach (var abonne in tous) abonne.Arreter();
        await Task.WhenAll(tous.Select(a => a.Boucle));
    }

    private void Retirer(Abonnement abonne)
    {
        lock (_verrou) _abonnes = _abonnes.Where(a => a != abonne).ToArray();
    }

    private sealed class Abonnement(EventBus bus, string nom, Type type, Func<CoreEvent, CancellationToken, Task> handler) : IDisposable
    {
        private readonly CancellationTokenSource _arret = new();

        public string Nom => nom;
        // Pas de SingleReader : cette variante ne sait pas compter ses éléments, et Diagnostics en a besoin.
        public Channel<CoreEvent> File { get; } = Channel.CreateUnbounded<CoreEvent>();
        public Task Boucle { get; private set; } = Task.CompletedTask;

        public bool Accepte(CoreEvent e) => type.IsInstanceOfType(e);

        public void Demarrer() => Boucle = Task.Run(LireAsync);

        private async Task LireAsync()
        {
            try
            {
                await foreach (var e in File.Reader.ReadAllAsync(_arret.Token))
                {
                    try
                    {
                        await handler(e, _arret.Token);
                    }
                    catch (OperationCanceledException) when (_arret.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        bus.Signaler(nom, ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Arreter()
        {
            File.Writer.TryComplete();
            _arret.Cancel();
        }

        public void Dispose()
        {
            bus.Retirer(this);
            Arreter();
        }
    }

    private void Signaler(string abonne, Exception ex)
    {
        try
        {
            onHandlerError?.Invoke(abonne, ex);
        }
        catch
        {
            // Le journal d'erreurs ne doit pas tuer la boucle de l'abonné.
        }
    }
}
