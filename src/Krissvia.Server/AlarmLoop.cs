using Krissvia.Climate.Alarm;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;

namespace Krissvia.Server;

/// <summary>
/// Fait vivre l'alarme : état repris au démarrage, détections par le bus (fronts des capteurs surveillés), tick d'une
/// seconde pour les décomptes et la durée de sirène. Le journal du serveur reprend celui de l'alarme.
/// </summary>
internal sealed partial class AlarmLoop(
    AlarmService service,
    AlarmEngine engine,
    CoreCommandRouter routeur,
    IEventBus bus,
    TimeProvider clock,
    ILogger<AlarmLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(1);
    private IDisposable? _abonnement;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        service.Journaled += Noter;
        await engine.StartAsync(cancellationToken);
        Demarrage(logger, service.Settings.Enabled ? AlarmLabels.Mode(engine.State.Mode) : "désactivée", service.Settings.Sensors.Count);

        service.Changed += Reglages;
        routeur.Register(AlarmEngine.IntegrationId, engine);   // arm / disarm de la carte du tableau de bord
        _abonnement = bus.Subscribe<EntityStateChanged>("alarme", (e, ct) => engine.OnStateChangedAsync(e, ct));
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        service.Changed -= Reglages;
        service.Journaled -= Noter;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            while (await minuterie.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await engine.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TickEnEchec(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Hors du fil de l'enregistrement : la centrale prend son propre verrou.</summary>
    private void Reglages() => _ = Task.Run(async () =>
    {
        try
        {
            await engine.OnSettingsChangedAsync();
        }
        catch (Exception ex)
        {
            TickEnEchec(logger, ex);
        }
    });

    private void Noter(AlarmJournalEntry ligne) => Journal(logger, ligne.Text);

    [LoggerMessage(Level = LogLevel.Information, Message = "Alarme : {Etat}, {Capteurs} capteur(s) surveillé(s).")]
    private static partial void Demarrage(ILogger logger, string etat, int capteurs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alarme : {Texte}")]
    private static partial void Journal(ILogger logger, string texte);

    [LoggerMessage(Level = LogLevel.Error, Message = "Alarme : traitement en échec.")]
    private static partial void TickEnEchec(ILogger logger, Exception ex);
}
