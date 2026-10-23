using System.Text.Json;
using Dapper;
using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Persistence.Sqlite;

public sealed class SqliteEntityRepository(SqliteDatabase db) : IEntityRepository
{
    private const string DeviceColumns =
        "device_id AS DeviceId, integration_id AS IntegrationId, unique_id AS UniqueId, display_name AS DisplayName, " +
        "manufacturer AS Manufacturer, model AS Model, room AS Room, extra_json AS ExtraJson";

    private const string EntityColumns =
        "entity_id AS EntityId, device_id AS DeviceId, integration_id AS IntegrationId, unique_id AS UniqueId, " +
        "kind AS Kind, display_name AS DisplayName, device_class AS DeviceClass, unit AS Unit, state_class AS StateClass, " +
        "commands_json AS CommandsJson, extra_json AS ExtraJson, usage AS Usage, enabled AS Enabled, missing AS Missing, " +
        "nominal_power AS NominalPower, widget AS Widget";

    private const string UpsertDevice = """
        INSERT INTO devices (device_id, integration_id, unique_id, display_name, manufacturer, model, room, extra_json)
        VALUES (@DeviceId, @IntegrationId, @UniqueId, @DisplayName, @Manufacturer, @Model, @Room, @ExtraJson)
        ON CONFLICT(device_id) DO UPDATE SET
            integration_id = excluded.integration_id, unique_id = excluded.unique_id,
            display_name = excluded.display_name, manufacturer = excluded.manufacturer,
            model = excluded.model, room = excluded.room, extra_json = excluded.extra_json;
        """;

    private const string UpsertEntity = """
        INSERT INTO entities (entity_id, device_id, integration_id, unique_id, kind, display_name, device_class, unit,
                              state_class, commands_json, extra_json, usage, enabled, missing, nominal_power, widget)
        VALUES (@EntityId, @DeviceId, @IntegrationId, @UniqueId, @Kind, @DisplayName, @DeviceClass, @Unit,
                @StateClass, @CommandsJson, @ExtraJson, @Usage, @Enabled, @Missing, @NominalPower, @Widget)
        ON CONFLICT(entity_id) DO UPDATE SET
            device_id = excluded.device_id, integration_id = excluded.integration_id, unique_id = excluded.unique_id,
            kind = excluded.kind, display_name = excluded.display_name, device_class = excluded.device_class,
            unit = excluded.unit, state_class = excluded.state_class, commands_json = excluded.commands_json,
            extra_json = excluded.extra_json, usage = excluded.usage, enabled = excluded.enabled,
            nominal_power = excluded.nominal_power, widget = excluded.widget,
            missing = excluded.missing;
        """;

    public async Task<IReadOnlyList<RegisteredDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        var lignes = await connexion.QueryAsync<DeviceRow>(
            new CommandDefinition($"SELECT {DeviceColumns} FROM devices ORDER BY device_id", cancellationToken: ct));
        return lignes.Select(AppareilDepuis).ToList();
    }

    public async Task<IReadOnlyList<RegisteredEntity>> ListEntitiesAsync(CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        var lignes = await connexion.QueryAsync<EntityRow>(
            new CommandDefinition($"SELECT {EntityColumns} FROM entities ORDER BY entity_id", cancellationToken: ct));
        return lignes.Select(EntiteDepuis).ToList();
    }

    public async Task SaveAsync(IReadOnlyList<RegisteredDevice> devices, IReadOnlyList<RegisteredEntity> entities, CancellationToken ct = default)
    {
        if (devices.Count == 0 && entities.Count == 0) return;

        await using var connexion = await db.OpenAsync(ct);
        await using var transaction = await connexion.BeginTransactionAsync(ct);

        foreach (var d in devices)
        {
            await connexion.ExecuteAsync(new CommandDefinition(UpsertDevice, new
            {
                d.DeviceId, d.IntegrationId, d.UniqueId, d.DisplayName, d.Manufacturer, d.Model, d.Room, d.ExtraJson,
            }, transaction, cancellationToken: ct));
        }

        foreach (var e in entities)
        {
            await connexion.ExecuteAsync(new CommandDefinition(UpsertEntity, new
            {
                e.EntityId, e.DeviceId, e.IntegrationId, e.UniqueId,
                Kind = e.Kind.ToString(),
                e.DisplayName, e.DeviceClass, e.Unit,
                StateClass = e.StateClass?.ToString(),
                CommandsJson = JsonSerializer.Serialize(e.Commands),
                e.ExtraJson, e.Usage,
                Enabled = e.Enabled ? 1 : 0,
                Missing = e.Missing ? 1 : 0,
                e.NominalPower,
                e.Widget,
            }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    public async Task DeleteEntityAsync(string entityId, string? orphanDeviceId, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        await using var transaction = await connexion.BeginTransactionAsync(ct);
        // Les alias d'abord : ils référencent l'entité (clé étrangère).
        foreach (var sql in new[]
                 {
                     "DELETE FROM entity_aliases WHERE entity_id = @entityId;",
                     "DELETE FROM entity_states WHERE entity_id = @entityId;",
                     "DELETE FROM entities WHERE entity_id = @entityId;",
                 })
            await connexion.ExecuteAsync(new CommandDefinition(sql, new { entityId }, transaction, cancellationToken: ct));

        if (orphanDeviceId is not null)
            await connexion.ExecuteAsync(new CommandDefinition("DELETE FROM devices WHERE device_id = @orphanDeviceId;",
                new { orphanDeviceId }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
    }

    public async Task AddAliasAsync(string alias, string entityId, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        await connexion.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entity_aliases (alias, entity_id) VALUES (@alias, @entityId) " +
            "ON CONFLICT(alias) DO UPDATE SET entity_id = excluded.entity_id;",
            new { alias, entityId }, cancellationToken: ct));
    }

    public async Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenAsync(ct);
        return await connexion.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT entity_id FROM entity_aliases WHERE alias = @alias",
            new { alias }, cancellationToken: ct));
    }

    // ── Lignes SQL → modèle ─────────────────────────────────────────────────

    private sealed class DeviceRow
    {
        public string DeviceId { get; set; } = string.Empty;
        public string IntegrationId { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Room { get; set; }
        public string? ExtraJson { get; set; }
    }

    private sealed class EntityRow
    {
        public string EntityId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string IntegrationId { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? DeviceClass { get; set; }
        public string? Unit { get; set; }
        public string? StateClass { get; set; }
        public string CommandsJson { get; set; } = "[]";
        public string? ExtraJson { get; set; }
        public string? Usage { get; set; }
        public long Enabled { get; set; }
        public long Missing { get; set; }
        public double? NominalPower { get; set; }
        public string? Widget { get; set; }
    }

    private static RegisteredDevice AppareilDepuis(DeviceRow r) => new()
    {
        DeviceId = r.DeviceId,
        IntegrationId = r.IntegrationId,
        UniqueId = r.UniqueId,
        DisplayName = r.DisplayName,
        Manufacturer = r.Manufacturer,
        Model = r.Model,
        Room = r.Room,
        ExtraJson = r.ExtraJson,
    };

    private static RegisteredEntity EntiteDepuis(EntityRow r) => new()
    {
        EntityId = r.EntityId,
        DeviceId = r.DeviceId,
        IntegrationId = r.IntegrationId,
        UniqueId = r.UniqueId,
        Kind = Enum.Parse<EntityKind>(r.Kind),
        DisplayName = r.DisplayName,
        DeviceClass = r.DeviceClass,
        Unit = r.Unit,
        StateClass = r.StateClass is null ? null : Enum.Parse<StateClass>(r.StateClass),
        Commands = JsonSerializer.Deserialize<List<string>>(r.CommandsJson) ?? new List<string>(),
        ExtraJson = r.ExtraJson,
        Usage = r.Usage,
        Enabled = r.Enabled != 0,
        Missing = r.Missing != 0,
        NominalPower = r.NominalPower,
        Widget = r.Widget,
    };
}
