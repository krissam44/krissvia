using System.Collections.Concurrent;
using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Commands;

/// <summary>Exécute les ordres reçus par les entités qu'un module du cœur publie (thermostats, bientôt l'alarme…).</summary>
public interface ICoreCommandHandler
{
    Task<CommandOutcome> HandleAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct);
}

/// <summary>Les intégrations du cœur : les entités que publie un module, et non un plugin.</summary>
public static class CoreIntegrations
{
    /// <summary>
    /// Préfixe des intégrations du cœur. Le point est interdit dans l'identifiant d'un plugin (le hub le refuse) :
    /// aucun plugin ne peut donc se faire passer pour un module du cœur, ni effacer ses entités par un « declare ».
    /// </summary>
    public const string Prefix = "coeur.";

    public const string Thermostats = Prefix + "thermostats";

    public static bool IsCore(string integrationId) => integrationId.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// Aiguillage des ordres du <see cref="CommandBus"/> : vers le module du cœur qui porte l'entité, sinon vers son
/// plugin. Le bus garde ainsi son unique passage : contrôles, journal et vérification valent pour un thermostat
/// comme pour une prise.
/// </summary>
/// <remarks>
/// Les modules s'inscrivent au démarrage (<see cref="Register"/>) au lieu d'être injectés : un module donne lui-même
/// des ordres par le bus, l'injection formerait un cycle.
/// </remarks>
public sealed class CoreCommandRouter(ICommandDispatcher plugins) : ICommandDispatcher
{
    private readonly ConcurrentDictionary<string, ICoreCommandHandler> _modules = new(StringComparer.Ordinal);

    public void Register(string integrationId, ICoreCommandHandler handler)
    {
        if (!CoreIntegrations.IsCore(integrationId))
            throw new ArgumentException($"« {integrationId} » n'est pas une intégration du cœur ({CoreIntegrations.Prefix}…).", nameof(integrationId));
        _modules[integrationId] = handler;
    }

    public Task<CommandOutcome> DispatchAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct)
    {
        if (!CoreIntegrations.IsCore(entity.IntegrationId)) return plugins.DispatchAsync(entity, command, ct);

        // Module pas (encore) démarré : comme un plugin absent, l'ordre ne passe pas et on ne fait pas semblant.
        return _modules.TryGetValue(entity.IntegrationId, out var module)
            ? module.HandleAsync(entity, command, ct)
            : Task.FromResult(CommandOutcome.Unreachable);
    }
}
