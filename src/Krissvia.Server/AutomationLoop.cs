using Krissvia.Climate.Automations;
using Krissvia.Core.Events;

namespace Krissvia.Server;

/// <summary>
/// Fait vivre les automatismes : chargés au démarrage, nourris par chaque changement d'état du bus (boutons, fronts),
/// et par l'horloge toutes les 15 s (horaires). Le journal du service note chaque exécution.
/// </summary>
/// <remarks>
/// Une action retardée en attente au moment d'un arrêt du serveur est perdue : comme un horaire manqué, elle n'est pas
/// rattrapée au redémarrage.
/// </remarks>
internal sealed partial class AutomationLoop(
    AutomationService automatismes,
    AutomationEngine engine,
    IEventBus bus,
    TimeProvider clock,
    ILogger<AutomationLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(15);
    private IDisposable? _abonnement;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await automatismes.LoadAsync(cancellationToken);
        foreach (var erreur in automatismes.LoadErrors) Illisible(logger, erreur);
        Demarrage(logger, automatismes.List().Count);

        engine.Ran += Noter;
        _abonnement = bus.Subscribe<EntityStateChanged>("automatismes", (e, _) =>
        {
            engine.OnStateChanged(e);
            return Task.CompletedTask;
        });
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        engine.Ran -= Noter;
        return base.StopAsync(cancellationToken);
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
                    engine.Tick();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TickEnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Noter(AutomationRun r)
    {
        if (r.Outcome is RunOutcome.Done or RunOutcome.ConditionsNotMet) Execute(logger, r.Name, r.Cause, r.Outcome, r.Detail ?? "");
        else EnEchec(logger, r.Name, r.Cause, r.Outcome, r.Detail ?? "");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatismes : {Nombre} en service.")]
    private static partial void Demarrage(ILogger logger, int nombre);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automatisme écarté au démarrage, configuration illisible : {Erreur}")]
    private static partial void Illisible(ILogger logger, string erreur);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatisme « {Nom} » ({Cause}) : {Issue}. {Detail}")]
    private static partial void Execute(ILogger logger, string nom, string cause, RunOutcome issue, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatisme « {Nom} » ({Cause}) : {Issue}. {Detail}")]
    private static partial void EnEchec(ILogger logger, string nom, string cause, RunOutcome issue, string? detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tick des automatismes en échec.")]
    private static partial void TickEnEchec(ILogger logger, Exception ex);
}
