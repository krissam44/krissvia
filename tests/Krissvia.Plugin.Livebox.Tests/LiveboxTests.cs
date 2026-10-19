using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.Livebox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.Livebox.Tests;

/// <summary>Lecture des réponses de la box, sans box : les formes vues selon les firmwares.</summary>
public class LiveboxParserTests
{
    [Fact]
    public void Les_appareils_sont_trouves_ou_qu_ils_soient_une_fois_par_adresse()
    {
        // Liste sous « status », un appareil imbriqué, une adresse en minuscules et tirets, un doublon.
        const string json = """
            {"status":[
              {"Key":"a","PhysAddress":"aa-bb-cc-dd-ee-01","Name":"Pixel de Christian","Active":true,"IPAddress":"192.168.1.20","DeviceType":"Mobile"},
              {"Key":"b","PhysAddress":"AA:BB:CC:DD:EE:02","HostName":"tablette","Active":false,
               "Children":[{"MACAddress":"AA:BB:CC:DD:EE:03","UserHostname":"imprimante","Active":true}]},
              {"Key":"c","PhysAddress":"AA:BB:CC:DD:EE:01","Name":"doublon","Active":false}
            ]}
            """;

        var appareils = LiveboxParser.Hosts(json);

        Assert.Equal(
        [
            new LiveboxHost("AA:BB:CC:DD:EE:01", "Pixel de Christian", true, "192.168.1.20", "Mobile"),
            new LiveboxHost("AA:BB:CC:DD:EE:02", "tablette", false, null, null),
            new LiveboxHost("AA:BB:CC:DD:EE:03", "imprimante", true, null, null),
        ], appareils);
    }

    [Fact]
    public void Authentification_et_session_expiree_se_lisent()
    {
        Assert.Equal("ctx-42", LiveboxParser.ContextId("""{"status":0,"data":{"contextID":"ctx-42","username":"admin"}}"""));
        Assert.Null(LiveboxParser.ContextId("""{"status":null,"errors":[{"error":13,"description":"Permission denied"}]}"""));
        Assert.Null(LiveboxParser.ContextId("pas du json"));
        Assert.True(LiveboxParser.IsPermissionDenied("""{"result":{"status":null,"errors":[{"error":13,"description":"Permission denied","info":""}]}}"""));
        Assert.False(LiveboxParser.IsPermissionDenied("""{"status":[{"Name":"Permission denied"}]}"""));
    }

    [Fact]
    public void La_connexion_se_lit_meme_incomplete()
    {
        const string wan = """{"result":{"status":true,"data":{"LinkType":"vdsl","LinkState":"up","IPAddress":"90.1.2.3","ConnectionState":"Bound"}}}""";
        const string dsl = """
            {"result":{"status":{"dsl":{"dsl0":{"DownstreamCurrRate":34200,"UpstreamCurrRate":5300,"DownstreamMaxRate":34900,"UpstreamMaxRate":5300,
            "DownstreamNoiseMargin":64,"UpstreamNoiseMargin":64,"DownstreamAttenuation":316,"UpstreamAttenuation":532,"CurrentProfile":"8b"}}}}}
            """;

        // Marges et atténuations en dixièmes de dB côté box : 64 → 6,4 dB, comme les affichait DomoMud.
        Assert.Equal(new WanStatus(true, "90.1.2.3", "vdsl", 34200, 5300)
        {
            DownMaxKbps = 34900, UpMaxKbps = 5300, NoiseMarginDown = 6.4, NoiseMarginUp = 6.4,
            AttenuationDown = 31.6, AttenuationUp = 53.2, Profile = "8b",
        }, LiveboxParser.Wan(wan, dsl));
        Assert.Equal(new WanStatus(true, "90.1.2.3", "vdsl", null, null), LiveboxParser.Wan(wan, null));   // fibre : pas de DSL
        Assert.Null(LiveboxParser.Wan("""{"result":{"data":{}}}""", null));
    }
}

/// <summary>La présence d'après la box : grâce, retour immédiat, box muette.</summary>
public class LiveboxPluginTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
    private const string Pixel = "AA:BB:CC:DD:EE:01";

    private sealed class FausseBox : ILiveboxApi
    {
        public List<LiveboxHost> Appareils { get; } = [];
        public Exception? Panne { get; set; }

        public Task<IReadOnlyList<LiveboxHost>> HostsAsync(CancellationToken ct) =>
            Panne is { } p ? Task.FromException<IReadOnlyList<LiveboxHost>>(p) : Task.FromResult<IReadOnlyList<LiveboxHost>>([.. Appareils]);

        public Task<WanStatus?> WanAsync(CancellationToken ct) => Task.FromResult<WanStatus?>(new WanStatus(true, "90.1.2.3", "vdsl", 50000, 8000));

        public void Connecter(bool actif) { Appareils.Clear(); Appareils.Add(new LiveboxHost(Pixel, "Pixel", actif, null, null)); }
    }

    private static (LiveboxPlugin Plugin, FausseBox Box, FakeTimeProvider Horloge) Banc()
    {
        var box = new FausseBox();
        var horloge = new FakeTimeProvider(T0);
        var plugin = new LiveboxPlugin([new TrackedDevice { Mac = "aa-bb-cc-dd-ee-01", Name = "Téléphone de Christian" }],
            box, horloge, NullLogger<LiveboxPlugin>.Instance, grace: TimeSpan.FromMinutes(20));
        return (plugin, box, horloge);
    }

    private static Dictionary<string, JsonElement> Valeurs(LiveboxPlugin plugin) =>
        plugin.Snapshot().OfType<StateBatch>().SelectMany(b => b.States).ToDictionary(s => s.EntityUniqueId, s => s.Value);

    private static bool Disponible(LiveboxPlugin plugin) => plugin.Snapshot().OfType<Availability>().Single().Online;

    private static readonly string Presence = LiveboxPlugin.PresenceId(Pixel);

    [Fact]
    public async Task Un_telephone_qui_quitte_le_wifi_reste_present_pendant_la_grace()
    {
        var (plugin, box, horloge) = Banc();
        box.Connecter(actif: true);
        await plugin.CycleAsync(default);
        Assert.True(Valeurs(plugin)[Presence].GetBoolean());

        box.Connecter(actif: false);   // veille profonde
        horloge.Advance(TimeSpan.FromMinutes(19));
        await plugin.CycleAsync(default);
        Assert.True(Valeurs(plugin)[Presence].GetBoolean());

        horloge.Advance(TimeSpan.FromMinutes(2));
        await plugin.CycleAsync(default);
        Assert.False(Valeurs(plugin)[Presence].GetBoolean());

        box.Connecter(actif: true);   // le retour compte aussitôt
        await plugin.CycleAsync(default);
        Assert.True(Valeurs(plugin)[Presence].GetBoolean());
    }

    [Fact]
    public async Task Un_telephone_jamais_vu_est_absent_et_l_ip_publique_est_publiee()
    {
        var (plugin, box, _) = Banc();
        await plugin.CycleAsync(default);

        var v = Valeurs(plugin);
        Assert.False(v[Presence].GetBoolean());
        Assert.Equal("90.1.2.3", v["ip"].GetString());
        Assert.True(v["internet"].GetBoolean());
        Assert.True(Disponible(plugin));
    }

    [Fact]
    public async Task Trois_releves_rates_rendent_tout_indisponible_et_le_premier_reussi_retablit()
    {
        var (plugin, box, _) = Banc();
        box.Connecter(actif: true);
        await plugin.CycleAsync(default);

        box.Panne = new HttpRequestException("hôte injoignable");
        await plugin.CycleAsync(default);
        await plugin.CycleAsync(default);
        Assert.True(Disponible(plugin));   // deux ratés : on patiente
        await plugin.CycleAsync(default);
        Assert.False(Disponible(plugin));

        box.Panne = null;
        await plugin.CycleAsync(default);
        Assert.True(Disponible(plugin));
    }

    [Fact]
    public async Task Un_mot_de_passe_refuse_compte_comme_un_releve_rate()
    {
        var (plugin, box, _) = Banc();
        await plugin.CycleAsync(default);
        box.Panne = new LiveboxAuthenticationException("authentification refusée");
        for (var i = 0; i < LiveboxPlugin.EchecsAvantIndisponible; i++) await plugin.CycleAsync(default);

        Assert.False(Disponible(plugin));
    }

    [Fact]
    public async Task Sans_supervision_la_presence_marche_seule_et_rien_de_la_connexion_n_est_declare()
    {
        var box = new FausseBox();
        box.Connecter(actif: true);
        var plugin = new LiveboxPlugin([new TrackedDevice { Mac = Pixel, Name = "Pixel" }], box, new FakeTimeProvider(T0),
            NullLogger<LiveboxPlugin>.Instance, TimeSpan.FromMinutes(20), supervision: false);

        await plugin.CycleAsync(default);

        Assert.DoesNotContain(plugin.Snapshot().OfType<Declare>().Single().Devices, d => d.UniqueId == LiveboxPlugin.BoxDeviceId);
        Assert.Equal([Presence, LiveboxPlugin.NetworkId], Valeurs(plugin).Keys.Order());
    }

    [Fact]
    public void La_connexion_est_declaree_avec_les_classes_standard_de_toute_box()
    {
        var (plugin, _, _) = Banc();

        var box = plugin.Snapshot().OfType<Declare>().Single().Devices.Single(d => d.UniqueId == LiveboxPlugin.BoxDeviceId);
        Assert.Equal([DeviceClasses.InternetConnection, DeviceClasses.Connectivity, DeviceClasses.PublicIp, DeviceClasses.DataRate, DeviceClasses.DataRate],
            box.Entities.Select(e => e.DeviceClass));
    }

    [Fact]
    public void Chaque_appareil_suivi_est_declare_avec_son_nom_et_la_classe_presence()
    {
        var (plugin, _, _) = Banc();

        var declare = plugin.Snapshot().OfType<Declare>().Single();
        var telephone = Assert.Single(declare.Devices, d => d.UniqueId == Presence);
        Assert.Equal("Téléphone de Christian", telephone.Name);
        Assert.Equal(DeviceClasses.Presence, Assert.Single(telephone.Entities).DeviceClass);
    }
}

/// <summary>La grille « Appareils du réseau » et le suivi qu'on y coche.</summary>
public class LiveboxReseauTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"krissvia-livebox-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    private sealed class Box : ILiveboxApi
    {
        public List<LiveboxHost> Appareils { get; } =
        [
            new("AA:BB:CC:DD:EE:01", "Pixel", true, "192.168.1.20", "Mobile"),
            new("AA:BB:CC:DD:EE:02", "iPhone", false, null, null),
        ];

        public Task<IReadOnlyList<LiveboxHost>> HostsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LiveboxHost>>([.. Appareils]);
        public Task<WanStatus?> WanAsync(CancellationToken ct) => Task.FromResult<WanStatus?>(null);
    }

    private string Fichier => Path.Combine(_dossier, "suivis.json");

    private LiveboxPlugin Plugin(IReadOnlyList<TrackedDevice>? depart = null) =>
        new(depart ?? [], new Box(), new FakeTimeProvider(T0), NullLogger<LiveboxPlugin>.Instance, TimeSpan.FromMinutes(20),
            supervision: false, stockage: new TrackingStore(Fichier));

    private static CommandRequest Suivre(string mac, bool oui) => new()
    {
        Id = "c1", EntityUniqueId = LiveboxPlugin.NetworkId, Command = LiveboxPlugin.SetTracked,
        Args = JsonSerializer.SerializeToElement(new { mac, tracked = oui }),
    };

    private static JsonElement Hotes(LiveboxPlugin p) =>
        p.Snapshot().OfType<StateBatch>().SelectMany(b => b.States).Single(s => s.EntityUniqueId == LiveboxPlugin.NetworkId).Value.GetProperty("hosts");

    [Fact]
    public async Task La_grille_liste_tout_ce_que_voit_la_box_et_ce_qui_est_suivi()
    {
        var p = Plugin([new TrackedDevice { Mac = "AA:BB:CC:DD:EE:09", Name = "Vieux téléphone" }]);
        await p.CycleAsync(default);

        var hotes = Hotes(p).EnumerateArray().ToList();
        Assert.Equal(["Pixel", "iPhone", "Vieux téléphone"], hotes.Select(h => h.GetProperty("name").GetString()));
        Assert.Equal([false, false, true], hotes.Select(h => h.GetProperty("tracked").GetBoolean()));   // le suivi absent figure aussi
        Assert.Equal("192.168.1.20", hotes[0].GetProperty("ip").GetString());
    }

    [Fact]
    public async Task Cocher_un_appareil_le_suit_aussitot_et_le_garde_apres_un_redemarrage()
    {
        var p = Plugin();
        await p.CycleAsync(default);

        var r = await p.ExecuteAsync(Suivre("aa-bb-cc-dd-ee-01", true), default);

        Assert.Equal(CommandOutcome.Accepted, r.Outcome);
        var presence = LiveboxPlugin.PresenceId("AA:BB:CC:DD:EE:01");
        var appareil = Assert.Single(p.Snapshot().OfType<Declare>().Single().Devices, d => d.UniqueId == presence);
        Assert.Equal("Pixel", appareil.Name);   // le nom que lui donne la box
        Assert.True(p.Snapshot().OfType<StateBatch>().SelectMany(b => b.States).Single(s => s.EntityUniqueId == presence).Value.GetBoolean());

        // Un plugin neuf (redémarrage) relit le fichier, et ne repart pas des réglages.
        var apres = Plugin([new TrackedDevice { Mac = "AA:BB:CC:DD:EE:02", Name = "ignoré" }]);
        Assert.Contains(apres.Snapshot().OfType<Declare>().Single().Devices, d => d.UniqueId == presence);
        Assert.DoesNotContain(apres.Snapshot().OfType<Declare>().Single().Devices, d => d.UniqueId == LiveboxPlugin.PresenceId("AA:BB:CC:DD:EE:02"));
    }

    [Fact]
    public async Task Decocher_retire_l_appareil_et_une_commande_mal_formee_est_refusee()
    {
        var p = Plugin([new TrackedDevice { Mac = "AA:BB:CC:DD:EE:01", Name = "Pixel" }]);
        await p.CycleAsync(default);

        await p.ExecuteAsync(Suivre("AA:BB:CC:DD:EE:01", false), default);

        Assert.DoesNotContain(p.Snapshot().OfType<Declare>().Single().Devices, d => d.UniqueId == LiveboxPlugin.PresenceId("AA:BB:CC:DD:EE:01"));
        Assert.Equal("[]", File.ReadAllText(Fichier).Trim());
        var refus = await p.ExecuteAsync(new CommandRequest { Id = "c2", EntityUniqueId = LiveboxPlugin.NetworkId, Command = LiveboxPlugin.SetTracked }, default);
        Assert.Equal(CommandOutcome.Failed, refus.Outcome);
    }
}
