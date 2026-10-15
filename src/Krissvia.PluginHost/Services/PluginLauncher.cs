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

public sealed class ProcessPluginLauncher : IPluginLauncher
{
    public IPluginProcess Start(PluginManifest plugin, IReadOnlyDictionary<string, string> environnement, Action<string> ligne)
    {
        var demarrage = new ProcessStartInfo(plugin.ExecutablePath)
        {
            WorkingDirectory = plugin.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (cle, valeur) in environnement) demarrage.Environment[cle] = valeur;

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
