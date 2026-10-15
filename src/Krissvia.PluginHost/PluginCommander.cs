using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost;

/// <summary>
/// Envoi des commandes des moteurs au plugin qui porte l'entité. Ne masque jamais un échec : plugin déconnecté
/// ou silencieux ⇒ <see cref="CommandOutcome.Unreachable"/>, entité inconnue ⇒ <see cref="CommandOutcome.Failed"/>.
/// </summary>
public sealed class PluginCommander(PluginHub hub, IEntityRegistry registry, ILogger<PluginCommander> logger) : IEntityCommander
{
    public async Task<CommandOutcome> SendAsync(
        string entityId,
        string command,
        CommandPriority priority = CommandPriority.Normal,
        int? ttlSeconds = null,
        CancellationToken ct = default)
    {
        var entite = await registry.GetAsync(entityId, ct);
        if (entite is null)
        {
            // Le cas « Radiateur Chbre RdC » de DomoMud : une référence vers rien doit échouer bruyamment.
            Log.CommandeEntiteInconnue(logger, entityId, command);
            return CommandOutcome.Failed;
        }

        if (!hub.TryGetSession(entite.IntegrationId, out var session))
        {
            Log.CommandePluginAbsent(logger, entite.IntegrationId, entityId, command);
            return CommandOutcome.Unreachable;
        }

        return await session.EnvoyerCommandeAsync(entite.UniqueId, command, priority, ttlSeconds, ct);
    }
}
