using Krissvia.Core.Overview;

namespace Krissvia.Server;

/// <summary>Moyennes par pièce recalculées toutes les 10 s, au rythme des thermostats qui peuvent s'en servir.</summary>
internal sealed partial class RoomAverageLoop(RoomAverageEntities moyennes, TimeProvider clock, ILogger<RoomAverageLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            do
            {
                try
                {
                    await moyennes.UpdateAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CalculEnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Calcul des moyennes par pièce en échec.")]
    private static partial void CalculEnEchec(ILogger logger, Exception ex);
}
