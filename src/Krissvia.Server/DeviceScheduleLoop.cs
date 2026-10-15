using System.Threading.Channels;
using Krissvia.Climate.DeviceSchedules;

namespace Krissvia.Server;

/// <summary>
/// Programmations : chargées au démarrage (après les thermostats, que la vérification consulte), puis un passage
/// toutes les 15 s — un changement de créneau part dans les 15 s — et tout de suite après une modification.
/// </summary>
internal sealed partial class DeviceScheduleLoop(
    DeviceScheduleService service,
    DeviceScheduleEngine engine,
    TimeProvider clock,
    ILogger<DeviceScheduleLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(15);

    // Capacité 1 et écriture abandonnée si plein : plusieurs modifications rapprochées font un passage, pas dix.
    private readonly Channel<bool> _reveils = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private void Reveiller() => _reveils.Writer.TryWrite(true);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await service.LoadAsync(cancellationToken);
        foreach (var erreur in service.LoadErrors) ConfigurationIllisible(logger, erreur);
        Demarrage(logger, service.List().Count(p => p.Enabled));
        service.Changed += Reveiller;
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        service.Changed -= Reveiller;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            // Les deux attentes restent en cours d'un tour à l'autre : seule celle qui a abouti est relancée.
            var periode = minuterie.WaitForNextTickAsync(stoppingToken).AsTask();
            var reveil = _reveils.Reader.ReadAsync(stoppingToken).AsTask();
            while (true)
            {
                try
                {
                    await engine.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PassageEnEchec(logger, ex);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Programmations : {Nombre} active(s).")]
    private static partial void Demarrage(ILogger logger, int nombre);

    [LoggerMessage(Level = LogLevel.Error, Message = "Programmation écartée au démarrage, configuration illisible : {Erreur}")]
    private static partial void ConfigurationIllisible(ILogger logger, string erreur);

    [LoggerMessage(Level = LogLevel.Error, Message = "Passage des programmations en échec.")]
    private static partial void PassageEnEchec(ILogger logger, Exception ex);
}
