using Krissvia.Climate.LoadShedding;

namespace Krissvia.Server;

/// <summary>
/// Délesteur : configuration chargée au démarrage (après les thermostats, qu'elle référence), puis un tick toutes les
/// 2 s comme DomoMud. Le journal ne note que les bascules, jamais chaque tick (D10).
/// </summary>
internal sealed partial class LoadSheddingLoop(
    LoadSheddingService service,
    LoadShedder shedder,
    TimeProvider clock,
    ILogger<LoadSheddingLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(2);
    private bool _sourceIndisponible;
    private string _nonConfirmes = "";
    private string? _source;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await service.LoadAsync(cancellationToken);
        if (service.LoadError is { } erreur) ConfigurationIllisible(logger, erreur);
        else Demarrage(logger, service.Configuration.Enabled, service.Configuration.Devices.Count);
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
                    await TickAsync(stoppingToken);
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

    private async Task TickAsync(CancellationToken ct)
    {
        var lecture = service.ReadPower(clock.GetUtcNow());
        if (service.ActiveSource is { } source && source != _source) SourceUtilisee(logger, source);
        _source = service.ActiveSource ?? _source;
        var tick = await shedder.TickAsync(lecture, ct);
        service.RecordTick(lecture, tick, clock.GetUtcNow());

        foreach (var id in tick.Shed) Deleste(logger, id, lecture?.Value);
        foreach (var id in tick.Restored) Retabli(logger, id, lecture?.Value);
        if (tick.Transition == ShedTransition.Started) EpisodeCommence(logger, lecture?.Value);
        if (tick.Transition == ShedTransition.Ended) EpisodeTermine(logger);

        // D6 : une mesure absente bloque toute décision ; on le dit une fois, à la bascule.
        if (service.Configuration.Enabled && tick.SourceUnavailable != _sourceIndisponible)
        {
            if (tick.SourceUnavailable) SourcePerdue(logger);
            else SourceRetrouvee(logger);
        }
        _sourceIndisponible = tick.SourceUnavailable;

        var nonConfirmes = string.Join(", ", tick.Unconfirmed);
        if (nonConfirmes != _nonConfirmes && nonConfirmes.Length > 0) CoupuresNonConfirmees(logger, nonConfirmes);
        _nonConfirmes = nonConfirmes;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Délestage : {Actif}, {Nombre} équipement(s) délestable(s).")]
    private static partial void Demarrage(ILogger logger, bool actif, int nombre);

    [LoggerMessage(Level = LogLevel.Error, Message = "Délestage désactivé : configuration illisible ({Erreur}).")]
    private static partial void ConfigurationIllisible(ILogger logger, string erreur);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Délestage : début d'épisode ({Puissance} VA).")]
    private static partial void EpisodeCommence(ILogger logger, double? puissance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Délestage : fin d'épisode, tout est rétabli.")]
    private static partial void EpisodeTermine(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Délestage : {Equipement} coupé ({Puissance} VA).")]
    private static partial void Deleste(ILogger logger, string equipement, double? puissance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Délestage : {Equipement} rétabli ({Puissance} VA).")]
    private static partial void Retabli(ILogger logger, string equipement, double? puissance);

    [LoggerMessage(Level = LogLevel.Error, Message = "Délestage : coupure NON confirmée pour {Equipements}, retentée à chaque tick.")]
    private static partial void CoupuresNonConfirmees(ILogger logger, string equipements);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Délestage : mesure de puissance absente ou périmée, aucune décision possible.")]
    private static partial void SourcePerdue(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Délestage : mesure de puissance prise sur « {Source} ».")]
    private static partial void SourceUtilisee(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Délestage : mesure de puissance retrouvée.")]
    private static partial void SourceRetrouvee(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tick du délestage en échec.")]
    private static partial void TickEnEchec(ILogger logger, Exception ex);
}
