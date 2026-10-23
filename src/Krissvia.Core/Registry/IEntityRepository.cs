namespace Krissvia.Core.Registry;

/// <summary>Port de persistance du registre, implémenté par Krissvia.Persistence.</summary>
public interface IEntityRepository
{
    Task<IReadOnlyList<RegisteredDevice>> ListDevicesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RegisteredEntity>> ListEntitiesAsync(CancellationToken ct = default);

    /// <summary>Enregistre appareils puis entités en une seule transaction : tout ou rien.</summary>
    Task SaveAsync(IReadOnlyList<RegisteredDevice> devices, IReadOnlyList<RegisteredEntity> entities, CancellationToken ct = default);

    /// <summary>
    /// Efface une entité, ses alias et son dernier état, en une transaction ; et son appareil s'il est donné (celui qui
    /// n'a plus d'autre entité).
    /// </summary>
    Task DeleteEntityAsync(string entityId, string? orphanDeviceId, CancellationToken ct = default);

    Task AddAliasAsync(string alias, string entityId, CancellationToken ct = default);

    Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default);
}
