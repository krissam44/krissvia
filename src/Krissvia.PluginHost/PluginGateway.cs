using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.PluginHost.Services;

namespace Krissvia.PluginHost;

/// <summary>
/// Ce que les messages du contrat 0.3 demandent au cœur : savoir ce qu'un plugin a le droit de lire ou de commander,
/// lire une entité, lui donner un ordre, suivre ses changements (docs/CONTRACT-V0.3.md).
/// </summary>
/// <remarks>
/// Branché sur le hub au démarrage (<see cref="PluginHub.Gateway"/>) et non injecté : le hub est le transport du bus de
/// commandes, en dépendre formerait un cycle.
/// </remarks>
public interface IPluginGateway
{
    EntityAccess AccessFor(string pluginId, string entityId);

    /// <summary>L'entité et son dernier état, ou null si elle n'existe pas.</summary>
    Task<EntityStateUpdate?> ReadAsync(string entityId, CancellationToken ct);

    Task<EntityCommandResponse> CommandAsync(string pluginId, EntityCommandRequest request, CancellationToken ct);

    /// <summary>Appelle <paramref name="handler"/> à chaque changement d'état d'une entité (son identifiant).</summary>
    IDisposable Watch(string subscriber, Func<string, CancellationToken, Task> handler);
}

/// <summary>Le passage du contrat 0.3 vers le registre, le magasin d'états, le bus de commandes et le bus d'événements.</summary>
/// <param name="access">Le droit d'un plugin sur une entité : celui que donnent ses réglages (<see cref="PluginEntityAccess"/>).</param>
public sealed class PluginGateway(
    IEntityRegistry registry,
    IEntityStateReader states,
    ICommandBus commands,
    IEventBus events,
    Func<string, string, EntityAccess> access) : IPluginGateway
{
    public EntityAccess AccessFor(string pluginId, string entityId) => access(pluginId, entityId);

    public async Task<EntityStateUpdate?> ReadAsync(string entityId, CancellationToken ct)
    {
        if (await registry.GetAsync(entityId, ct) is not { } e) return null;
        var etat = states.Get(entityId);
        return new EntityStateUpdate
        {
            EntityId = e.EntityId, Name = e.DisplayName, Kind = e.Kind, DeviceClass = e.DeviceClass, Unit = e.Unit,
            Available = etat is { Available: true },
            Value = etat?.Value, MeasuredAt = etat?.MeasuredAt,
        };
    }

    public async Task<EntityCommandResponse> CommandAsync(string pluginId, EntityCommandRequest request, CancellationToken ct)
    {
        var r = await commands.SendVerifiedAsync(new EntityCommand(request.EntityId, request.Command)
        {
            Args = request.Args is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } a ? a : null,
            Origin = $"plugin:{pluginId}",
        }, ct: ct);
        return new EntityCommandResponse
        {
            Id = request.Id, Outcome = r.Outcome, Detail = r.Detail,
            Verification = JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Verification.ToString()),
        };
    }

    public IDisposable Watch(string subscriber, Func<string, CancellationToken, Task> handler) =>
        events.Subscribe<EntityStateChanged>(subscriber, (e, ct) => handler(e.EntityId, ct));
}

/// <summary>
/// Le droit d'un plugin sur une entité : celui que lui donnent les entités choisies dans ses réglages (page Services).
/// Sans fiche installée ni réglages enregistrés, aucun : un plugin lancé à la main ne lit et ne commande rien.
/// </summary>
public sealed class PluginEntityAccess(Func<string, PluginManifest?> manifest, PluginSettingsService settings)
{
    public EntityAccess AccessFor(string pluginId, string entityId)
    {
        if (manifest(pluginId) is not { } fiche || !settings.HasSaved(pluginId)) return EntityAccess.None;
        if (SettingsSchema.Parse(fiche.Settings) is not { } schema) return EntityAccess.None;
        return SettingsSchema.EntityReferences(schema, settings.Get(fiche)).GetValueOrDefault(entityId, EntityAccess.None);
    }
}
