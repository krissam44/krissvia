using System.Threading.Channels;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.Commands;

namespace Krissvia.Server;

/// <summary>
/// Charge la configuration des thermostats au démarrage, puis fait tourner le moteur toutes les 10 s, et tout de
/// suite après un changement de configuration : un mode choisi coupe ou allume le radiateur sans attendre jusqu'à
/// 10 s, et l'écran montre aussitôt la consigne régulée. Enregistré après <see cref="StateStoreLifetime"/> : la base
/// doit être prête avant la lecture de la configuration.
/// </summary>
internal sealed partial class ThermostatLoop(
    ThermostatService thermostats,
    ThermostatEngine engine,
    ThermostatEntities entites,
    CoreCommandRouter routeur,
    TimeProvider clock,
    ILogger<ThermostatLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(10);

    // Capacité 1 et écriture abandonnée si plein : dix clics rapides font un recalcul, pas dix.
    private readonly Channel<bool> _reveils = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private void Reveiller() => _reveils.Writer.TryWrite(true);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await thermostats.LoadAsync(cancellationToken);
        foreach (var erreur in thermostats.LoadErrors) ConfigurationIllisible(logger, erreur);
        Demarrage(logger, thermostats.List().Count);
        thermostats.Changed += Reveiller;
        routeur.Register(ThermostatEntities.IntegrationId, entites);
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        thermostats.Changed -= Reveiller;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            // Les deux attentes restent en cours d'un tour à l'autre : seule celle qui a abouti est relancée. En
            // recréer une à chaque tour perdrait le réveil arrivé pendant le tick précédent.
            var periode = minuterie.WaitForNextTickAsync(stoppingToken).AsTask();
            var reveil = _reveils.Reader.ReadAsync(stoppingToken).AsTask();
            while (true)
            {
                try
                {
                    await engine.TickAsync(stoppingToken);
                    await entites.PublishAsync(stoppingToken);   // l'état que voient l'API, le flux et les widgets
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Une erreur d'un tick ne doit pas arrêter la régulation des suivants.
                    TickEnEchec(logger, ex);
                }

                if (await Task.WhenAny(periode, reveil) == periode)
                {
                    if (!await periode) break;
                    periode = minuterie.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    await reveil;
                    reveil = _reveils.Reader.ReadAsync(stoppingToken).AsTask();
                }
            }
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
