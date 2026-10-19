using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost.Services;

/// <summary>Un processus de plugin lancé par le superviseur.</summary>
public interface IPluginProcess
{
    int ProcessId { get; }

    /// <summary>Se termine à la sortie du processus, avec son code de sortie.</summary>
    Task<int> Exited { get; }

    /// <summary>Arrête le processus et ses descendants. Sans effet s'il est déjà terminé.</summary>
    void Kill();
}

/// <summary>Lance un plugin. Simulé dans les tests ; un vrai processus en production.</summary>
public interface IPluginLauncher
{
    /// <param name="ligne">Appelé pour chaque ligne écrite par le plugin (sortie et erreurs).</param>
    IPluginProcess Start(PluginManifest plugin, IReadOnlyDictionary<string, string> environnement, Action<string> ligne);
}

/// <summary>Les interpréteurs connus pour <see cref="PluginManifest.Runtime"/>.</summary>
public static class PluginRuntimes
{
    public const string Python = "python";

    public static readonly IReadOnlyList<string> Known = [Python];

    /// <summary>L'interpréteur configuré, sinon celui du PATH.</summary>
    public static string Interpreter(string runtime, PluginHostOptions options) =>
        options.Runtimes.TryGetValue(runtime, out var chemin) && !string.IsNullOrWhiteSpace(chemin)
            ? chemin
            : OperatingSystem.IsWindows() ? "python" : "python3";
}

/// <summary>Lance chaque plugin dans un vrai processus, qui ne doit pas survivre au cœur.</summary>
/// <remarks>
/// Deux garde-fous, parce qu'un plugin orphelin continue d'interroger ses services extérieurs et de frapper à la
/// porte du cœur suivant avec un jeton périmé :
/// <list type="bullet">
/// <item>Windows : chaque plugin entre dans un Job Object (<see cref="TravailWindows"/>) que le système ferme à la
/// mort du cœur, même brutale.</item>
/// <item>Partout : le PID du cœur part dans <c>Krissvia__ParentPid</c>, et les deux SDK arrêtent le plugin quand ce
/// processus disparaît. Sous Linux, systemd tue déjà le cgroup du service ; c'est la ceinture en plus.</item>
/// </list>
/// </remarks>
public sealed partial class ProcessPluginLauncher(PluginHostOptions options, ILogger<ProcessPluginLauncher>? logger = null) : IPluginLauncher, IDisposable
{
    /// <summary>Variable d'environnement portant le PID du cœur, lue par les SDK.</summary>
    public const string ParentPidVariable = "Krissvia__ParentPid";

    private readonly Lock _verrou = new();
    private object? _travail;   // TravailWindows, créé au premier lancement ; vit autant que le cœur
    private bool _travailImpossible;

    /// <summary>
    /// Comment lancer un plugin : son exécutable, ou l'interpréteur de son runtime avec le script en argument.
    /// </summary>
    public static ProcessStartInfo StartInfo(PluginManifest plugin, PluginHostOptions options, IReadOnlyDictionary<string, string> environnement)
    {
        var demarrage = new ProcessStartInfo(plugin.Runtime is { } runtime ? PluginRuntimes.Interpreter(runtime, options) : plugin.ExecutablePath)
        {
            WorkingDirectory = plugin.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (plugin.Runtime == PluginRuntimes.Python)
        {
            // -u : chaque ligne du plugin arrive aussitôt dans le journal de la page Services, pas par blocs.
            demarrage.ArgumentList.Add("-u");
            demarrage.ArgumentList.Add(plugin.ExecutablePath);
            // Le journal est lu en UTF-8 ; un Windows réglé en cp1252 écrirait sinon « Ã© » pour « é ».
            demarrage.Environment["PYTHONIOENCODING"] = "utf-8";
            // Pas de __pycache__ dans le dossier du plugin : il peut être en lecture seule, ou dans le dépôt.
            demarrage.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            var sdk = Path.Combine(options.SdkDirectory, "python");
            demarrage.Environment["PYTHONPATH"] = demarrage.Environment.TryGetValue("PYTHONPATH", out var existant) && !string.IsNullOrEmpty(existant)
                ? sdk + Path.PathSeparator + existant
                : sdk;
        }

        foreach (var (cle, valeur) in environnement) demarrage.Environment[cle] = valeur;
        // Après les réglages : aucun ne peut faire croire au plugin qu'il dépend d'un autre processus.
        demarrage.Environment[ParentPidVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        return demarrage;
    }

    public IPluginProcess Start(PluginManifest plugin, IReadOnlyDictionary<string, string> environnement, Action<string> ligne)
    {
        var demarrage = StartInfo(plugin, options, environnement);

        var processus = new Process { StartInfo = demarrage, EnableRaisingEvents = true };
        processus.OutputDataReceived += (_, e) => { if (e.Data is { } l) ligne(l); };
        processus.ErrorDataReceived += (_, e) => { if (e.Data is { } l) ligne(l); };
        processus.Start();
        if (OperatingSystem.IsWindows()) AttacherAuTravail(processus);
        processus.BeginOutputReadLine();
        processus.BeginErrorReadLine();
        return new Processus(processus);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void AttacherAuTravail(Process processus)
    {
        lock (_verrou)
        {
            if (_travail is null && !_travailImpossible)
            {
                try
                {
                    _travail = new TravailWindows();
                }
                catch (Win32Exception ex)
                {
                    _travailImpossible = true;   // un seul avertissement, pas un par lancement
                    if (logger is not null) TravailImpossible(logger, ex.Message);
                }
            }

            if (_travail is not TravailWindows travail) return;
            try
            {
                travail.Ajouter(processus);
            }
            catch (Win32Exception ex)
            {
                // Le plugin tourne quand même ; il ne survivrait simplement pas au cœur que grâce au SDK.
                if (logger is not null) AffectationImpossible(logger, processus.Id, ex.Message);
            }
        }
    }

    /// <summary>Arrêt du cœur : fermer le job tue les plugins qui y sont encore.</summary>
    public void Dispose()
    {
        lock (_verrou)
        {
            (_travail as IDisposable)?.Dispose();
            _travail = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job Object Windows impossible ({Erreur}) : un plugin pourra survivre à une mort brutale du cœur.")]
    private static partial void TravailImpossible(ILogger logger, string erreur);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processus {Pid} hors du Job Object ({Erreur}) : il pourra survivre à une mort brutale du cœur.")]
    private static partial void AffectationImpossible(ILogger logger, int pid, string erreur);

    private sealed class Processus(Process processus) : IPluginProcess
    {
        public int ProcessId { get; } = processus.Id;

        public Task<int> Exited { get; } = processus.WaitForExitAsync().ContinueWith(_ => processus.ExitCode, TaskScheduler.Default);

        public void Kill()
        {
            try
            {
                if (!processus.HasExited) processus.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Déjà terminé entre-temps.
            }
        }
    }
}
