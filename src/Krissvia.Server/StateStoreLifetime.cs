using Krissvia.Core.States;
using Krissvia.Persistence.Sqlite;

namespace Krissvia.Server;

/// <summary>
/// Cycle de vie de la base et du magasin d'états : schéma et restauration au démarrage, écriture périodique,
/// dernière écriture à l'arrêt. Le magasin n'écrit jamais à chaque mesure (Linky : toutes les 2 s).
/// </summary>
internal sealed partial class StateStoreLifetime(
    SqliteDatabase db,
    EntityStateStore etats,
    TimeProvider clock,
    ILogger<StateStoreLifetime> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(30);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await db.InitializeAsync(cancellationToken);
        await etats.LoadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            while (await minuterie.WaitForNextTickAsync(stoppingToken)) await EcrireAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await EcrireAsync(cancellationToken);
    }

    private async Task EcrireAsync(CancellationToken ct)
    {
        try
        {
            await etats.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Les états restent à écrire : la prochaine tentative les reprendra.
            EcritureEnEchec(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Écriture des états en échec ; nouvelle tentative au prochain cycle.")]
    private static partial void EcritureEnEchec(ILogger logger, Exception ex);
}
