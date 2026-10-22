using Krissvia.Core.Events;
using Krissvia.Core.History;

namespace Krissvia.Server;

/// <summary>
/// L'historique : écrit les points retenus toutes les 30 s (et à l'arrêt), consolide les heures toutes les 10 min,
/// purge les points bruts une fois par jour.
/// </summary>
/// <remarks>
/// <b>La consolidation passe avant la purge</b>, et la purge ne descend jamais sous la dernière heure consolidée :
/// purger d'abord effacerait des points avant de les avoir totalisés (DomoMud, conso_jour). Une base neuve, ou
/// restée arrêtée, est rattrapée au premier passage depuis sa dernière heure consolidée.
/// </remarks>
internal sealed partial class HistoryLoop(
    HistoryRecorder recorder,
    IHistoryRepository repository,
    IEventBus bus,
    TimeProvider clock,
    ILogger<HistoryLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeriodeConsolidation = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PeriodePurge = TimeSpan.FromDays(1);

    private IDisposable? _abonnement;
    private DateTimeOffset _derniereConsolidation;
    private DateTimeOffset _dernierePurge;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = recorder.Attach(bus);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            do
            {
                await EcrireAsync(stoppingToken);
                var maintenant = clock.GetUtcNow();
                try
                {
                    if (maintenant - _derniereConsolidation >= PeriodeConsolidation)
                    {
                        // Depuis la dernière heure consolidée (elle peut être incomplète), ou 30 jours au premier passage.
                        var depuis = await repository.LastRolledHourAsync(stoppingToken) ?? maintenant - HourlyRollup.Retention;
                        await repository.RollupAsync(depuis, maintenant, stoppingToken);
                        _derniereConsolidation = maintenant;
                    }

                    if (maintenant - _dernierePurge >= PeriodePurge
                        && HourlyRollup.LimitePurge(maintenant, await repository.LastRolledHourAsync(stoppingToken)) is { } limite)
                    {
                        var supprimes = await repository.PurgeAsync(limite, stoppingToken);
                        if (supprimes > 0) Purge(logger, supprimes, limite);
                        _dernierePurge = maintenant;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ConsolidationEnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        await base.StopAsync(cancellationToken);
        await EcrireAsync(cancellationToken);
    }

    private async Task EcrireAsync(CancellationToken ct)
    {
        try
        {
            await recorder.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Les points restent en attente : la prochaine écriture les reprendra.
            EcritureEnEchec(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Historique : écriture en échec ; nouvelle tentative au prochain cycle.")]
    private static partial void EcritureEnEchec(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Historique : consolidation ou purge en échec.")]
    private static partial void ConsolidationEnEchec(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Historique : {Nombre} point(s) bruts antérieurs au {Limite:u} supprimés (heures consolidées gardées).")]
    private static partial void Purge(ILogger logger, int nombre, DateTimeOffset limite);
}
