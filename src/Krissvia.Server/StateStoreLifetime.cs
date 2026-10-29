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
    private static readonly TimeSpan DelaiDerniereEcriture = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// La dernière écriture, à l'arrêt, avec son PROPRE délai. L'arrêt de l'hôte a 15 s pour tous les services, arrêtés
    /// l'un après l'autre en commençant par le serveur web ; celui-ci peut les consommer entièrement (pages encore
    /// ouvertes), et ce service, enregistré parmi les premiers, est arrêté parmi les derniers : son jeton était déjà
    /// annulé, l'ouverture SQLite levait, l'exception remontait jusqu'à <c>Run()</c> et le processus finissait en
    /// core-dump (VM, 29/10/2026) — en perdant ce qu'il fallait écrire. Elle ne lève jamais : un arrêt doit aboutir.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        using var delai = new CancellationTokenSource(DelaiDerniereEcriture);
        try
        {
            await etats.FlushAsync(delai.Token);
        }
        catch (Exception ex)
        {
            DerniereEcritureEnEchec(logger, ex);
        }
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Dernière écriture des états, à l'arrêt, en échec : les plus récents sont perdus.")]
    private static partial void DerniereEcritureEnEchec(ILogger logger, Exception ex);
}
