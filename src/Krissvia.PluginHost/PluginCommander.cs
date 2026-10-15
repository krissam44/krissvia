using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost;

/// <summary>
/// Transport des ordres du bus de commandes vers le plugin qui porte l'entité. Plugin déconnecté ou silencieux ⇒
/// <see cref="CommandOutcome.Unreachable"/>. Les contrôles (entité connue, commande déclarée) sont faits par le
/// <see cref="CommandBus"/>, avant.
/// </summary>
public sealed class PluginCommander(PluginHub hub, ILogger<PluginCommander> logger) : ICommandDispatcher
{
    public async Task<CommandOutcome> DispatchAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct)
    {
        if (!hub.TryGetSession(entity.IntegrationId, out var session))
        {
            Log.CommandePluginAbsent(logger, entity.IntegrationId, entity.EntityId, command.Command);
            return CommandOutcome.Unreachable;
        }

        return await session.EnvoyerCommandeAsync(entity.UniqueId, command.Command, command.Args, command.Priority, command.TtlSeconds, ct);
    }
}
