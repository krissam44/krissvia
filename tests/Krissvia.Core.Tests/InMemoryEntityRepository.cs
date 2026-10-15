using Krissvia.Core.Registry;

namespace Krissvia.Core.Tests;

/// <summary>Dépôt en mémoire : permet de tester la logique du registre sans base de données.</summary>
internal sealed class InMemoryEntityRepository : IEntityRepository
{
    private readonly Dictionary<string, RegisteredDevice> _devices = new();
    private readonly Dictionary<string, RegisteredEntity> _entities = new();
    private readonly Dictionary<string, string> _aliases = new();

    public Task<IReadOnlyList<RegisteredDevice>> ListDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegisteredDevice>>(_devices.Values.ToList());

    public Task<IReadOnlyList<RegisteredEntity>> ListEntitiesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegisteredEntity>>(_entities.Values.ToList());

    public Task SaveAsync(IReadOnlyList<RegisteredDevice> devices, IReadOnlyList<RegisteredEntity> entities, CancellationToken ct = default)
    {
        foreach (var d in devices) _devices[d.DeviceId] = d;
        foreach (var e in entities) _entities[e.EntityId] = e;
        return Task.CompletedTask;
    }

    public Task AddAliasAsync(string alias, string entityId, CancellationToken ct = default)
    {
        _aliases[alias] = entityId;
        return Task.CompletedTask;
    }

    public Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default) =>
        Task.FromResult<string?>(_aliases.TryGetValue(alias, out var id) ? id : null);
}
