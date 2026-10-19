using Krissvia.Climate.Presence;

namespace Krissvia.Server;

/// <summary>
/// Présence du foyer recalculée toutes les 10 s, comme le tick des thermostats, puis transmise à l'absence (départ
/// constaté, retour anticipé, fin de période). Le journal ne note que les bascules.
/// </summary>
internal sealed partial class PresenceLoop(HomePresence presence, AbsenceService absence, TimeProvider clock, ILogger<PresenceLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(10);
    private HomePresenceState? _precedent;
    private ScheduledAbsence? _periode;
    private HomeAbsenceState? _absence;

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

                    await absence.UpdateAsync(etat, stoppingToken);
                    var periode = absence.Period;
                    if (_periode is { } av && periode is not null && av.Departure == periode.Departure && av.Return == periode.Return)
                    {
                        if (periode.DepartureSeen && !av.DepartureSeen) DepartConstate(logger);
                        if (periode.Cancelled && !av.Cancelled) RetourAnticipe(logger, periode.Return.ToLocalTime());
                    }
                    else if (_periode is { } fini && periode is null && clock.GetUtcNow() >= fini.Return)
                    {
                        PeriodeTerminee(logger, fini.Return.ToLocalTime());
                    }

                    if (_absence is { } a && a != absence.Current)
                        AbsenceChange(logger, absence.Current.Away ? "vide" : "occupée", absence.Current.Source);
                    (_periode, _absence) = (periode, absence.Current);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Absence programmée : départ constaté, un retour anticipé pourra l'annuler.")]
    private static partial void DepartConstate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Absence programmée annulée : présence détectée avant le retour prévu ({Retour:dd/MM HH:mm}).")]
    private static partial void RetourAnticipe(ILogger logger, DateTimeOffset retour);

    [LoggerMessage(Level = LogLevel.Information, Message = "Absence programmée terminée (retour {Retour:dd/MM HH:mm}) : la détection de présence reprend.")]
    private static partial void PeriodeTerminee(ILogger logger, DateTimeOffset retour);

    [LoggerMessage(Level = LogLevel.Information, Message = "Absence : maison {Etat} ({Source}).")]
    private static partial void AbsenceChange(ILogger logger, string etat, AbsenceSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Calcul de la présence en échec.")]
    private static partial void CalculEnEchec(ILogger logger, Exception ex);
}
