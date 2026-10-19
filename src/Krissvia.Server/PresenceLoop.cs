using Krissvia.Climate.Presence;

namespace Krissvia.Server;

/// <summary>
/// Présence du foyer recalculée toutes les 10 s, comme le tick des thermostats. Le journal ne note que les bascules :
/// maison vide, retour, détection perdue.
/// </summary>
internal sealed partial class PresenceLoop(HomePresence presence, TimeProvider clock, ILogger<PresenceLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(10);
    private HomePresenceState? _precedent;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            do
            {
                try
                {
                    var etat = await presence.UpdateAsync(stoppingToken);
                    if (_precedent is { } p && etat.Tracked > 0)
                    {
                        if (etat.DetectionLost && !p.DetectionLost) DetectionPerdue(logger, string.Join(", ", etat.Unknown));
                        else if (!etat.Occupied && p.Occupied) MaisonVide(logger, etat.Tracked);
                        else if (etat.Occupied && !p.Occupied) Retour(logger, string.Join(", ", etat.Present));
                    }

                    _precedent = etat;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CalculEnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Présence : maison vide (aucun des {Suivis} appareils suivis).")]
    private static partial void MaisonVide(ILogger logger, int suivis);

    [LoggerMessage(Level = LogLevel.Information, Message = "Présence : retour ({Presents}).")]
    private static partial void Retour(ILogger logger, string presents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Présence : détection perdue ({Inconnus}) — maison considérée occupée par sécurité.")]
    private static partial void DetectionPerdue(ILogger logger, string inconnus);

    [LoggerMessage(Level = LogLevel.Error, Message = "Calcul de la présence en échec.")]
    private static partial void CalculEnEchec(ILogger logger, Exception ex);
}
