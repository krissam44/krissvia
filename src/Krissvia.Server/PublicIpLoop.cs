using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Supervision;

namespace Krissvia.Server;

/// <summary>
/// Surveille l'IP publique : chaque changement d'état d'une entité <c>public_ip</c> passe par <see cref="PublicIpWatch"/>,
/// et les envois échoués sont retentés toutes les minutes. Le journal note chaque changement.
/// </summary>
internal sealed partial class PublicIpLoop(PublicIpWatch watch, IEventBus bus, TimeProvider clock, ILogger<PublicIpLoop> logger) : BackgroundService
{
    private IDisposable? _abonnement;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        watch.Changed += Noter;
        _abonnement = bus.Subscribe<EntityStateChanged>("ip-publique", async (e, ct) => await watch.OnStateChangedAsync(e, ct));
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        watch.Changed -= Noter;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await minuterie.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await watch.RetryPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RelanceEnEchec(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Noter(PublicIpChange c, SendOutcome issue) => Changement(logger, c.Name, c.Previous, c.Current, issue);

    [LoggerMessage(Level = LogLevel.Information, Message = "IP publique changée ({Nom}) : {Avant} → {Apres}. Notification : {Issue}.")]
    private static partial void Changement(ILogger logger, string nom, string avant, string apres, SendOutcome issue);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relance des notifications d'IP publique en échec.")]
    private static partial void RelanceEnEchec(ILogger logger, Exception ex);
}
