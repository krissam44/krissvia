using System.Collections.Concurrent;
using Krissvia.Core.Configuration;
using Krissvia.PluginHost.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Le superviseur de la page Services, sur le vrai hub. Seul le lancement des processus est simulé : il quitte la
/// machine de test.
/// </summary>
public class SuperviseurTests
{
    private sealed class MagasinEnMemoire : IConfigurationStore
    {
        public ConcurrentDictionary<(string, string), string> Documents { get; } = new();

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(Documents.Where(kv => kv.Key.Item1 == collection)
                .Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) =>
            Task.FromResult(Documents.TryRemove((collection, id), out _));
    }

    internal sealed class FauxProcessus(int pid) : IPluginProcess
    {
        private readonly TaskCompletionSource<int> _sortie = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => pid;
        public Task<int> Exited => _sortie.Task;
        public bool Tue { get; private set; }

        public void Kill()
        {
            Tue = true;
            _sortie.TrySetResult(-1);
        }

        public void Planter(int code = 1) => _sortie.TrySetResult(code);
    }

    internal sealed class FauxLanceur : IPluginLauncher
    {
        private int _pid = 1000;
        public ConcurrentQueue<(PluginManifest Plugin, IReadOnlyDictionary<string, string> Env, FauxProcessus Processus)> Lancements { get; } = new();
        public bool Echoue { get; set; }

        public IPluginProcess Start(PluginManifest plugin, IReadOnlyDictionary<string, string> environnement, Action<string> ligne)
        {
            if (Echoue) throw new System.ComponentModel.Win32Exception("exécutable introuvable");
            var p = new FauxProcessus(Interlocked.Increment(ref _pid));
            Lancements.Enqueue((plugin, environnement, p));
            ligne("info: démarré");
            return p;
        }

        public FauxProcessus Dernier => Lancements.Last().Processus;
    }

    private static readonly PluginManifest Shelly = new() { Id = "shelly", Name = "Shelly", Executable = "Krissvia.Plugin.Shelly", Directory = "/plugins/shelly" };
    private static readonly PluginManifest Z2m = new() { Id = "zigbee2mqtt", Name = "Zigbee2MQTT", Executable = "Krissvia.Plugin.Zigbee2Mqtt" };

    private static async Task<(PluginSupervisor Superviseur, FauxLanceur Lanceur, MagasinEnMemoire Magasin, BancPlugins Banc)> MonterAsync(
        MagasinEnMemoire? magasin = null, BancPlugins? banc = null)
    {
        banc ??= await BancPlugins.CreerAsync();
        magasin ??= new MagasinEnMemoire();
        var lanceur = new FauxLanceur();
        var superviseur = new PluginSupervisor(magasin, banc.Hub, banc.Options, lanceur, banc.Horloge, NullLogger<PluginSupervisor>.Instance)
        {
            CoreUrl = "ws://127.0.0.1:12795/plugins",
        };
        await superviseur.LoadAsync([Shelly, Z2m]);
        return (superviseur, lanceur, magasin, banc);
    }

    private static ServiceStatus Statut(PluginSupervisor s, string id) => s.GetSnapshot(id)!.Status;

    [Fact]
    public async Task Par_defaut_rien_ne_demarre()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;

        await s.StartEnabledAsync();

        Assert.Empty(lanceur.Lancements);
        Assert.Equal(ServiceStatus.Stopped, Statut(s, "shelly"));
    }

    [Fact]
    public async Task Activer_lance_le_plugin_avec_un_jeton_neuf_inscrit_aupres_du_hub()
    {
        var (s, lanceur, magasin, banc) = await MonterAsync();
        await using var _ = banc;

        Assert.Null(await s.SetEnabledAsync("shelly", true));

        var (plugin, env, _) = Assert.Single(lanceur.Lancements);
        Assert.Equal("shelly", plugin.Id);
        Assert.Equal("ws://127.0.0.1:12795/plugins", env["Krissvia__Url"]);
        Assert.Equal("shelly", env["Krissvia__PluginId"]);
        Assert.Equal(env["Krissvia__Token"], banc.Options.Tokens["shelly"]);
        Assert.Equal(48, env["Krissvia__Token"].Length);
        Assert.Equal(ServiceStatus.Starting, Statut(s, "shelly"));
        Assert.Contains("\"enabled\":true", magasin.Documents[(PluginSupervisor.Collection, "shelly")]);
        Assert.Contains("info: démarré", s.GetSnapshot("shelly")!.Logs);
        Assert.False(env.ContainsKey("Krissvia__DataDirectory"));   // aucun dossier de données réglé
    }

    [Fact]
    public async Task Chaque_plugin_recoit_son_dossier_de_donnees_cree_a_l_avance()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _b = banc;
        var racine = Path.Combine(Path.GetTempPath(), $"krissvia-donnees-{Guid.NewGuid():N}");
        banc.Options.DataDirectory = racine;
        try
        {
            await s.SetEnabledAsync("shelly", true);

            var (_, env, _) = Assert.Single(lanceur.Lancements);
            Assert.Equal(Path.Combine(racine, "shelly"), env["Krissvia__DataDirectory"]);
            Assert.True(Directory.Exists(Path.Combine(racine, "shelly")));
        }
        finally
        {
            if (Directory.Exists(racine)) Directory.Delete(racine, recursive: true);
        }
    }

    [Fact]
    public async Task Un_plugin_active_redemarre_avec_le_coeur()
    {
        var (avant, _, magasin, banc) = await MonterAsync();
        await using var _ = banc;
        await avant.SetEnabledAsync("shelly", true);
        avant.StopAll();

        var (apres, lanceur, _, _) = await MonterAsync(magasin, banc);
        await apres.StartEnabledAsync();

        Assert.Equal("shelly", Assert.Single(lanceur.Lancements).Plugin.Id);
    }

    [Fact]
    public async Task Desactiver_arrete_le_processus_retire_le_jeton_et_ne_relance_pas()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;
        await s.SetEnabledAsync("shelly", true);
        var processus = lanceur.Dernier;

        await s.SetEnabledAsync("shelly", false);
        await banc.AvancerAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));

        Assert.True(processus.Tue);
        Assert.False(banc.Options.Tokens.ContainsKey("shelly"));
        Assert.Single(lanceur.Lancements);
        Assert.Equal(ServiceStatus.Stopped, Statut(s, "shelly"));
    }

    [Fact]
    public async Task Redemarrer_arrete_l_ancien_processus_et_en_lance_un_nouveau_avec_un_autre_jeton()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;
        await s.SetEnabledAsync("shelly", true);
        var premier = lanceur.Lancements.Last();

        Assert.Null(await s.RestartAsync("shelly"));

        Assert.True(premier.Processus.Tue);
        Assert.Equal(2, lanceur.Lancements.Count);
        Assert.NotEqual(premier.Env["Krissvia__Token"], banc.Options.Tokens["shelly"]);
        await banc.AvancerAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.Equal(2, lanceur.Lancements.Count);   // l'arrêt volontaire n'est pas pris pour un plantage
    }

    [Fact]
    public async Task Un_plugin_qui_plante_est_relance_de_plus_en_plus_tard()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;
        await s.SetEnabledAsync("shelly", true);

        lanceur.Dernier.Planter();
        await BancPlugins.AttendreAsync(() => Statut(s, "shelly") == ServiceStatus.Restarting);
        Assert.Contains("code 1", s.GetSnapshot("shelly")!.LastExit);

        await banc.AvancerAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.Single(lanceur.Lancements);
        await banc.AvancerAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        await BancPlugins.AttendreAsync(() => lanceur.Lancements.Count == 2);

        lanceur.Dernier.Planter();   // replante aussitôt : l'attente passe à 5 s
        await BancPlugins.AttendreAsync(() => Statut(s, "shelly") == ServiceStatus.Restarting);
        await banc.AvancerAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        Assert.Equal(2, lanceur.Lancements.Count);
        await banc.AvancerAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        await BancPlugins.AttendreAsync(() => lanceur.Lancements.Count == 3);
        Assert.Equal(2, s.GetSnapshot("shelly")!.Restarts);
    }

    [Fact]
    public async Task Activer_ou_desactiver_previent_le_menu_mais_pas_un_refus()
    {
        var (s, _, _, banc) = await MonterAsync();
        await using var _ = banc;
        var annonces = 0;
        s.EnabledChanged += () => annonces++;

        await s.SetEnabledAsync("shelly", true);
        await s.SetEnabledAsync("shelly", false);
        Assert.NotNull(await s.SetEnabledAsync("absent", true));

        Assert.Equal(2, annonces);
    }

    [Fact]
    public async Task Un_plugin_deja_connecte_depuis_ailleurs_n_est_pas_lance()
    {
        var banc = await BancPlugins.CreerAsync();
        await using var _ = banc;
        await banc.ConnecterAsync("zigbee2mqtt");   // lancé depuis Visual Studio, par exemple
        var (s, lanceur, _, _) = await MonterAsync(banc: banc);

        Assert.Equal(ServiceStatus.External, Statut(s, "zigbee2mqtt"));
        var refus = await s.SetEnabledAsync("zigbee2mqtt", true);

        Assert.Contains("déjà connecté", refus);
        Assert.Empty(lanceur.Lancements);
    }

    [Fact]
    public async Task Un_plugin_lance_et_connecte_est_demarre_avec_sa_version()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;
        await s.SetEnabledAsync("shelly", true);
        var jeton = lanceur.Lancements.Last().Env["Krissvia__Token"];

        await banc.ConnecterAsync("shelly", jeton);

        Assert.Equal(ServiceStatus.Connected, Statut(s, "shelly"));
        Assert.Equal("0.1.0", s.GetSnapshot("shelly")!.PluginVersion);
    }

    [Fact]
    public async Task Un_plugin_lance_qui_ne_se_connecte_pas_est_signale()
    {
        var (s, _, _, banc) = await MonterAsync();
        await using var _ = banc;
        await s.SetEnabledAsync("shelly", true);

        await banc.AvancerAsync(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5));

        Assert.Equal(ServiceStatus.NotConnected, Statut(s, "shelly"));
    }

    [Fact]
    public async Task Un_plugin_non_installe_ne_peut_pas_etre_active()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;

        Assert.Contains("pas installé", await s.SetEnabledAsync("teleinfo", true));
        Assert.Empty(lanceur.Lancements);
    }

    [Fact]
    public async Task Un_lancement_impossible_est_retente()
    {
        var (s, lanceur, _, banc) = await MonterAsync();
        await using var _ = banc;
        lanceur.Echoue = true;

        await s.SetEnabledAsync("shelly", true);
        Assert.Equal(ServiceStatus.Restarting, Statut(s, "shelly"));
        Assert.Contains(s.GetSnapshot("shelly")!.Logs, l => l.Contains("lancement impossible"));

        lanceur.Echoue = false;
        await banc.AvancerAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        await BancPlugins.AttendreAsync(() => lanceur.Lancements.Count == 1);
    }
}
