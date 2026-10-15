using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;

namespace Krissvia.Server;

/// <summary>
/// Écrit chaque ordre dans le journal du service. Un ordre qui n'est pas accepté, ou dont l'effet n'est pas
/// confirmé, sort en avertissement : c'est la trace qui manquait à DomoMud, où la publication réussie d'un ordre
/// parti dans le vide s'écrivait comme un succès.
/// </summary>
internal sealed partial class CommandJournal(IEventBus bus, ILogger<CommandJournal> logger) : IHostedService
{
    private IDisposable? _abonnement;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = bus.Subscribe<CommandCompleted>("journal-des-ordres", (e, _) =>
        {
            var r = e.Record;
            var echec = r.Outcome != CommandOutcome.Accepted || r.Verification == CommandVerification.NotConfirmed;
            if (echec)
                OrdreEnEchec(logger, r.Command.Command, r.Command.EntityId, r.Command.Origin, r.Outcome, r.Verification, r.Detail);
            else
                OrdrePasse(logger, r.Command.Command, r.Command.EntityId, r.Command.Origin, r.Verification, (int)r.Duration.TotalMilliseconds);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ordre {Command} → {EntityId} ({Origin}) : accepté, vérification {Verification}, {DurationMs} ms.")]
    private static partial void OrdrePasse(ILogger logger, string command, string entityId, string origin, CommandVerification verification, int durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ordre {Command} → {EntityId} ({Origin}) : {Outcome}, vérification {Verification}. {Detail}")]
    private static partial void OrdreEnEchec(ILogger logger, string command, string entityId, string origin, CommandOutcome outcome, CommandVerification verification, string? detail);
}
