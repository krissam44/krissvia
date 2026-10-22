using Krissvia.Climate.Presence;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Supervision;
using Krissvia.PluginHost.Services;

namespace Krissvia.Server;

/// <summary>
/// La supervision toutes les minutes : un délai de grâce de 5 min se mesure à la minute, pas au quart d'heure de
/// l'analyse météo. Rassemble ce que regardent les règles — entités, plugins activés, présence, disque de la base — et
/// laisse <see cref="SupervisionWatch"/> conclure. Le journal ne note que les changements.
/// </summary>
internal sealed partial class SupervisionLoop(
    SupervisionWatch watch,
    IEntityRegistry registry,
    IEntityStateReader states,
    PluginSupervisor plugins,
    HomePresence presence,
    string databaseDirectory,
    TimeProvider clock,
    ILogger<SupervisionLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(1);
    private HashSet<string> _enAlerte = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var minuterie = new PeriodicTimer(Periode, clock);
        try
        {
            do
            {
                try
                {
                    var rapport = await watch.CheckAsync(Entree(), stoppingToken);
                    var enAlerte = rapport.Items.Where(i => i.Alerting).Select(i => i.Item.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var i in rapport.Items.Where(i => i.Alerting && !_enAlerte.Contains(i.Item.Key))) Panne(logger, i.Item.Name, i.Item.Detail);
                    foreach (var cle in _enAlerte.Where(k => !enAlerte.Contains(k))) Retour(logger, cle);
                    _enAlerte = enAlerte;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    EnEchec(logger, ex);
                }
            }
            while (await minuterie.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private SupervisionInput Entree()
    {
        // Les plugins ACTIVÉS seulement : un plugin désactivé exprès n'est pas en panne. Lancé ailleurs et connecté
        // (Visual Studio), il compte comme connecté.
        var etatPlugins = plugins.Snapshots()
            .Where(s => s.Enabled)
            .Select(s => new PluginHealth(s.Id, s.Name, s.Status is ServiceStatus.Connected or ServiceStatus.External))
            .ToList();

        var p = presence.Current;
        return new SupervisionInput(clock.GetUtcNow(), registry.Loaded, states, etatPlugins,
            p is { Tracked: > 0 } ? p.DetectionLost : null, EspaceLibre(), clock.LocalTimeZone);
    }

    private long? EspaceLibre()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(databaseDirectory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;   // inconnu : on ne conclut rien
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Supervision : {Nom} — {Detail}.")]
    private static partial void Panne(ILogger logger, string nom, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Supervision : {Cle} rétabli.")]
    private static partial void Retour(ILogger logger, string cle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Supervision en échec.")]
    private static partial void EnEchec(ILogger logger, Exception ex);
}
