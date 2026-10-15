namespace Krissvia.Core.Registry;

/// <summary>Port de persistance du registre, implémenté par Krissvia.Persistence.</summary>
public interface IEntityRepository
{
    Task<IReadOnlyList<RegisteredDevice>> ListDevicesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RegisteredEntity>> ListEntitiesAsync(CancellationToken ct = default);

    /// <summary>Enregistre appareils puis entités en une seule transaction : tout ou rien.</summary>
    Task SaveAsync(IReadOnlyList<RegisteredDevice> devices, IReadOnlyList<RegisteredEntity> entities, CancellationToken ct = default);

    Task AddAliasAsync(string alias, string entityId, CancellationToken ct = default);

    Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default);
}
