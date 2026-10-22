using Krissvia.Climate.Thermostats;

namespace Krissvia.Server;

/// <summary>
/// L'auto-TPI en tâche de fond : relit les comptes rendus au démarrage, puis, toutes les heures, recalcule ceux des
/// thermostats qui l'ont demandé et dont le dernier calcul a plus de 20 h. Ne fait que proposer : rien ne change dans
/// la régulation sans le bouton « Appliquer ».
/// </summary>
internal sealed partial class AutoTpiLoop(AutoTpiService autoTpi, TimeProvider clock, ILogger<AutoTpiLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromHours(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await autoTpi.LoadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisser le moteur publier les thermostats en entités avant le premier calcul.
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            while (await minuterie.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (await autoTpi.ComputeDueAsync(stoppingToken) is > 0 and var n) Calcules(logger, n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    EnEchec(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-TPI : {Nombre} thermostat(s) recalculé(s) ; les réglages proposés attendent « Appliquer ».")]
    private static partial void Calcules(ILogger logger, int nombre);

    [LoggerMessage(Level = LogLevel.Error, Message = "Auto-TPI : calcul en échec.")]
    private static partial void EnEchec(ILogger logger, Exception ex);
}
