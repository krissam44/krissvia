using Krissvia.Contracts;
using Krissvia.Core.Commands;

namespace Krissvia.Climate.Tests;

/// <summary>
/// Base des bus de commandes de test. Seul le transport quitte la machine, donc seul lui est simulé : l'issue de
/// l'envoi (<see cref="Envoyer"/>) et le comportement de l'appareil (<see cref="Obeit"/>), qui décide si un ordre
/// accepté est ensuite confirmé par un nouvel état.
/// </summary>
internal abstract class FauxBusCommandes : ICommandBus
{
    private readonly object _verrou = new();

    /// <summary>Faux : l'ordre est accepté, mais l'appareil ne publie aucun état qui le confirme.</summary>
    public Func<string, string, bool> Obeit { get; set; } = (_, _) => true;

    /// <summary>Ordres envoyés avec vérification : entité, origine, délai de vérification demandé.</summary>
    public List<(string EntityId, string Origin, TimeSpan? Timeout)> Verifies { get; } = [];

    protected abstract CommandOutcome Envoyer(string entityId, string command, CommandPriority priority);

    public Task<CommandOutcome> SendAsync(
        string entityId, string command, CommandPriority priority = CommandPriority.Normal, int? ttlSeconds = null, CancellationToken ct = default) =>
        Task.FromResult(Envoyer(entityId, command, priority));

    public Task<CommandOutcome> SendAsync(EntityCommand command, CancellationToken ct = default) =>
        Task.FromResult(Envoyer(command.EntityId, command.Command, command.Priority));

    public Task<VerifiedCommandResult> SendVerifiedAsync(EntityCommand command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        lock (_verrou) Verifies.Add((command.EntityId, command.Origin, timeout));

        var issue = Envoyer(command.EntityId, command.Command, command.Priority);
        var verification = issue != CommandOutcome.Accepted ? CommandVerification.NotSent
            : Obeit(command.EntityId, command.Command) ? CommandVerification.Confirmed
            : CommandVerification.NotConfirmed;
        return Task.FromResult(new VerifiedCommandResult(issue, verification));
    }

    public IReadOnlyList<CommandRecord> RecentCommands => [];
}
