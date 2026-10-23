using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Core.Configuration;
using Krissvia.PluginHost.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.PluginHost.Tests;

/// <summary>Réglages des plugins : description, valeurs par défaut, validation, transmission au lancement.</summary>
public class ReglagesPluginsTests
{
    /// <summary>La vraie fiche d'un plugin du dépôt : une description cassée fait échouer les tests.</summary>
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

    private static SettingField Schema(string projet) => SettingsSchema.Parse(Fiche(projet).Settings)!;

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

    // ── Description ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Krissvia.Plugin.Shelly")]
    [InlineData("Krissvia.Plugin.Zigbee2Mqtt")]
    [InlineData("Krissvia.Plugin.Teleinfo")]
    [InlineData("Krissvia.Plugin.Meteo")]
    public void Chaque_plugin_du_depot_decrit_ses_reglages(string projet)
    {
        var schema = Schema(projet);

        Assert.Equal(SettingType.Object, schema.Type);
        Assert.NotEmpty(schema.Properties);
        Assert.All(schema.Properties, p => Assert.False(string.IsNullOrEmpty(p.Title)));
    }

    [Fact]
    public void Les_valeurs_par_defaut_completent_ce_qui_n_a_pas_ete_saisi()
    {
        var schema = Schema("Krissvia.Plugin.Zigbee2Mqtt");

        var valeur = SettingsSchema.Complete(schema, JsonNode.Parse("""{"Mqtt":{"Host":"192.168.1.60"}}""")!.AsObject());

        Assert.Equal("192.168.1.60", (string?)valeur["Mqtt"]!["Host"]);
        Assert.Equal(1883, (int?)valeur["Mqtt"]!["Port"]);
        Assert.Equal("zigbee2mqtt", (string?)valeur["Mqtt"]!["BaseTopic"]);
        Assert.True(Schema("Krissvia.Plugin.Zigbee2Mqtt").Properties[0].Properties.Single(p => p.Key == "Password").Secret);
    }

    [Fact]
    public void Les_champs_obligatoires_et_les_bornes_sont_controles_avec_des_libelles_lisibles()
    {
        var livebox = Schema("Krissvia.Plugin.Livebox");
        var zigbee = Schema("Krissvia.Plugin.Zigbee2Mqtt");

        var obligatoire = SettingsSchema.Validate(livebox, SettingsSchema.Complete(livebox,
            JsonNode.Parse("""{"Box":{"Password":"  "}}""")!.AsObject()));
        // L'adresse du broker n'est plus obligatoire (vide = broker commun), mais ses bornes restent contrôlées.
        var bornes = SettingsSchema.Validate(zigbee, SettingsSchema.Complete(zigbee,
            JsonNode.Parse("""{"Mqtt":{"Host":"  ","Port":70000}}""")!.AsObject()));

        Assert.Contains("Box › Mot de passe administrateur : obligatoire.", obligatoire);
        Assert.Contains(bornes, e => e.StartsWith("Broker MQTT › Port : au plus", StringComparison.Ordinal));
    }

    [Fact]
    public void Une_liste_d_appareils_est_controlee_ligne_par_ligne()
    {
        var schema = Schema("Krissvia.Plugin.Shelly");

        var vide = SettingsSchema.Validate(schema, SettingsSchema.Complete(schema, null));
        var sansAdresse = SettingsSchema.Validate(schema, SettingsSchema.Complete(schema,
            JsonNode.Parse("""{"Shelly":{"Devices":[{"Host":"192.168.1.203"},{"Name":"Relais"}]}}""")!.AsObject()));

        Assert.Contains("Shelly › Appareils : au moins un élément.", vide);
        Assert.Equal(["Shelly › Appareils n° 2 › Adresse IP : obligatoire."], sansAdresse);
    }

    [Fact]
    public void Un_entier_a_virgule_ou_un_texte_a_la_place_d_un_nombre_est_refuse()
    {
        var schema = Schema("Krissvia.Plugin.Shelly");
        var valeur = JsonNode.Parse("""{"Shelly":{"Devices":[{"Host":"h"}],"PollSeconds":2.5}}""")!.AsObject();

        Assert.Contains("Shelly › Interrogation (s) : nombre entier attendu.", SettingsSchema.Validate(schema, valeur));
        valeur["Shelly"]!["PollSeconds"] = "deux";
        Assert.Contains("Shelly › Interrogation (s) : nombre attendu.", SettingsSchema.Validate(schema, valeur));
    }

    [Fact]
    public void Les_reglages_deviennent_des_variables_de_configuration_sans_les_valeurs_vides()
    {
        var env = SettingsSchema.ToEnvironment(JsonNode.Parse("""
            {"Shelly":{"PollSeconds":2,"Devices":[{"Host":"192.168.1.203","Name":"Pince maison"},{"Host":"192.168.1.204","Name":""}]},
             "Options":{"Actif":true,"Mot":null}}
            """));

        Assert.Equal("2", env["Shelly__PollSeconds"]);
        Assert.Equal("Pince maison", env["Shelly__Devices__0__Name"]);
        Assert.Equal("192.168.1.204", env["Shelly__Devices__1__Host"]);
        Assert.False(env.ContainsKey("Shelly__Devices__1__Name"));   // vide : ne doit pas écraser le fichier du plugin
        Assert.Equal("true", env["Options__Actif"]);
        Assert.False(env.ContainsKey("Options__Mot"));
    }

    // ── Service ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Des_reglages_valides_sont_enregistres_et_relus_apres_un_redemarrage()
    {
        var magasin = new Magasin();
        var fiche = Fiche("Krissvia.Plugin.Shelly");
        var avant = new PluginSettingsService(magasin);

        var erreurs = await avant.SaveAsync(fiche, JsonNode.Parse("""{"Shelly":{"Devices":[{"Host":"192.168.1.203","Name":"Pince"}]}}""")!.AsObject());

        Assert.Empty(erreurs);
        var apres = new PluginSettingsService(magasin);
        await apres.LoadAsync();
        Assert.True(apres.HasSaved("shelly"));
        Assert.Equal("192.168.1.203", (string?)apres.Get(fiche)["Shelly"]!["Devices"]![0]!["Host"]);
        Assert.Equal("2", apres.EnvironmentFor("shelly")["Shelly__PollSeconds"]);   // défaut enregistré avec le reste
    }

    [Fact]
    public async Task Des_reglages_invalides_ne_sont_pas_enregistres()
    {
        var magasin = new Magasin();
        var service = new PluginSettingsService(magasin);

        var erreurs = await service.SaveAsync(Fiche("Krissvia.Plugin.Shelly"), new JsonObject());

        Assert.NotEmpty(erreurs);
        Assert.Empty(magasin.Documents);
        Assert.Empty(service.EnvironmentFor("shelly"));
    }

    [Fact]
    public async Task Les_entites_choisies_dans_les_reglages_d_un_plugin_active_sont_des_references()
    {
        var banc = await BancPlugins.CreerAsync();
        await using var _ = banc;
        var magasin = new Magasin();
        var reglages = new PluginSettingsService(magasin);
        var fiche = Fiche("Krissvia.Plugin.OpenHasp") with { Directory = "/plugins/openhasp" };
        await reglages.SaveAsync(fiche, JsonNode.Parse("""
            {"Mqtt":{"Host":"127.0.0.1"},"Platines":[{"Noeud":"appart1","Thermostat":"climate.salon",
              "Interrupteurs":[{"Entite":"light.lampe_salon"}]}],"Alarme":{"Entite":"sensor.maison_alarme"}}
            """)!.AsObject());
        var superviseur = new PluginSupervisor(magasin, banc.Hub, banc.Options, new SuperviseurTests.FauxLanceur(), banc.Horloge,
            NullLogger<PluginSupervisor>.Instance, reglages) { CoreUrl = "ws://127.0.0.1:12795/plugins" };
        await superviseur.LoadAsync([fiche]);
        var source = new PluginSettingsReferences(superviseur, reglages);

        Assert.Empty(await source.GetReferencesAsync());   // désactivé : il ne lit rien, rien à vérifier

        await superviseur.SetEnabledAsync("openhasp", true);
        var references = await source.GetReferencesAsync();
        Assert.Equal(["climate.salon", "light.lampe_salon", "sensor.maison_alarme"], references.Select(r => r.EntityId).Order());
        Assert.All(references, r => Assert.Equal(("plugin", "openhasp", "Plugin openHASP"), (r.OwnerKind, r.OwnerId, r.OwnerName)));
    }

    [Fact]
    public async Task Le_superviseur_transmet_les_reglages_sans_qu_ils_puissent_ecraser_l_adresse_ni_le_jeton()
    {
        var banc = await BancPlugins.CreerAsync();
        await using var _ = banc;
        var magasin = new Magasin();
        var reglages = new PluginSettingsService(magasin);
        var fiche = Fiche("Krissvia.Plugin.Shelly") with { Directory = "/plugins/shelly" };
        await reglages.SaveAsync(fiche, JsonNode.Parse("""{"Shelly":{"Devices":[{"Host":"192.168.1.203"}]}}""")!.AsObject());
        magasin.Documents[(PluginSettingsService.Collection, "shelly")] =
            """{"Shelly":{"Devices":[{"Host":"192.168.1.203"}]},"Krissvia":{"Url":"ws://pirate/plugins","Token":"x"}}""";
        await reglages.LoadAsync();

        var lanceur = new SuperviseurTests.FauxLanceur();
        var superviseur = new PluginSupervisor(magasin, banc.Hub, banc.Options, lanceur, banc.Horloge,
            NullLogger<PluginSupervisor>.Instance, reglages) { CoreUrl = "ws://127.0.0.1:12795/plugins" };
        await superviseur.LoadAsync([fiche]);
        await superviseur.SetEnabledAsync("shelly", true);

        var env = Assert.Single(lanceur.Lancements).Env;
        Assert.Equal("192.168.1.203", env["Shelly__Devices__0__Host"]);
        Assert.Equal("ws://127.0.0.1:12795/plugins", env["Krissvia__Url"]);
        Assert.Equal(banc.Options.Tokens["shelly"], env["Krissvia__Token"]);
    }
}
