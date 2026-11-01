using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Core.Events;

namespace Krissvia.Core.States;

/// <summary>État d'une entité tel que persisté : la valeur brute et l'instant de sa mesure.</summary>
public sealed record PersistedEntityState(string EntityId, string ValueJson, DateTimeOffset MeasuredAt, DateTimeOffset? ChangedAt = null);

/// <summary>
/// Port de persistance du magasin d'états, implémenté par Krissvia.Persistence. La disponibilité n'est pas
/// persistée : après un redémarrage, elle se reconstruit quand les plugins se reconnectent.
/// </summary>
public interface IEntityStateRepository
{
    Task<IReadOnlyList<PersistedEntityState>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insère ou remplace l'état de chaque entité, en une seule transaction.</summary>
    Task SaveAsync(IReadOnlyList<PersistedEntityState> states, CancellationToken ct = default);
}

/// <summary>Écriture de l'état des entités : ce que fait le PluginHost à la réception d'un message.</summary>
public interface IEntityStateWriter
{
    /// <summary>Nouvelle valeur. L'entité devient disponible. <paramref name="measuredAt"/> absent : heure de réception.</summary>
    void Update(string entityId, JsonElement value, DateTimeOffset? measuredAt = null);

    /// <summary>
    /// Joignabilité d'une entité. La dernière valeur et son instant sont conservés (affichage « dernière valeur
    /// connue »), mais l'état n'est plus exploitable par les moteurs. Sans effet sur une entité jamais vue.
    /// </summary>
    void SetAvailability(string entityId, bool available);
}

/// <summary>
/// Magasin d'états : dernière valeur et instant de mesure réel de chaque entité.
/// </summary>
/// <remarks>
/// Règles :
/// <list type="bullet">
/// <item>structures concurrentes : écrit par la réception des plugins, lu en même temps par les moteurs, le
/// frontend et l'API (classe de bug du 02/10/2026 sur DomoMud) ;</item>
/// <item>l'ordre d'arrivée fait foi : une mise à jour est toujours acceptée, même avec un instant plus ancien ;</item>
/// <item>un instant de mesure dans le futur au-delà de <see cref="FutureTolerance"/> (horloge de plugin
/// déréglée) est ramené à l'heure de réception : un capteur mort ne doit pas paraître frais jusqu'à sa date ;</item>
/// <item>la valeur est clonée : elle survit à la libération du document JSON d'origine ;</item>
/// <item>écriture différée : les valeurs modifiées sont persistées par <see cref="FlushAsync"/>, appelé
/// périodiquement et à l'arrêt, jamais à chaque mesure (Linky : toutes les 2 s) ;</item>
/// <item>après un redémarrage, les états restaurés gardent leur instant d'origine et sont marqués indisponibles
/// jusqu'à ce que le plugin les republie.</item>
/// </list>
/// </remarks>
/// <remarks>
/// Chaque mise à jour de valeur et chaque changement de disponibilité publie un <see cref="EntityStateChanged"/>
/// sur le bus, avec l'état précédent.
/// </remarks>
public sealed class EntityStateStore(
    IEntityStateRepository repository,
    TimeProvider clock,
    IEventPublisher? events = null) : IEntityStateReader, IEntityStateWriter
{
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, EntityStateSnapshot> _etats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _aEcrire = new(StringComparer.Ordinal);

    public EntityStateSnapshot? Get(string entityId) =>
        _etats.TryGetValue(entityId, out var etat) ? etat : null;

    /// <summary>Copie des états au moment de l'appel.</summary>
    public IReadOnlyDictionary<string, EntityStateSnapshot> GetAll() =>
        _etats.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    public void Update(string entityId, JsonElement value, DateTimeOffset? measuredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("La valeur d'un état ne peut pas être indéfinie.", nameof(value));

        var maintenant = clock.GetUtcNow();
        var instant = measuredAt is { } m && m <= maintenant + FutureTolerance ? m : maintenant;

        var etat = new EntityStateSnapshot(value.Clone(), instant.ToUniversalTime(), Available: true, ChangedAt: instant.ToUniversalTime());
        EntityStateSnapshot? precedent = null;
        // La fabrique peut tourner plusieurs fois en cas de concurrence : la dernière capture est la bonne.
        // La même valeur republiée garde l'instant de son changement : c'est lui que « depuis 2 h » raconte.
        etat = _etats.AddOrUpdate(entityId, _ => { precedent = null; return etat; }, (_, ancien) =>
        {
            precedent = ancien;
            return JsonElement.DeepEquals(ancien.Value, etat.Value) ? etat with { ChangedAt = ancien.ChangedSince } : etat;
        });
        _aEcrire[entityId] = 0;

        events?.Publish(new EntityStateChanged(entityId, precedent, etat));
    }

    public void SetAvailability(string entityId, bool available)
    {
        while (_etats.TryGetValue(entityId, out var actuel))
        {
            if (actuel.Available == available) return;

            var nouveau = actuel with { Available = available };
            if (!_etats.TryUpdate(entityId, nouveau, actuel)) continue;   // modifié entre-temps : on relit

            events?.Publish(new EntityStateChanged(entityId, actuel, nouveau));
            return;
        }
    }

    // ── Persistance ──────────────────────────────────────────────────────────

    /// <summary>
    /// Restaure les états persistés, avec leur instant d'origine, indisponibles jusqu'à republication. Ne remplace
    /// pas un état déjà publié, ne lève aucun événement, ne marque rien à réécrire. Une ligne illisible est ignorée.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var persiste in await repository.LoadAllAsync(ct))
        {
            if (!TryParse(persiste.ValueJson, out var valeur)) continue;

            _etats.TryAdd(persiste.EntityId, new EntityStateSnapshot(valeur, persiste.MeasuredAt, Available: false, persiste.ChangedAt));
        }
    }

    /// <summary>Persiste les états modifiés depuis le dernier appel. Renvoie leur nombre.</summary>
    /// <remarks>En cas d'échec (ou d'annulation), ils restent à réécrire et l'exception remonte à l'appelant.</remarks>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        var lot = new List<PersistedEntityState>();
        var retires = new List<string>();

        foreach (var cle in _aEcrire.Keys.ToList())
        {
            if (!_aEcrire.TryRemove(cle, out _)) continue;
            if (!_etats.TryGetValue(cle, out var etat)) continue;

            lot.Add(new PersistedEntityState(cle, etat.Value.GetRawText(), etat.MeasuredAt, etat.ChangedAt));
            retires.Add(cle);
        }

        if (lot.Count == 0) return 0;

        try
        {
            await repository.SaveAsync(lot, ct);
        }
        catch
        {
            foreach (var cle in retires) _aEcrire[cle] = 0;
            throw;
        }

        return lot.Count;
    }

    // ── Interne ──────────────────────────────────────────────────────────────

    private static bool TryParse(string json, out JsonElement valeur)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            valeur = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            valeur = default;
            return false;
        }
    }
}
