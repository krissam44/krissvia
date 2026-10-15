using Krissvia.Contracts;

namespace Krissvia.Core.Registry;

public interface IEntityRegistry
{
    /// <summary>
    /// Applique l'instantané COMPLET d'un plugin (message « declare »). Idempotent : les champs de
    /// l'utilisateur sont conservés, les entités absentes sont marquées « manquantes », jamais supprimées.
    /// </summary>
    Task<RegistrationResult> RegisterAsync(string integrationId, IReadOnlyList<DeviceDescriptor> devices, CancellationToken ct = default);

    Task<RegisteredEntity?> GetAsync(string entityId, CancellationToken ct = default);

    Task<RegisteredEntity?> FindByUniqueIdAsync(string integrationId, string uniqueId, CancellationToken ct = default);

    Task<IReadOnlyList<RegisteredEntity>> ListAsync(CancellationToken ct = default);

    Task RenameAsync(string entityId, string displayName, CancellationToken ct = default);

    Task SetUsageAsync(string entityId, string? usage, CancellationToken ct = default);

    Task SetEnabledAsync(string entityId, bool enabled, CancellationToken ct = default);

    /// <summary>Associe un ancien identifiant (ex. un topic DomoMud) à une entité. Refuse un alias déjà pris par une autre entité.</summary>
    Task AddAliasAsync(string alias, string entityId, CancellationToken ct = default);

    Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default);
}
