using System.Net;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.Proxmox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.Proxmox.Tests;

/// <summary>Lecture de <c>cluster/resources</c> et forme des requêtes, sans Proxmox.</summary>
public class ProxmoxApiTests
{
    [Fact]
    public void Les_noeuds_et_les_invites_se_lisent_sans_les_modeles()
    {
        const string json = """
            {"data":[
              {"type":"node","node":"pve0","status":"online","cpu":0.12,"maxcpu":8,"mem":4000,"maxmem":16000,"disk":1,"maxdisk":2,"uptime":3600},
              {"type":"qemu","vmid":101,"node":"pve0","name":"domomud","status":"running","cpu":0.05,"maxcpu":2,"mem":1000,"maxmem":2000,"disk":0,"maxdisk":32000,"uptime":7200},
              {"type":"lxc","vmid":200,"node":"pve0","name":"mosquitto","status":"stopped"},
              {"type":"qemu","vmid":9000,"node":"pve0","name":"modele-debian","status":"stopped","template":1},
              {"type":"storage","storage":"local","node":"pve0"}
            ]}
            """;

        var r = ProxmoxParser.Resources(json);

        Assert.Equal(new ProxmoxNode("pve0", true, 0.12, 8, 4000, 16000, 1, 2, 3600), Assert.Single(r.Nodes));
        Assert.Equal([101, 200], r.Guests.Select(g => g.VmId));
        Assert.True(r.Guests[0].Running);
        Assert.Equal("LXC", r.Guests[1].TypeLabel);
        Assert.False(r.NoRights);
        Assert.True(ProxmoxParser.Resources("""{"data":[]}""").NoRights);
        Assert.Throws<JsonException>(() => ProxmoxParser.Resources("""{"errors":"x"}"""));
    }

    private sealed class Enregistreur(HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requetes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requetes.Add(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("""{"data":[]}""") });
        }
    }

    [Fact]
    public async Task Le_jeton_part_sans_espace_et_les_ordres_visent_la_bonne_machine()
    {
        var h = new Enregistreur();
        using var client = new ProxmoxHttpClient("10.0.0.5", 8006, "root@pam!krissvia", "abc-123", verifyTls: false, h);

        await client.ResourcesAsync(CancellationToken.None);
        await client.ActionAsync(new ProxmoxGuest(200, "pve 0", "lxc", "mosquitto", "running", 0, 1, 0, 0, 0, 0, 0), "shutdown", CancellationToken.None);

        // « PVEAPIToken=id=secret » : avec une espace (AuthenticationHeaderValue), Proxmox répond 401.
        Assert.Equal(["PVEAPIToken=root@pam!krissvia=abc-123"], h.Requetes[0].Headers.GetValues("Authorization"));
        Assert.Equal("https://10.0.0.5:8006/api2/json/cluster/resources", h.Requetes[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, h.Requetes[1].Method);
        Assert.Equal("/api2/json/nodes/pve%200/lxc/200/status/shutdown", h.Requetes[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Un_jeton_refuse_se_distingue_d_une_panne()
    {
        using var client = new ProxmoxHttpClient("10.0.0.5", 8006, "x", "y", false, new Enregistreur(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<ProxmoxAuthenticationException>(() => client.ResourcesAsync(CancellationToken.None));
    }
}

/// <summary>Les machines vues par le plugin : arrêt inattendu, arrêt demandé, relance automatique et son garde-fou.</summary>
public class ProxmoxPluginTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FauxProxmox : IProxmoxApi
    {
        public Dictionary<int, string> Etats { get; } = new() { [101] = "running", [200] = "running" };
        public List<(int VmId, string Action)> Actions { get; } = [];
        public Exception? Panne { get; set; }
        public bool ActionRefusee { get; set; }
        public bool NoeudEnLigne { get; set; } = true;

        public Task<ProxmoxSnapshot> ResourcesAsync(CancellationToken ct) => Panne is { } p
            ? Task.FromException<ProxmoxSnapshot>(p)
            : Task.FromResult(new ProxmoxSnapshot([new ProxmoxNode("pve0", NoeudEnLigne, 0.1, 8, 1, 2, 3, 4, 10)],
                [.. Etats.Select(kv => new ProxmoxGuest(kv.Key, "pve0", kv.Key < 200 ? "qemu" : "lxc", $"machine{kv.Key}", kv.Value, 0.1, 2, 1, 2, 0, 8, 60))], false));

        public Task ActionAsync(ProxmoxGuest guest, string action, CancellationToken ct)
        {
            if (ActionRefusee) return Task.FromException(new HttpRequestException("injoignable"));
            Actions.Add((guest.VmId, action));
            return Task.CompletedTask;
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FauxProxmox Api { get; } = new();
        public string Fichier { get; } = Path.Combine(Path.GetTempPath(), $"krissvia-proxmox-{Guid.NewGuid():N}.json");
        public ProxmoxPlugin Plugin { get; private set; }

        public Banc() => Plugin = Nouveau();

        public ProxmoxPlugin Nouveau() => Plugin = new ProxmoxPlugin(Api, Horloge, NullLogger<ProxmoxPlugin>.Instance, preferences: new PreferenceStore(Fichier));

        public async Task ReleveAsync(double minutes = 1)
        {
            Horloge.Advance(TimeSpan.FromMinutes(minutes));
            await Plugin.CycleAsync(CancellationToken.None);
        }

        public bool Probleme(int vmid) => Valeur(ProxmoxPlugin.StopId(vmid)).GetBoolean();

        public JsonElement Valeur(string id) =>
            Plugin.Snapshot().OfType<StateBatch>().Single().States.Single(s => s.EntityUniqueId == id).Value;

        public Task<CommandResult> OrdreAsync(int vmid, string commande, object? args = null) =>
            Plugin.ExecuteAsync(new CommandRequest
            {
                Id = "o1", EntityUniqueId = ProxmoxPlugin.GuestId(vmid), Command = commande,
                Args = args is null ? null : JsonSerializer.SerializeToElement(args),
            }, CancellationToken.None);
    }

    [Fact]
    public async Task Seul_un_passage_en_marche_vers_arrete_est_un_arret_inattendu()
    {
        var b = new Banc();
        b.Api.Etats[300] = "stopped";   // déjà arrêtée au premier relevé : une machine de réserve, pas une panne
        await b.ReleveAsync();
        await b.ReleveAsync();          // toujours arrêtée : ni au premier relevé, ni aux suivants
        Assert.False(b.Probleme(300));

        b.Api.Etats[101] = "stopped";
        await b.ReleveAsync();
        Assert.True(b.Probleme(101));
        Assert.True(b.Valeur(ProxmoxPlugin.GuestId(101)).GetProperty("unexpected_stop").GetBoolean());

        b.Api.Etats[101] = "running";
        await b.ReleveAsync();
        Assert.False(b.Probleme(101));
    }

    [Fact]
    public async Task Un_arret_demande_depuis_krissvia_n_est_pas_une_panne()
    {
        var b = new Banc();
        await b.ReleveAsync();

        var r = await b.OrdreAsync(101, ProxmoxPlugin.Shutdown);
        b.Api.Etats[101] = "stopped";
        await b.ReleveAsync();

        Assert.Equal(CommandOutcome.Accepted, r.Outcome);
        Assert.Equal([(101, "shutdown")], b.Api.Actions);
        Assert.False(b.Probleme(101));
    }

    [Fact]
    public async Task Une_machine_non_surveillee_peut_s_arreter_sans_alerte()
    {
        var b = new Banc();
        await b.ReleveAsync();
        await b.OrdreAsync(101, ProxmoxPlugin.SetTracked, new { enabled = false });

        b.Api.Etats[101] = "stopped";
        await b.ReleveAsync();

        Assert.False(b.Probleme(101));
    }

    [Fact]
    public async Task La_relance_automatique_s_arrete_au_bout_de_trois_et_l_alerte_part()
    {
        var b = new Banc();
        await b.ReleveAsync();
        await b.OrdreAsync(200, ProxmoxPlugin.SetAutoRestart, new { enabled = true });

        for (var i = 1; i <= 4; i++)
        {
            b.Api.Etats[200] = "stopped";
            await b.ReleveAsync();
            if (i <= 3)
            {
                Assert.False(b.Probleme(200));   // relancée : pas de panne à dire
                b.Api.Etats[200] = "running";
                await b.ReleveAsync();
            }
        }

        Assert.Equal(3, b.Api.Actions.Count(a => a == (200, "start")));
        Assert.True(b.Probleme(200));   // la quatrième fois en 30 min : un vrai plantage

        // Hors de la fenêtre, les relances reprennent.
        b.Api.Etats[200] = "running";
        await b.ReleveAsync(31);
        b.Api.Etats[200] = "stopped";
        await b.ReleveAsync();
        Assert.Equal(4, b.Api.Actions.Count(a => a == (200, "start")));
    }

    [Fact]
    public async Task Une_relance_sans_effet_ou_impossible_se_dit()
    {
        var b = new Banc();
        await b.ReleveAsync();
        await b.OrdreAsync(200, ProxmoxPlugin.SetAutoRestart, new { enabled = true });

        b.Api.Etats[200] = "stopped";
        await b.ReleveAsync();
        Assert.False(b.Probleme(200));
        await b.ReleveAsync();             // toujours arrêtée après la relance
        Assert.True(b.Probleme(200));

        var c = new Banc();
        await c.ReleveAsync();
        await c.OrdreAsync(200, ProxmoxPlugin.SetAutoRestart, new { enabled = true });
        c.Api.ActionRefusee = true;
        c.Api.Etats[200] = "stopped";
        await c.ReleveAsync();
        Assert.True(c.Probleme(200));       // la relance n'a pas pu partir
    }

    [Fact]
    public async Task Les_preferences_survivent_a_un_redemarrage_du_plugin()
    {
        var b = new Banc();
        await b.ReleveAsync();
        await b.OrdreAsync(101, ProxmoxPlugin.SetTracked, new { enabled = false });
        await b.OrdreAsync(200, ProxmoxPlugin.SetAutoRestart, new { enabled = true });

        b.Nouveau();
        await b.ReleveAsync();

        Assert.False(b.Valeur(ProxmoxPlugin.GuestId(101)).GetProperty("tracked").GetBoolean());
        Assert.True(b.Valeur(ProxmoxPlugin.GuestId(200)).GetProperty("auto_restart").GetBoolean());
        File.Delete(b.Fichier);
    }

    [Fact]
    public async Task Les_machines_sont_declarees_avec_leurs_commandes_et_leur_probleme()
    {
        var b = new Banc();
        await b.ReleveAsync();

        var declaration = b.Plugin.Snapshot().OfType<Declare>().Single();
        var vm = declaration.Devices.Single(d => d.UniqueId == "vm101");
        Assert.Equal(DeviceClasses.VirtualMachine, vm.Entities[0].DeviceClass);
        Assert.Contains(ProxmoxPlugin.Reboot, vm.Entities[0].Commands);
        Assert.Equal(DeviceClasses.Problem, vm.Entities[1].DeviceClass);
        Assert.Contains(declaration.Devices, d => d.UniqueId == "noeud:pve0" && d.Entities.Any(e => e.DeviceClass == DeviceClasses.Problem));
    }

    [Fact]
    public async Task Le_noeud_publie_processeur_memoire_et_disque_systeme_comme_l_en_tete_de_domomud()
    {
        var b = new Banc();
        b.Api.Etats[101] = "stopped";
        await b.ReleveAsync();

        var noeud = b.Valeur("noeud:pve0");
        Assert.Equal((10.0, 8, 50.0, 75.0, 3L, 4L), (noeud.GetProperty("cpu_percent").GetDouble(), noeud.GetProperty("max_cpu").GetInt32(),
            noeud.GetProperty("memory_percent").GetDouble(), noeud.GetProperty("disk_percent").GetDouble(),
            noeud.GetProperty("disk_bytes").GetInt64(), noeud.GetProperty("max_disk_bytes").GetInt64()));
        Assert.Equal((1, 2), (noeud.GetProperty("guests_running").GetInt32(), noeud.GetProperty("guests_total").GetInt32()));
        Assert.Equal(75.0, b.Valeur("noeud:pve0:disque").GetDouble());
        var declaration = b.Plugin.Snapshot().OfType<Declare>().Single().Devices.Single(d => d.UniqueId == "noeud:pve0");
        Assert.Contains(declaration.Entities, e => e.UniqueId == "noeud:pve0" && e.DeviceClass == DeviceClasses.HypervisorNode);
    }

    [Fact]
    public async Task Un_noeud_hors_ligne_n_a_pas_de_mesure_et_non_zero()
    {
        var b = new Banc();
        b.Api.NoeudEnLigne = false;
        await b.ReleveAsync();

        var noeud = b.Valeur("noeud:pve0");
        Assert.False(noeud.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Null, noeud.GetProperty("cpu_percent").ValueKind);
        Assert.Equal(JsonValueKind.Null, noeud.GetProperty("disk_percent").ValueKind);
    }

    [Fact]
    public async Task Trois_releves_rates_rendent_tout_indisponible()
    {
        var b = new Banc();
        await b.ReleveAsync();
        b.Api.Panne = new HttpRequestException("injoignable");

        await b.ReleveAsync();
        await b.ReleveAsync();
        Assert.True(b.Plugin.Snapshot().OfType<Availability>().Single().Online);
        await b.ReleveAsync();

        Assert.False(b.Plugin.Snapshot().OfType<Availability>().Single().Online);
    }
}
