using Krissvia.Climate.Alerts;
using Krissvia.Core.Notifications;

namespace Krissvia.Server;

/// <summary>
/// Analyse des alertes toutes les 15 min (la prévision change toutes les 10 min au mieux). Le journal ne note que ce
/// qui se passe : une alerte levée, rétablie, ou un envoi qui échoue.
/// </summary>
internal sealed partial class AlertLoop(AlertService alerts, FrostWatch frost, TimeProvider clock, ILogger<AlertLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(15);

    /// <summary>Laisse aux plugins le temps de republier leurs états après un démarrage : sinon tout est « indisponible ».</summary>
    private static readonly TimeSpan Attente = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Attente, clock, stoppingToken);
            using var minuterie = new PeriodicTimer(Periode, clock);
            do
            {
                try
                {
                    var reglages = await alerts.GetSettingsAsync(stoppingToken);
                    foreach (var c in await frost.CheckAsync(reglages.Frost, stoppingToken))
                    {
                        if (c.Outcome == nameof(RaiseOutcome.Raised)) Gel(logger, c.Place, c.Minimum);
                        else if (c.Outcome == "Resolved") GelEcarte(logger, c.Place, c.Minimum);
                        else if (c.Outcome == nameof(RaiseOutcome.NotDelivered)) NonEnvoyee(logger, c.Place);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AnalyseEnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alerte gel à {Lieu} : minimum prévu {Minimum} °C.")]
    private static partial void Gel(ILogger logger, string lieu, double? minimum);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gel écarté à {Lieu} : minimum prévu {Minimum} °C.")]
    private static partial void GelEcarte(ILogger logger, string lieu, double? minimum);

    [LoggerMessage(Level = LogLevel.Error, Message = "Alerte gel à {Lieu} non envoyée (serveur ntfy injoignable) : nouvel essai à la prochaine analyse.")]
    private static partial void NonEnvoyee(ILogger logger, string lieu);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analyse des alertes en échec.")]
    private static partial void AnalyseEnEchec(ILogger logger, Exception ex);
}
