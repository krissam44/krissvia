using System.Diagnostics;
using System.Text;

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

public sealed class ProcessPluginLauncher(PluginHostOptions options) : IPluginLauncher
{
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
        return demarrage;
    }

    public IPluginProcess Start(PluginManifest plugin, IReadOnlyDictionary<string, string> environnement, Action<string> ligne)
    {
        var demarrage = StartInfo(plugin, options, environnement);

        var processus = new Process { StartInfo = demarrage, EnableRaisingEvents = true };
        processus.OutputDataReceived += (_, e) => { if (e.Data is { } l) ligne(l); };
        processus.ErrorDataReceived += (_, e) => { if (e.Data is { } l) ligne(l); };
        processus.Start();
        processus.BeginOutputReadLine();
        processus.BeginErrorReadLine();
        return new Processus(processus);
    }

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
