using Krissvia.Climate.Thermostats;

namespace Krissvia.Server;

/// <summary>
/// Charge la configuration des thermostats au démarrage, puis fait tourner le moteur toutes les 10 s. Enregistré
/// après <see cref="StateStoreLifetime"/> : la base doit être prête avant la lecture de la configuration.
/// </summary>
internal sealed partial class ThermostatLoop(
    ThermostatService thermostats,
    ThermostatEngine engine,
    TimeProvider clock,
    ILogger<ThermostatLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(10);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await thermostats.LoadAsync(cancellationToken);
        foreach (var erreur in thermostats.LoadErrors) ConfigurationIllisible(logger, erreur);
        Demarrage(logger, thermostats.List().Count);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            do
            {
                try
                {
                    await engine.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Une erreur d'un tick ne doit pas arrêter la régulation des suivants.
                    TickEnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Thermostats : {Nombre} en service.")]
    private static partial void Demarrage(ILogger logger, int nombre);

    [LoggerMessage(Level = LogLevel.Error, Message = "Thermostat écarté au démarrage, configuration illisible : {Erreur}")]
    private static partial void ConfigurationIllisible(ILogger logger, string erreur);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tick des thermostats en échec.")]
    private static partial void TickEnEchec(ILogger logger, Exception ex);
}
