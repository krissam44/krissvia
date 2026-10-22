using Krissvia.Core.Events;
using Krissvia.Core.States;

namespace Krissvia.Core.History;

/// <summary>Port de persistance de l'historique, implémenté par Krissvia.Persistence.</summary>
public interface IHistoryRepository
{
    Task AppendAsync(IReadOnlyList<HistoryPoint> points, CancellationToken ct = default);

    /// <summary>Les points de [from ; to], précédés du dernier point avant <paramref name="from"/> : la valeur en vigueur au début.</summary>
    Task<IReadOnlyList<HistoryPoint>> ReadAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<IReadOnlyList<HistoryBucket>> ReadHourlyAsync(string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Les séries enregistrées pour une entité (elle-même et ses champs).</summary>
    Task<IReadOnlyList<string>> SeriesAsync(string entityId, CancellationToken ct = default);

    /// <summary>Recalcule les heures de [from ; to[ (<see cref="HourlyRollup"/>), l'heure entamée comprise jusqu'à <paramref name="to"/>.</summary>
    Task RollupAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>La dernière heure consolidée, null si rien ne l'est encore.</summary>
    Task<DateTimeOffset?> LastRolledHourAsync(CancellationToken ct = default);

    /// <summary>Supprime les points bruts antérieurs à <paramref name="before"/>. Rend le nombre de points supprimés.</summary>
    Task<int> PurgeAsync(DateTimeOffset before, CancellationToken ct = default);
}

/// <summary>
/// L'enregistreur : suit les changements d'état sur le bus, garde ce que <see cref="HistorySampler"/> retient, et
/// l'écrit par lots (<see cref="FlushAsync"/>, appelé par la boucle du serveur), jamais une écriture par mesure.
/// </summary>
public sealed class HistoryRecorder(IHistoryRepository repository, TimeProvider clock)
{
    private readonly HistorySampler _echantillons = new();

    public IDisposable Attach(IEventBus bus) =>
        bus.Subscribe<EntityStateChanged>("historique", (e, _) =>
        {
            Observe(e.EntityId, e.Current);
            return Task.CompletedTask;
        });

    public void Observe(string entityId, EntityStateSnapshot etat)
    {
        // Injoignable : un trou dans chacune de ses courbes, plutôt qu'une dernière valeur qui s'étirerait jusqu'au retour.
        if (!etat.Available)
        {
            foreach (var serie in _echantillons.SeriesOf(entityId)) _echantillons.Offer(serie, clock.GetUtcNow(), null);
            return;
        }

        foreach (var (serie, valeur) in HistoryValues.Extraire(entityId, etat.Value))
            _echantillons.Offer(serie, etat.MeasuredAt, valeur);
    }

    /// <summary>Écrit ce qui est dû ; rend le nombre de points. Un échec remet les points en attente.</summary>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        var points = _echantillons.TakeDue(clock.GetUtcNow());
        if (points.Count == 0) return 0;
        try
        {
            await repository.AppendAsync(points, ct);
            return points.Count;
        }
        catch
        {
            _echantillons.Requeue(points);
            throw;
        }
    }
}
