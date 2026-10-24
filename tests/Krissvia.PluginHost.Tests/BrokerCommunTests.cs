using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.PluginHost.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Le broker MQTT commun (pastille de la barre du haut) : saisi une fois, transmis aux plugins qui le déclarent, et l'état
/// de leur lien remonté au cœur.
/// </summary>
public class BrokerCommunTests
{
    private sealed class Magasin : IConfigurationStore
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

    private static PluginManifest Fiche(string projet)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Krissvia.slnx"))) dir = dir.Parent;
        var json = File.ReadAllText(Path.Combine(dir!.FullName, "plugins", projet, "plugin.json"));
        return JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private static readonly SharedBroker Commun = new() { Host = "192.168.1.60", Port = 1884, Username = "krissvia", Password = "secret-essai" };

    // ── Réglages ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Krissvia.Plugin.Zigbee2Mqtt")]
    [InlineData("Krissvia.Plugin.OpenHasp")]
    [InlineData("Krissvia.Plugin.Teleinfo")]
    [InlineData("Krissvia.Plugin.MqttDiscovery")]
    public void Les_plugins_mqtt_declarent_leur_section_mqtt_partagee(string projet)
    {
        Assert.Equal([("Mqtt", "mqtt")], SettingsSchema.SharedSections(SettingsSchema.Parse(Fiche(projet).Settings)));
    }

    [Fact]
    public void Dans_une_section_partagee_l_adresse_n_est_plus_obligatoire()
    {
        var schema = SettingsSchema.Parse(Fiche("Krissvia.Plugin.Zigbee2Mqtt").Settings)!;

        Assert.Empty(SettingsSchema.Validate(schema, SettingsSchema.Complete(schema, new JsonObject())));
    }

    [Fact]
    public void Le_broker_commun_remplace_adresse_port_et_identifiants_mais_pas_le_reste_de_la_section()
    {
        // Ce que donne un Zigbee2MQTT réglé sans adresse : le port par défaut de sa fiche, et son topic de base.
        var propres = new Dictionary<string, string> { ["Mqtt__Port"] = "1883", ["Mqtt__BaseTopic"] = "zigbee2mqtt" };

        var env = SharedSettings.Merge(propres, [("Mqtt", "mqtt")], Commun);

        Assert.Equal(new Dictionary<string, string>
        {
            ["Mqtt__Host"] = "192.168.1.60", ["Mqtt__Port"] = "1884", ["Mqtt__Username"] = "krissvia",
            ["Mqtt__Password"] = "secret-essai", ["Mqtt__BaseTopic"] = "zigbee2mqtt",
        }, env);
    }

    [Fact]
    public void Un_plugin_qui_a_saisi_son_propre_broker_le_garde()
    {
        var propres = new Dictionary<string, string> { ["Mqtt__Host"] = "10.0.0.5", ["Mqtt__Port"] = "1883" };

        Assert.Equal(propres, SharedSettings.Merge(propres, [("Mqtt", "mqtt")], Commun));
        Assert.Equal(propres, SharedSettings.Merge(propres, [("Mqtt", "mqtt")], new SharedBroker()));   // rien de commun saisi
    }

    [Fact]
    public async Task Le_broker_commun_survit_au_redemarrage_et_refuse_une_adresse_vide()
    {
        var magasin = new Magasin();
        await new SharedBrokerService(magasin).SaveAsync(Commun with { Host = " 192.168.1.60 ", Username = " " });

        var relu = new SharedBrokerService(magasin);
        await relu.LoadAsync();

        Assert.Equal(Commun with { Username = null }, relu.Current);
        await Assert.ThrowsAsync<ArgumentException>(() => relu.SaveAsync(new SharedBroker { Host = "  " }));
    }

    [Fact]
    public async Task Le_superviseur_lance_un_plugin_mqtt_avec_le_broker_commun()
    {
        var banc = await BancPlugins.CreerAsync();
        await using var _ = banc;
        var magasin = new Magasin();
        var broker = new SharedBrokerService(magasin);
        await broker.SaveAsync(Commun);
        var lanceur = new SuperviseurTests.FauxLanceur();
        var superviseur = new PluginSupervisor(magasin, banc.Hub, banc.Options, lanceur, banc.Horloge,
            NullLogger<PluginSupervisor>.Instance, new PluginSettingsService(magasin), broker) { CoreUrl = "ws://127.0.0.1:12795/plugins" };
        await superviseur.LoadAsync([Fiche("Krissvia.Plugin.Zigbee2Mqtt") with { Directory = "/plugins/z2m" }]);

        await superviseur.SetEnabledAsync("zigbee2mqtt", true);

        var env = Assert.Single(lanceur.Lancements).Env;
        Assert.Equal(("192.168.1.60", "1884", "krissvia"), (env["Mqtt__Host"], env["Mqtt__Port"], env["Mqtt__Username"]));
        Assert.True(superviseur.MqttStatus().SharedSaved);
    }

    [Fact]
    public async Task Sans_broker_commun_le_superviseur_le_signale_a_la_pastille()
    {
        var banc = await BancPlugins.CreerAsync();
        await using var _ = banc;
        var magasin = new Magasin();
        var superviseur = new PluginSupervisor(magasin, banc.Hub, banc.Options, new SuperviseurTests.FauxLanceur(), banc.Horloge,
            NullLogger<PluginSupervisor>.Instance, new PluginSettingsService(magasin), new SharedBrokerService(magasin));
        await superviseur.LoadAsync([Fiche("Krissvia.Plugin.MqttDiscovery") with { Directory = "/plugins/disco" }]);

        await superviseur.SetEnabledAsync("mqtt-discovery", true);

        var etat = superviseur.MqttStatus();
        Assert.False(etat.SharedSaved);
        Assert.Equal(MqttOverview.NotSavedLine, etat.Lines[0]);
    }

    // ── Pastille ─────────────────────────────────────────────────────────────

    private static MqttPluginLink P(string nom, bool connecte = true, bool? lien = true, string? motif = null) =>
        new(nom.ToLowerInvariant(), nom, connecte, lien is { } l ? new LinkState { Link = "mqtt", Connected = l, Detail = motif } : null);

    [Fact]
    public void La_pastille_est_verte_rouge_orange_ou_grise_selon_ce_que_disent_les_plugins()
    {
        Assert.Equal(MqttHealth.NoPlugin, MqttOverview.Evaluate([]).Health);
        Assert.Equal(MqttHealth.Ok, MqttOverview.Evaluate([P("Zigbee2MQTT"), P("openHASP")]).Health);
        Assert.Equal(MqttHealth.Waiting, MqttOverview.Evaluate([P("Zigbee2MQTT"), P("openHASP", lien: null)]).Health);
        Assert.Equal(MqttHealth.Waiting, MqttOverview.Evaluate([P("Zigbee2MQTT"), P("openHASP", connecte: false)]).Health);

        var rouge = MqttOverview.Evaluate([P("Zigbee2MQTT", lien: false, motif: "identifiant ou mot de passe refusé par le broker"), P("openHASP", lien: null)]);
        Assert.Equal(MqttHealth.Problem, rouge.Health);
        Assert.Equal(["openHASP : en attente du broker", "Zigbee2MQTT : identifiant ou mot de passe refusé par le broker"], rouge.Lines);
    }

    [Fact]
    public void Sans_broker_commun_enregistre_la_pastille_le_dit_en_tete_et_n_est_pas_verte()
    {
        var sans = MqttOverview.Evaluate([P("Zigbee2MQTT"), P("MQTT Discovery")], sharedSaved: false);

        Assert.Equal(MqttHealth.Waiting, sans.Health);   // tout le monde est connecté, mais il reste quelque chose à faire
        Assert.False(sans.SharedSaved);
        Assert.Equal(MqttOverview.NotSavedLine, sans.Lines[0]);

        // Une vraie panne reste rouge ; sans plugin MQTT, rien à signaler.
        Assert.Equal(MqttHealth.Problem, MqttOverview.Evaluate([P("MQTT Discovery", lien: false, motif: "aucun broker réglé")], sharedSaved: false).Health);
        Assert.Equal(MqttHealth.NoPlugin, MqttOverview.Evaluate([], sharedSaved: false).Health);
        Assert.True(MqttOverview.Evaluate([P("Zigbee2MQTT")]).SharedSaved);
    }

    [Fact]
    public async Task Le_hub_garde_l_etat_du_lien_le_temps_de_la_session()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = await banc.ConnecterAsync("openhasp");
        var signale = 0;
        banc.Hub.LinksChanged += () => Interlocked.Increment(ref signale);

        await plugin.EnvoyerAsync(new LinkState { Link = "mqtt", Connected = false, Detail = "broker injoignable (adresse ou port)" });
        await BancPlugins.AttendreAsync(() => banc.Hub.GetLink("openhasp", "mqtt") is { Connected: false }, "lien signalé coupé");
        Assert.Equal("broker injoignable (adresse ou port)", banc.Hub.GetLink("openhasp", "mqtt")!.Detail);

        await plugin.CloseAsync("fin", CancellationToken.None);
        await session.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(banc.Hub.GetLink("openhasp", "mqtt"));   // un plugin déconnecté ne répond plus de son broker
        Assert.True(signale >= 2);
    }
}
