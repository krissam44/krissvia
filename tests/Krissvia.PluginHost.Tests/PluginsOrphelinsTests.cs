using System.Diagnostics;
using Krissvia.PluginHost.Services;
using Krissvia.Sdk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Un plugin lancé par le cœur ne lui survit pas : le Job Object côté Windows, le PID du parent côté SDK.
/// </summary>
public sealed class PluginsOrphelinsTests
{
    private static readonly PluginManifest Fiche = new() { Id = "meteo", Name = "Météo", Runtime = "python", Executable = "meteo.py", Directory = Path.GetTempPath() };

    [Fact]
    public void Le_plugin_recoit_le_pid_du_coeur_et_aucun_reglage_ne_peut_le_changer()
    {
        var demarrage = ProcessPluginLauncher.StartInfo(Fiche, new PluginHostOptions(),
            new Dictionary<string, string> { [ProcessPluginLauncher.ParentPidVariable] = "1" });

        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), demarrage.Environment["Krissvia__ParentPid"]);
    }

    [Fact]
    public void Fermer_le_job_tue_les_processus_qui_y_sont()
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux : c'est le cgroup de systemd, et le SDK

        using var processus = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var travail = new TravailWindows();
            travail.Ajouter(processus);
            Assert.False(processus.WaitForExit(300));

            travail.Dispose();   // ce que fait Windows à la mort du cœur, même brutale

            Assert.True(processus.WaitForExit(5000), "le processus a survécu à la fermeture du job");
        }
        finally
        {
            if (!processus.HasExited) processus.Kill();
        }
    }

    [Fact]
    public async Task Un_plugin_lance_meurt_avec_le_lanceur()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dossier = Path.Combine(Path.GetTempPath(), $"krissvia-orphelin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "attente.py"), "import time; time.sleep(60)");
        var fiche = new PluginManifest { Id = "attente", Name = "Attente", Runtime = "python", Executable = "attente.py", Directory = dossier };
        var lanceur = new ProcessPluginLauncher(new PluginHostOptions());
        try
        {
            IPluginProcess processus;
            try
            {
                processus = lanceur.Start(fiche, new Dictionary<string, string>(), _ => { });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;   // pas de Python sur cette machine
            }

            await Task.Delay(300);
            Assert.False(processus.Exited.IsCompleted);

            lanceur.Dispose();   // le handle du job se ferme, comme à la mort du cœur

            await processus.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lanceur.Dispose();
            Directory.Delete(dossier, recursive: true);
        }
    }

    // ── SDK C# ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Le_sdk_rend_la_main_des_que_le_parent_a_disparu()
    {
        var verifications = 0;
        bool Vivant(int pid) => ++verifications < 3;

        var disparu = await ParentProcessWatch.WaitForExitAsync(42, TimeSpan.FromMilliseconds(1), TimeProvider.System, Vivant, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(disparu);
        Assert.Equal(3, verifications);
    }

    [Fact]
    public async Task Tant_que_le_parent_vit_le_plugin_continue()
    {
        using var fin = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var disparu = await ParentProcessWatch.WaitForExitAsync(42, TimeSpan.FromMilliseconds(5), TimeProvider.System, _ => true, fin.Token);

        Assert.False(disparu);
    }

    [Fact]
    public async Task Un_parent_disparu_arrete_le_plugin()
    {
        var vie = new FausseVie();
        var surveillance = new ParentProcessWatch(new CoreClientOptions { ParentPid = 42, ParentCheckPeriod = TimeSpan.FromMilliseconds(1) },
            vie, TimeProvider.System, NullLogger<ParentProcessWatch>.Instance, _ => false);

        await surveillance.StartAsync(CancellationToken.None);
        await surveillance.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vie.Arrete);
    }

    [Fact]
    public async Task Un_plugin_lance_a_la_main_ne_surveille_rien()
    {
        var vie = new FausseVie();
        var interroge = false;
        var surveillance = new ParentProcessWatch(new CoreClientOptions(), vie, TimeProvider.System, NullLogger<ParentProcessWatch>.Instance,
            _ => interroge = true);

        await surveillance.StartAsync(CancellationToken.None);
        await surveillance.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(interroge);
        Assert.False(vie.Arrete);
    }

    [Fact]
    public void Un_processus_vivant_est_reconnu_et_un_processus_sorti_ne_l_est_plus()
    {
        Assert.True(ParentProcessWatch.IsAlive(Environment.ProcessId));

        using var bref = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("true") { UseShellExecute = false })!;
        var pid = bref.Id;
        bref.WaitForExit();

        Assert.False(ParentProcessWatch.IsAlive(pid));
    }

    private sealed class FausseVie : IHostApplicationLifetime
    {
        public bool Arrete { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Arrete = true;
    }
}
