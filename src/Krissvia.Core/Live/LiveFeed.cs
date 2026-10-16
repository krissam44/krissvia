using Krissvia.Core.Events;

namespace Krissvia.Core.Live;

/// <summary>Ce qui a changé depuis la dernière lecture d'un abonnement au flux en direct.</summary>
/// <param name="RegistryChanged">Le registre a changé (entité ajoutée, renommée, désactivée…) : relire la liste.</param>
/// <param name="ChangedEntities">Les entités dont l'état a changé, chacune une seule fois.</param>
public sealed record LiveBatch(bool RegistryChanged, IReadOnlyList<string> ChangedEntities);

/// <summary>
/// Flux en direct des entités (<c>/api/live</c>) : un seul abonnement au bus d'événements, réparti entre les
/// clients connectés (tableaux de bord, widgets, applis).
/// </summary>
/// <remarks>
/// Chaque client ne reçoit que des <em>identifiants</em> à relire, regroupés : une sonde qui publie dix fois
/// pendant qu'un téléphone est lent ne fait qu'une ligne, et le client lit toujours l'état le plus récent. Un client
/// lent ne fait donc jamais grossir la mémoire du cœur — contrairement à un abonné du bus, dont la file n'est pas
/// bornée (ce qui est voulu pour les moteurs, qui ne doivent perdre aucun front).
/// </remarks>
public sealed class LiveFeed : IDisposable
{
    private readonly Lock _verrou = new();
    private readonly IDisposable _etats;
    private readonly IDisposable _registre;
    private LiveSubscription[] _abonnes = [];

    public LiveFeed(IEventBus events)
    {
        _etats = events.Subscribe<EntityStateChanged>("flux-direct:etats", (e, _) =>
        {
            foreach (var abonne in Volatile.Read(ref _abonnes)) abonne.SignalerEtat(e.EntityId);
            return Task.CompletedTask;
        });
        _registre = events.Subscribe<RegistryChanged>("flux-direct:registre", (_, _) =>
        {
            foreach (var abonne in Volatile.Read(ref _abonnes)) abonne.SignalerRegistre();
            return Task.CompletedTask;
        });
    }

    /// <summary>Clients connectés en ce moment.</summary>
    public int Subscribers => Volatile.Read(ref _abonnes).Length;

    /// <summary>Abonne un client ; <paramref name="entities"/> restreint les changements d'état reçus (null : tous).</summary>
    public LiveSubscription Subscribe(IReadOnlyCollection<string>? entities = null)
    {
        var abonne = new LiveSubscription(this, entities is null ? null : new HashSet<string>(entities, StringComparer.Ordinal));
        lock (_verrou) _abonnes = [.. _abonnes, abonne];
        return abonne;
    }

    internal void Retirer(LiveSubscription abonne)
    {
        lock (_verrou) _abonnes = [.. _abonnes.Where(a => a != abonne)];
    }

    public void Dispose()
    {
        _etats.Dispose();
        _registre.Dispose();
    }
}

/// <summary>Abonnement d'un client au flux en direct. Libérer l'abonnement le retire.</summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly LiveFeed _flux;
    private readonly HashSet<string>? _filtre;
    private readonly Lock _verrou = new();
    private readonly List<string> _changees = [];
    private readonly HashSet<string> _dejaNotees = new(StringComparer.Ordinal);
    private bool _registre;
    private TaskCompletionSource? _attente;

    internal LiveSubscription(LiveFeed flux, HashSet<string>? filtre)
    {
        _flux = flux;
        _filtre = filtre;
    }

    internal void SignalerEtat(string entityId)
    {
        if (_filtre is not null && !_filtre.Contains(entityId)) return;

        TaskCompletionSource? reveil;
        lock (_verrou)
        {
            if (_dejaNotees.Add(entityId)) _changees.Add(entityId);
            reveil = Reveiller();
        }

        reveil?.TrySetResult();
    }

    internal void SignalerRegistre()
    {
        TaskCompletionSource? reveil;
        lock (_verrou)
        {
            _registre = true;
            reveil = Reveiller();
        }

        reveil?.TrySetResult();
    }

    /// <summary>Attend qu'il y ait du nouveau, puis rend tout ce qui s'est accumulé depuis la lecture précédente.</summary>
    public async Task<LiveBatch> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task attente;
            lock (_verrou)
            {
                if (_registre || _changees.Count > 0)
                {
                    var lot = new LiveBatch(_registre, [.. _changees]);
                    _registre = false;
                    _changees.Clear();
                    _dejaNotees.Clear();
                    return lot;
                }

                _attente = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                attente = _attente.Task;
            }

            await attente.WaitAsync(ct);
        }
    }

    public void Dispose() => _flux.Retirer(this);

    // Sous le verrou : le réveil se fait après l'avoir rendu, pour ne jamais exécuter le lecteur en le tenant.
    private TaskCompletionSource? Reveiller()
    {
        var attente = _attente;
        _attente = null;
        return attente;
    }
}
