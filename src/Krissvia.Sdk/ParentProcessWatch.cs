using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krissvia.Sdk;

/// <summary>
/// Arrête le plugin quand le cœur qui l'a lancé disparaît (<see cref="CoreClientOptions.ParentPid"/>).
/// </summary>
/// <remarks>
/// Un plugin orphelin continue d'interroger ses appareils et frappe à la porte du cœur suivant avec un jeton
/// périmé, refusé, toutes les minutes. Sous Windows le Job Object du cœur le tue déjà, sous Linux systemd tue le
/// cgroup du service : ceci couvre tout le reste (cœur lancé à la main sous Linux, job refusé…). Sans
/// <c>ParentPid</c>, ne fait rien.
/// </remarks>
public sealed partial class ParentProcessWatch(
    CoreClientOptions options,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<ParentProcessWatch> logger,
    Func<int, bool>? isAlive = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ParentPid is not { } pid) return;

        if (await WaitForExitAsync(pid, options.ParentCheckPeriod, clock, isAlive ?? IsAlive, stoppingToken))
        {
            ParentDisparu(logger, pid);
            lifetime.StopApplication();
        }
    }

    /// <summary>Rend vrai quand le processus <paramref name="pid"/> n'existe plus, faux à l'annulation.</summary>
    public static async Task<bool> WaitForExitAsync(int pid, TimeSpan period, TimeProvider clock, Func<int, bool> isAlive, CancellationToken ct)
    {
        try
        {
            while (isAlive(pid)) await Task.Delay(period, clock, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Vrai si le processus existe encore.</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            using var processus = Process.GetProcessById(pid);
            return !processus.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // aucun processus de ce numéro
        }
        catch (InvalidOperationException)
        {
            return false;   // sorti entre-temps
        }
        catch (Win32Exception)
        {
            return true;    // il existe, mais on n'a pas le droit de l'interroger : dans le doute, rester
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Le cœur qui a lancé ce plugin (processus {Pid}) a disparu : arrêt du plugin.")]
    private static partial void ParentDisparu(ILogger logger, int pid);
}

public static class ParentProcessWatchExtensions
{
    /// <summary>
    /// Arrête le plugin à la disparition du cœur qui l'a lancé. À appeler dans le <c>Program.cs</c> de chaque
    /// plugin, avec les options lues de la section <c>Krissvia</c> (où <c>Krissvia__ParentPid</c> arrive seul).
    /// </summary>
    public static IServiceCollection AddParentProcessWatch(this IServiceCollection services, CoreClientOptions options) =>
        services.AddHostedService(sp => new ParentProcessWatch(
            options,
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<ParentProcessWatch>>()));
}
