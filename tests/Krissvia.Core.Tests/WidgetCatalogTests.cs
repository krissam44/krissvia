using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;

namespace Krissvia.Core.Tests;

public sealed class WidgetCatalogTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "kv-widgets-" + Guid.NewGuid().ToString("N"));
    private string Standards => Path.Combine(_racine, "standard");
    private string Locaux => Path.Combine(_racine, "local");

    public void Dispose()
    {
        if (Directory.Exists(_racine)) Directory.Delete(_racine, recursive: true);
    }

    private static string Manifeste(string id, string slots = """[{ "name": "entite", "required": true }]""",
        string element = "", string script = "w.js", int contrat = 2) => $$"""
        { "contract": {{contrat}}, "id": "{{id}}", "name": "Widget {{id}}", "element": "{{(element.Length > 0 ? element : "kv-" + id)}}",
          "script": "{{script}}", "slots": {{slots}}, "size": { "cols": 2, "rows": 1 } }
        """;

    private void Deposer(string racine, string dossier, string manifeste, string? script = "w.js")
    {
        var chemin = Path.Combine(racine, dossier);
        Directory.CreateDirectory(chemin);
        File.WriteAllText(Path.Combine(chemin, "widget.json"), manifeste);
        if (script is not null) File.WriteAllText(Path.Combine(chemin, script), "// widget");
    }

    private WidgetCatalogSnapshot Charger() => WidgetCatalog.Load(
    [
        WidgetSource.FromDirectory(Standards, WidgetCatalog.Standard, "/widgets/standard"),
        WidgetSource.FromDirectory(Locaux, WidgetCatalog.Local, "/widgets/local/"),
    ]);

    private static RegisteredEntity Entite(EntityKind kind, string? deviceClass = null, string? usage = null) => new()
    {
        EntityId = "e", DeviceId = "d", IntegrationId = "p", UniqueId = "u", Kind = kind, DisplayName = "E",
        DeviceClass = deviceClass, Usage = usage,
    };

    [Fact]
    public void Un_widget_peut_demander_une_demi_ligne_de_plus_mais_pas_un_quart()
    {
        Deposer(Locaux, "energie", Manifeste("energie").Replace("\"rows\": 1", "\"rows\": 2.5"));
        Deposer(Locaux, "quart", Manifeste("quart").Replace("\"rows\": 1", "\"rows\": 2.25"));

        var catalogue = Charger();

        Assert.Equal(2.5, Assert.Single(catalogue.Widgets).Manifest.Size.Rows);
        Assert.Contains(catalogue.Errors, e => e.Contains("quart") && e.Contains("demi-ligne"));
    }

    [Fact]
    public void Un_widget_valide_entre_au_catalogue_avec_l_adresse_de_son_script()
    {
        Deposer(Locaux, "jauge", Manifeste("jauge-cuve", """[{ "name": "niveau", "kinds": ["sensor", "binary_sensor"], "device_classes": ["moisture"], "required": true }]"""));

        var catalogue = Charger();

        var w = Assert.Single(catalogue.Widgets);
        Assert.Empty(catalogue.Errors);
        Assert.Equal("/widgets/local/jauge/w.js", w.ScriptUrl);
        Assert.Equal(WidgetCatalog.Local, w.Origin);
        Assert.Equal([EntityKind.Sensor, EntityKind.BinarySensor], w.Manifest.Slots[0].Kinds);
        Assert.Equal(new WidgetSize(2, 1), w.Manifest.Size);
    }

    [Fact]
    public void Un_widget_du_contrat_1_garde_sa_largeur_comptee_en_douziemes()
    {
        Deposer(Locaux, "ancien", Manifeste("ancien", contrat: 1));

        var w = Assert.Single(Charger().Widgets);

        // 2 sixièmes = 4 douzièmes : la carte garde exactement sa largeur dans la grille de 12.
        Assert.Equal(new WidgetSize(4, 1), w.Manifest.Size);
    }

    [Theory]
    [InlineData("contrat", """{ "contract": 3, "id": "a", "name": "A", "element": "kv-a", "script": "w.js", "slots": [{ "name": "e", "required": true }] }""", "contrat 3")]
    [InlineData("element", """{ "contract": 1, "id": "a", "name": "A", "element": "ma-jauge", "script": "w.js", "slots": [{ "name": "e", "required": true }] }""", "kv-")]
    [InlineData("exterieur", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "https://exemple.org/w.js", "slots": [{ "name": "e", "required": true }] }""", "chemin relatif")]
    [InlineData("remonte", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "../autre/w.js", "slots": [{ "name": "e", "required": true }] }""", "chemin relatif")]
    [InlineData("remontemilieu", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "js/../../autre/w.js", "slots": [{ "name": "e", "required": true }] }""", "chemin relatif")]
    [InlineData("sansslot", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "w.js", "slots": [] }""", "au moins un slot")]
    [InlineData("principal", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "w.js", "slots": [{ "name": "e" }] }""", "principal")]
    [InlineData("taille", """{ "contract": 2, "id": "a", "name": "A", "element": "kv-a", "script": "w.js", "slots": [{ "name": "e", "required": true }], "size": { "cols": 13 } }""", "size")]
    [InlineData("taillev1", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "w.js", "slots": [{ "name": "e", "required": true }], "size": { "cols": 7 } }""", "de 1 à 6 colonnes")]
    [InlineData("json", """{ "contract": 1, "id": """, "illisible")]
    public void Un_widget_invalide_est_ecarte_avec_sa_raison_sans_gener_les_autres(string dossier, string manifeste, string raison)
    {
        Deposer(Locaux, dossier, manifeste);
        Deposer(Locaux, "zz-bon", Manifeste("bon"));

        var catalogue = Charger();

        Assert.Equal("bon", Assert.Single(catalogue.Widgets).Manifest.Id);
        Assert.Contains(raison, Assert.Single(catalogue.Errors));
    }

    private static WidgetManifest Composant(string id, string? type = "Perso.Jauge") => new()
    {
        Contract = 1, Id = id, Name = id, Blazor = type,
        Slots = [new WidgetSlot { Name = "mesure", Kinds = [EntityKind.Sensor], Required = true }],
    };

    [Fact]
    public void Un_widget_Blazor_entre_au_catalogue_sans_script()
    {
        Deposer(Locaux, "a", Manifeste("a"));

        var catalogue = WidgetCatalog.Load(
            [WidgetSource.FromDirectory(Locaux, WidgetCatalog.Local, "/widgets/local")], [Composant("jauge")]);

        var jauge = catalogue.Find("jauge")!;
        Assert.True(jauge.IsBlazor);
        Assert.Equal(WidgetCatalog.Blazor, jauge.Origin);
        Assert.Equal("", jauge.ScriptUrl);
        Assert.False(catalogue.Find("a")!.IsBlazor);
        Assert.Empty(catalogue.Errors);
    }

    [Fact]
    public void Un_widget_Blazor_ne_prend_pas_l_identifiant_d_un_autre_ni_ne_se_passe_de_composant()
    {
        Deposer(Standards, "valeur", Manifeste("valeur"));

        var catalogue = WidgetCatalog.Load(
            [WidgetSource.FromDirectory(Standards, WidgetCatalog.Standard, "/widgets/standard")],
            [Composant("valeur"), Composant("sans-type", type: " "), Composant("Majuscules")]);

        Assert.Equal(WidgetCatalog.Standard, Assert.Single(catalogue.Widgets).Origin);
        Assert.Equal(3, catalogue.Errors.Count);
        Assert.Contains(catalogue.Errors, e => e.Contains("déjà pris"));
        Assert.Contains(catalogue.Errors, e => e.Contains("nom du composant"));
    }

    [Fact]
    public void Un_widget_json_ne_peut_pas_se_declarer_Blazor()
    {
        Deposer(Locaux, "a", """{ "contract": 1, "id": "a", "name": "A", "element": "kv-a", "script": "w.js", "blazor": "X.Y", "slots": [{ "name": "e", "required": true }] }""");

        Assert.Contains("attribut", Assert.Single(Charger().Errors));
    }

    [Fact]
    public void Un_script_absent_est_signale()
    {
        Deposer(Locaux, "a", Manifeste("a"), script: null);

        Assert.Contains("introuvable", Assert.Single(Charger().Errors));
    }

    [Fact]
    public void Un_identifiant_deja_pris_par_un_standard_est_refuse_au_widget_local()
    {
        Deposer(Standards, "valeur", Manifeste("valeur"));
        Deposer(Locaux, "valeur", Manifeste("valeur"));

        var catalogue = Charger();

        Assert.Equal(WidgetCatalog.Standard, Assert.Single(catalogue.Widgets).Origin);
        Assert.Contains("déjà pris", Assert.Single(catalogue.Errors));
    }

    [Fact]
    public void Un_dossier_absent_ne_gene_pas()
    {
        Assert.Empty(Charger().Widgets);
        Assert.Empty(Charger().Errors);
    }

    [Theory]
    [InlineData(new EntityKind[0], new string[0], -1, 0)]
    [InlineData(new[] { EntityKind.Sensor }, new string[0], -1, 1)]
    [InlineData(new[] { EntityKind.Sensor }, new[] { "temperature" }, -1, 2)]
    [InlineData(new[] { EntityKind.Switch }, new string[0], -1, -1)]
    [InlineData(new[] { EntityKind.Sensor }, new[] { "humidity" }, -1, -1)]
    public void Le_score_mesure_la_precision_du_slot(EntityKind[] kinds, string[] classes, int _, int attendu)
    {
        var slot = new WidgetSlot { Name = "e", Kinds = kinds, DeviceClasses = classes };

        Assert.Equal(attendu, WidgetCatalog.Score(slot, Entite(EntityKind.Sensor, "temperature")));
    }

    [Fact]
    public void L_usage_est_le_critere_le_plus_precis()
    {
        var slot = new WidgetSlot { Name = "e", Usages = ["cuve"] };

        Assert.Equal(3, WidgetCatalog.Score(slot, Entite(EntityKind.Sensor, usage: "cuve")));
        Assert.Equal(-1, WidgetCatalog.Score(slot, Entite(EntityKind.Sensor)));
    }

    /// <summary>Les widgets standards livrés avec le serveur, tels quels.</summary>
    private static string DossierLivre()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "Krissvia.slnx"))) dossier = dossier.Parent;
        return Path.Combine(dossier!.FullName, "src", "Krissvia.Server", "wwwroot", "widgets", "standard");
    }

    [Theory]
    [InlineData(EntityKind.Switch, null, "interrupteur")]
    [InlineData(EntityKind.Switch, EntityUsages.Radiator, "radiateur")]
    [InlineData(EntityKind.Switch, EntityUsages.Lighting, "eclairage")]
    [InlineData(EntityKind.Switch, EntityUsages.Plug, "prise")]
    [InlineData(EntityKind.Switch, EntityUsages.Meter, "compteur")]
    [InlineData(EntityKind.Light, null, "eclairage")]   // une lumière éclaire sans qu'on ait à le dire
    public void Chaque_interrupteur_a_par_defaut_la_carte_de_son_usage(EntityKind famille, string? usage, string attendu)
    {
        var catalogue = WidgetCatalog.Load([WidgetSource.FromDirectory(DossierLivre(), WidgetCatalog.Standard, "/widgets/standard")]);

        Assert.Empty(catalogue.Errors);
        var compatibles = WidgetCatalog.CompatibleWith(catalogue, Entite(famille, usage: usage));
        Assert.Equal(attendu, compatibles[0].Manifest.Id);
        Assert.Contains(compatibles, w => w.Manifest.Id == "interrupteur");   // le bouton simple reste au choix
    }

    [Fact]
    public void Un_plugin_livre_ses_widgets_sous_son_adresse_sans_prendre_un_identifiant_existant()
    {
        var plugin = Path.Combine(_racine, "plugin-proxmox", "widgets");
        Deposer(Standards, "valeur", Manifeste("valeur"));
        Deposer(plugin, "vm", Manifeste("proxmox-vm"));
        Deposer(plugin, "copie", Manifeste("valeur"));

        var catalogue = WidgetCatalog.Load(
        [
            WidgetSource.FromDirectory(Standards, WidgetCatalog.Standard, "/widgets/standard"),
            WidgetSource.FromDirectory(plugin, WidgetCatalog.PluginOrigin("proxmox"), WidgetCatalog.PluginBaseUrl + "/proxmox"),
        ]);

        var vm = catalogue.Find("proxmox-vm")!;
        Assert.Equal("plugin:proxmox", vm.Origin);
        Assert.Equal("proxmox", WidgetCatalog.PluginOf(vm.Origin));
        Assert.Equal("/widgets/plugins/proxmox/vm/w.js", vm.ScriptUrl);
        Assert.Equal(WidgetCatalog.Standard, catalogue.Find("valeur")!.Origin);   // le standard garde son identifiant
        Assert.Contains(catalogue.Errors, e => e.StartsWith("plugin:proxmox/copie", StringComparison.Ordinal) && e.Contains("déjà pris"));
        Assert.Null(WidgetCatalog.PluginOf(WidgetCatalog.Local));
    }

    [Theory]
    [InlineData("vm/w.js", true)]
    [InlineData("vm/../vm/w.js", false)]
    [InlineData("../plugin.json", false)]
    [InlineData("vm//w.js", false)]
    [InlineData("./vm/w.js", false)]
    [InlineData(@"vm\w.js", false)]
    [InlineData("C:/Windows/win.ini", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("vm/absent.js", false)]
    [InlineData("", false)]
    public void Un_fichier_de_widget_de_plugin_ne_sort_jamais_du_dossier_widgets(string chemin, bool servi)
    {
        var plugin = Path.Combine(_racine, "plugin");
        Deposer(Path.Combine(plugin, "widgets"), "vm", Manifeste("vm"));
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), "{}");

        Assert.Equal(servi, WidgetCatalog.SafeFile(Path.Combine(plugin, "widgets"), chemin) is not null);
    }

    [Fact]
    public void L_alarme_du_coeur_a_sa_carte_avec_clavier_par_defaut()
    {
        var catalogue = WidgetCatalog.Load([WidgetSource.FromDirectory(DossierLivre(), WidgetCatalog.Standard, "/widgets/standard")]);

        Assert.Empty(catalogue.Errors);
        Assert.Equal("alarme", WidgetCatalog.CompatibleWith(catalogue, Entite(EntityKind.Sensor, DeviceClasses.Alarm))[0].Manifest.Id);
        Assert.DoesNotContain(WidgetCatalog.CompatibleWith(catalogue, Entite(EntityKind.Sensor, DeviceClasses.Temperature)), w => w.Manifest.Id == "alarme");
    }

    [Fact]
    public void Le_widget_le_plus_precis_vient_d_abord_et_un_local_l_emporte_a_egalite()
    {
        Deposer(Standards, "valeur", Manifeste("valeur"));
        Deposer(Standards, "temperature", Manifeste("temperature", """[{ "name": "t", "kinds": ["sensor"], "device_classes": ["temperature"], "required": true }]"""));
        Deposer(Locaux, "ma-temp", Manifeste("ma-temp", """[{ "name": "t", "kinds": ["sensor"], "device_classes": ["temperature"], "required": true }]"""));
        Deposer(Standards, "interrupteur", Manifeste("interrupteur", """[{ "name": "s", "kinds": ["switch"], "required": true }]"""));

        var compatibles = WidgetCatalog.CompatibleWith(Charger(), Entite(EntityKind.Sensor, "temperature"));

        Assert.Equal(["ma-temp", "temperature", "valeur"], compatibles.Select(w => w.Manifest.Id));
    }

    [Fact]
    public void A_score_egal_le_widget_le_plus_specifique_l_emporte_mais_jamais_sur_un_widget_installe()
    {
        Deposer(Standards, "puissance", Manifeste("puissance", """[{ "name": "m", "kinds": ["sensor"], "device_classes": ["power", "apparent_power", "energy"], "required": true }]"""));
        Deposer(Standards, "zlinky", Manifeste("zlinky", """[{ "name": "m", "kinds": ["sensor"], "device_classes": ["apparent_power"], "required": true }]"""));
        var linky = Entite(EntityKind.Sensor, "apparent_power");

        // « zlinky » vient après « puissance » dans l'alphabet : seule la spécificité le fait passer devant.
        Assert.Equal("zlinky", WidgetCatalog.CompatibleWith(Charger(), linky)[0].Manifest.Id);

        Deposer(Locaux, "large", Manifeste("large", """[{ "name": "m", "kinds": ["sensor"], "device_classes": ["power", "apparent_power"], "required": true }]"""));
        Assert.Equal("large", WidgetCatalog.CompatibleWith(Charger(), linky)[0].Manifest.Id);
    }
}

public class DashboardServiceTests
{
    private sealed class Magasin : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(Documents.Where(kv => kv.Key.Item1 == collection)
                .Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) =>
            Task.FromResult(Documents.Remove((collection, id)));
    }

    private static DashboardCard Carte(string id, int? cols = null) => new()
    {
        Id = id, Widget = "temperature", Entities = new Dictionary<string, string> { ["t"] = "sensor.salon" }, Cols = cols,
        Settings = JsonSerializer.SerializeToElement(new { seuil = 20 }),
    };

    private static Dashboard Page(string id, params DashboardSection[] sections) => new() { Id = id, Name = id, Sections = sections };

    private static DashboardSection Section(string id, params DashboardCard[] cartes) => new() { Id = id, Title = id, Cards = cartes };

    [Fact]
    public async Task Sans_rien_d_enregistre_il_y_a_une_page_d_accueil_vide()
    {
        var page = Assert.Single(await new DashboardService(new Magasin()).ListAsync());

        Assert.Equal(DashboardService.MainId, page.Id);
        Assert.Empty(page.Sections);
    }

    [Fact]
    public async Task Sections_et_cartes_survivent_a_l_enregistrement_dans_l_ordre_avec_leurs_options()
    {
        var magasin = new Magasin();
        var annonces = 0;
        var service = new DashboardService(magasin);
        service.Changed += () => annonces++;

        await service.SaveAsync(Page("confort", Section("meteo") with { Collapsed = true }, Section("thermo", Carte("b", cols: 2), Carte("a"))));
        var relue = (await new DashboardService(magasin).GetAsync("confort"))!;

        Assert.Equal(["meteo", "thermo"], relue.Sections.Select(s => s.Id));
        Assert.True(relue.Sections[0].Collapsed);
        Assert.Equal(["b", "a"], relue.Sections[1].Cards.Select(c => c.Id));
        Assert.Equal(2, relue.Sections[1].Cards[0].Cols);
        Assert.Null(relue.Sections[1].Cards[1].Cols);
        Assert.Equal("sensor.salon", relue.Sections[1].Cards[0].Entities["t"]);
        Assert.Equal(20, relue.Sections[1].Cards[0].Settings!.Value.GetProperty("seuil").GetInt32());
        Assert.DoesNotContain("all_cards", magasin.Documents.Values.Single());
        Assert.Equal(1, annonces);
    }

    [Fact]
    public async Task Une_page_enregistree_en_sixiemes_est_relue_en_douziemes_une_seule_fois()
    {
        var magasin = new Magasin();
        // Une page d'avant la grille de 12 : pas de « grid », largeurs en sixièmes.
        magasin.Documents[(DashboardService.Collection, "confort")] = """
            { "name": "Confort", "sections": [ { "id": "s", "cards": [
              { "id": "a", "widget": "temperature", "cols": 2 }, { "id": "b", "widget": "temperature" }, { "id": "c", "widget": "temperature", "cols": 6 } ] } ] }
            """;
        var service = new DashboardService(magasin);

        var lue = (await service.GetAsync("confort"))!;
        Assert.Equal(DashboardService.GridColumns, lue.Grid);
        Assert.Equal(new int?[] { 4, null, 12 }, lue.AllCards.Select(c => c.Cols));

        await service.SaveAsync(lue);
        var relue = (await new DashboardService(magasin).GetAsync("confort"))!;
        Assert.Equal(new int?[] { 4, null, 12 }, relue.AllCards.Select(c => c.Cols));
        Assert.Contains("\"grid\":12", magasin.Documents[(DashboardService.Collection, "confort")]);
    }

    [Fact]
    public async Task Le_nom_propre_a_une_carte_est_enregistre_et_borne()
    {
        var magasin = new Magasin();
        var service = new DashboardService(magasin);

        await service.SaveAsync(Page("confort", Section("s", Carte("a") with { Title = "Salon" })));

        Assert.Equal("Salon", (await new DashboardService(magasin).GetAsync("confort"))!.AllCards.Single().Title);
        var refus = await Assert.ThrowsAsync<DashboardValidationException>(() =>
            service.SaveAsync(Page("confort", Section("s", Carte("a") with { Title = new string('x', DashboardService.MaxTitleLength + 1) }))));
        Assert.Contains("nom", refus.Message);
    }

    [Theory]
    [InlineData(1.5, true)]
    [InlineData(2.5, true)]
    [InlineData(4.0, true)]
    [InlineData(1.25, false)]   // pas de quart de ligne : la grille compte en demi-lignes
    [InlineData(0.5, false)]
    [InlineData(4.5, false)]
    public async Task Une_carte_se_regle_par_demi_ligne(double lignes, bool acceptee)
    {
        var magasin = new Magasin();
        var service = new DashboardService(magasin);
        var page = Page("technique", Section("s", Carte("energie") with { Rows = lignes }));

        if (!acceptee)
        {
            var refus = await Assert.ThrowsAsync<DashboardValidationException>(() => service.SaveAsync(page));
            Assert.Contains("demi-ligne", refus.Message);
            return;
        }

        await service.SaveAsync(page);
        var relue = await new DashboardService(magasin).GetAsync("technique");
        Assert.Equal(lignes, relue!.Sections[0].Cards[0].Rows);
    }

    [Fact]
    public async Task Une_section_garde_sa_largeur_et_une_largeur_hors_grille_est_refusee()
    {
        var magasin = new Magasin();
        var service = new DashboardService(magasin);

        await service.SaveAsync(Page("accueil", Section("radiateurs") with { Cols = 6 }, Section("eclairage") with { Cols = 6 }, Section("meteo")));

        var relue = (await new DashboardService(magasin).GetAsync("accueil"))!;
        Assert.Equal([6, 6, null], relue.Sections.Select(s => s.Cols));
        var refus = await Assert.ThrowsAsync<DashboardValidationException>(() =>
            service.SaveAsync(Page("accueil", Section("radiateurs") with { Cols = 13 })));
        Assert.Contains("radiateurs", refus.Message);
        await Assert.ThrowsAsync<DashboardValidationException>(() => service.SaveAsync(Page("accueil", Section("radiateurs") with { Cols = 0 })));
    }

    [Fact]
    public async Task Une_page_comptee_dans_une_autre_grille_est_refusee()
    {
        var service = new DashboardService(new Magasin());

        var refus = await Assert.ThrowsAsync<DashboardValidationException>(() => service.SaveAsync(Page("confort") with { Grid = 6 }));
        Assert.Contains("grid", refus.Message);
        await Assert.ThrowsAsync<DashboardValidationException>(() => service.SaveAsync(Page("confort", Section("s", Carte("a", cols: 13)))));
        await service.SaveAsync(Page("confort", Section("s", Carte("a", cols: 12))));
    }

    [Fact]
    public async Task Tout_enregistrer_fixe_l_ordre_des_pages_et_supprime_les_pages_retirees()
    {
        var magasin = new Magasin();
        var service = new DashboardService(magasin);
        await service.SaveAllAsync([Page("confort"), Page("technique"), Page("vieille")]);

        await service.SaveAllAsync([Page("technique"), Page("confort")]);

        var pages = await service.ListAsync();
        Assert.Equal(["technique", "confort"], pages.Select(p => p.Id));
        Assert.Equal([0, 1], pages.Select(p => p.Order));
        Assert.Equal(2, magasin.Documents.Count);
    }

    [Fact]
    public async Task Tout_enregistrer_n_ecrit_rien_si_une_page_est_invalide()
    {
        var magasin = new Magasin();
        var service = new DashboardService(magasin);
        await service.SaveAllAsync([Page("confort")]);
        var avant = magasin.Documents.Values.Single();

        await Assert.ThrowsAsync<DashboardValidationException>(
            () => service.SaveAllAsync([Page("technique"), Page("confort", Section("s", Carte("a"), Carte("a")))]));

        Assert.Equal(avant, Assert.Single(magasin.Documents).Value);
    }

    [Fact]
    public async Task On_ne_supprime_pas_la_derniere_page()
    {
        var service = new DashboardService(new Magasin());
        await service.SaveAllAsync([Page("confort"), Page("technique")]);

        Assert.True(await service.DeleteAsync("technique"));
        await Assert.ThrowsAsync<DashboardValidationException>(() => service.DeleteAsync("confort"));
    }

    [Fact]
    public async Task Une_page_illisible_n_empeche_pas_de_lire_les_autres()
    {
        var magasin = new Magasin();
        magasin.Documents[(DashboardService.Collection, "cassee")] = "{ pas du json";
        await new DashboardService(magasin).SaveAsync(Page("confort"));

        Assert.Equal("confort", Assert.Single(await new DashboardService(magasin).ListAsync()).Id);
    }

    [Theory]
    [InlineData("Confort", new string[0], "confort")]
    [InlineData("Pièce à vivre", new string[0], "piece-a-vivre")]
    [InlineData("Confort", new[] { "confort", "confort-2" }, "confort-3")]
    [InlineData("!!!", new string[0], "page")]
    public void L_identifiant_d_une_nouvelle_page_vient_de_son_nom(string nom, string[] pris, string attendu)
    {
        Assert.Equal(attendu, DashboardService.NewPageId(nom, pris));
    }

    [Theory]
    [InlineData("doublecarte")]
    [InlineData("doublesection")]
    [InlineData("taille")]
    [InlineData("widget")]
    [InlineData("slot")]
    [InlineData("idpage")]
    [InlineData("nom")]
    public async Task Une_page_mal_formee_est_refusee_et_rien_n_est_ecrit(string defaut)
    {
        var magasin = new Magasin();
        var page = defaut switch
        {
            // Une carte est unique dans la PAGE, pas seulement dans sa section : le JS les retrouve par identifiant.
            "doublecarte" => Page("p", Section("a", Carte("x")), Section("b", Carte("x"))),
            "doublesection" => Page("p", Section("a"), Section("a")),
            "taille" => Page("p", Section("a", Carte("x", cols: 13))),
            "widget" => Page("p", Section("a", Carte("x") with { Widget = "" })),
            "slot" => Page("p", Section("a", Carte("x") with { Entities = new Dictionary<string, string> { ["t"] = "" } })),
            "idpage" => Page("Ma Page"),
            _ => Page("p") with { Name = " " },
        };

        await Assert.ThrowsAsync<DashboardValidationException>(() => new DashboardService(magasin).SaveAsync(page));
        Assert.Empty(magasin.Documents);
    }
}

public class DashboardProposalTests
{
    private static RegisteredEntity E(string id, EntityKind kind, string? classe = null, string appareil = "d", bool active = true) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = kind, DisplayName = id,
        DeviceClass = classe, Enabled = active,
    };

    private static CatalogWidget W(string id, string standard, params WidgetSlot[] slots) =>
        new(new WidgetManifest { Contract = 1, Id = id, Name = id, Element = $"kv-{id}", Script = "w.js", Slots = slots }, standard, $"/{id}.js");

    private static readonly WidgetCatalogSnapshot Catalogue = new(
    [
        W("valeur", WidgetCatalog.Standard, new WidgetSlot { Name = "entite", Required = true }),
        W("temperature", WidgetCatalog.Standard,
            new WidgetSlot { Name = "temperature", Kinds = [EntityKind.Sensor], DeviceClasses = ["temperature"], Required = true },
            new WidgetSlot { Name = "humidite", Kinds = [EntityKind.Sensor], DeviceClasses = ["humidity"] }),
        W("interrupteur", WidgetCatalog.Standard, new WidgetSlot { Name = "interrupteur", Kinds = [EntityKind.Switch, EntityKind.Light], Required = true }),
        W("thermostat", WidgetCatalog.Standard, new WidgetSlot { Name = "thermostat", Kinds = [EntityKind.Climate], Required = true }),
    ], []);

    [Fact]
    public void Une_section_par_famille_dans_l_ordre_avec_le_meilleur_widget()
    {
        RegisteredEntity[] entites =
        [
            E("switch.prise", EntityKind.Switch, appareil: "p1"),
            E("sensor.salon_t", EntityKind.Sensor, "temperature", "s1"),
            E("climate.salon", EntityKind.Climate, appareil: "t1"),
            E("binary_sensor.porte", EntityKind.BinarySensor, "opening", "b1"),
        ];

        var sections = DashboardProposal.Propose(entites, Catalogue);

        Assert.Equal(["Thermostats", "Températures", "Interrupteurs et lumières", "Capteurs"], sections.Select(s => s.Title));
        // Les cartes proposées suivent leur entité : aucun widget figé dans la carte.
        Assert.All(sections, s => Assert.Null(s.Cards.Single().Widget));
        Assert.Equal(["thermostat", "temperature", "interrupteur", "valeur"],
            sections.Select(s => WidgetResolver.Resolve(s.Cards.Single(), entites, Catalogue).WidgetId));
    }

    [Fact]
    public void Une_sonde_recoit_l_humidite_de_son_propre_appareil()
    {
        RegisteredEntity[] entites =
        [
            E("sensor.cave_h", EntityKind.Sensor, "humidity", "cave"),
            E("sensor.salon_t", EntityKind.Sensor, "temperature", "salon"),
            E("sensor.salon_h", EntityKind.Sensor, "humidity", "salon"),
            E("sensor.cave_t", EntityKind.Sensor, "temperature", "autre"),
        ];

        var cartes = DashboardProposal.Propose(entites, Catalogue).Single().Cards
            .Select(c => WidgetResolver.Resolve(c, entites, Catalogue).Entities).ToList();

        Assert.Equal("sensor.salon_h", cartes.Single(c => c["temperature"] == "sensor.salon_t")["humidite"]);
        Assert.False(cartes.Single(c => c["temperature"] == "sensor.cave_t").ContainsKey("humidite"));
    }

    [Fact]
    public void Les_entites_desactivees_ou_manquantes_sont_ecartees_et_une_famille_vide_ne_fait_pas_de_section()
    {
        var sections = DashboardProposal.Propose(
        [
            E("switch.a", EntityKind.Switch, active: false),
            E("switch.b", EntityKind.Switch) with { Missing = true },
        ], Catalogue);

        Assert.Empty(sections);
    }

    [Fact]
    public void Un_interrupteur_declare_radiateur_a_sa_propre_section()
    {
        RegisteredEntity[] entites =
        [
            E("switch.prise", EntityKind.Switch, appareil: "1"),
            E("switch.radiateur", EntityKind.Switch, appareil: "2") with { Usage = EntityUsages.Radiator },
        ];

        var sections = DashboardProposal.Propose(entites, Catalogue);

        Assert.Equal(["Radiateurs", "Interrupteurs et lumières"], sections.Select(s => s.Title));
        Assert.Equal("switch.radiateur", sections[0].Cards.Single().Entity);
        Assert.Equal("switch.prise", sections[1].Cards.Single().Entity);
    }

    [Fact]
    public void Un_radiateur_pilote_par_un_thermostat_n_est_pas_propose()
    {
        var sections = DashboardProposal.Propose(
            [E("switch.radiateur", EntityKind.Switch, appareil: "1"), E("switch.prise", EntityKind.Switch, appareil: "2")],
            Catalogue, new HashSet<string> { "switch.radiateur" });

        Assert.Equal("switch.prise", sections.Single().Cards.Single().Entity);
    }

    [Fact]
    public void Les_identifiants_proposes_sont_uniques()
    {
        var sections = DashboardProposal.Propose(
            [E("switch.a", EntityKind.Switch, appareil: "1"), E("switch.b", EntityKind.Switch, appareil: "2")], Catalogue);

        Assert.Equal(2, sections.SelectMany(s => s.Cards).Select(c => c.Id).Distinct().Count());
        DashboardService.Validate(new Dashboard { Id = "p", Sections = sections });
    }
}

public class WidgetResolverTests
{
    private static RegisteredEntity E(string id, EntityKind kind, string? classe = null, string appareil = "d", string? widget = null) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = kind, DisplayName = id,
        DeviceClass = classe, Widget = widget,
    };

    private static CatalogWidget W(string id, params WidgetSlot[] slots) =>
        new(new WidgetManifest { Contract = 1, Id = id, Name = id, Element = $"kv-{id}", Script = "w.js", Slots = slots }, WidgetCatalog.Standard, $"/{id}.js");

    private static readonly WidgetCatalogSnapshot Catalogue = new(
    [
        W("valeur", new WidgetSlot { Name = "entite", Required = true }),
        W("temperature",
            new WidgetSlot { Name = "temperature", Kinds = [EntityKind.Sensor], DeviceClasses = ["temperature"], Required = true },
            new WidgetSlot { Name = "humidite", Kinds = [EntityKind.Sensor], DeviceClasses = ["humidity"] }),
        W("jauge", new WidgetSlot { Name = "mesure", Kinds = [EntityKind.Sensor], Required = true }),
        W("interrupteur", new WidgetSlot { Name = "interrupteur", Kinds = [EntityKind.Switch], Required = true }),
    ], []);

    private static DashboardCard Auto(string entite) => new() { Id = "c", Entity = entite };

    [Fact]
    public void Une_carte_automatique_prend_le_widget_choisi_pour_l_entite()
    {
        RegisteredEntity[] entites = [E("sensor.t", EntityKind.Sensor, "temperature", widget: "jauge")];

        var carte = WidgetResolver.Resolve(Auto("sensor.t"), entites, Catalogue);

        Assert.Equal("jauge", carte.WidgetId);
        Assert.Equal("sensor.t", carte.Entities["mesure"]);
    }

    [Fact]
    public void Sans_choix_ou_avec_un_choix_qui_ne_convient_pas_c_est_le_plus_precis()
    {
        RegisteredEntity[] entites =
        [
            E("sensor.a", EntityKind.Sensor, "temperature"),
            E("sensor.b", EntityKind.Sensor, "temperature", widget: "interrupteur"),
            E("sensor.c", EntityKind.Sensor, "temperature", widget: "disparu"),
        ];

        Assert.All(new[] { "sensor.a", "sensor.b", "sensor.c" },
            id => Assert.Equal("temperature", WidgetResolver.Resolve(Auto(id), entites, Catalogue).WidgetId));
    }

    [Fact]
    public void Le_widget_impose_par_la_carte_passe_avant_celui_de_l_entite()
    {
        RegisteredEntity[] entites = [E("sensor.t", EntityKind.Sensor, "temperature", widget: "jauge")];
        var carte = new DashboardCard { Id = "c", Widget = "valeur", Entities = new Dictionary<string, string> { ["entite"] = "sensor.t" } };

        var resolue = WidgetResolver.Resolve(carte, entites, Catalogue);

        Assert.Equal("valeur", resolue.WidgetId);
        Assert.Equal("sensor.t", resolue.Entities["entite"]);
    }

    [Fact]
    public void Les_slots_secondaires_ne_prennent_que_des_entites_actives_du_meme_appareil()
    {
        RegisteredEntity[] entites =
        [
            E("sensor.h_ailleurs", EntityKind.Sensor, "humidity", "autre"),
            E("sensor.h_desactivee", EntityKind.Sensor, "humidity", "salon") with { Enabled = false },
            E("sensor.t", EntityKind.Sensor, "temperature", "salon"),
        ];

        Assert.False(WidgetResolver.Resolve(Auto("sensor.t"), entites, Catalogue).Entities.ContainsKey("humidite"));
    }

    [Fact]
    public void Une_entite_disparue_s_affiche_dans_le_widget_generique()
    {
        var resolue = WidgetResolver.Resolve(Auto("sensor.disparue"), [], Catalogue);

        Assert.Equal(WidgetCatalog.FallbackId, resolue.WidgetId);
        Assert.Equal("sensor.disparue", resolue.Entities["entite"]);
    }

    [Fact]
    public void Une_carte_doit_avoir_un_widget_ou_une_entite_a_suivre()
    {
        DashboardService.Validate(new Dashboard { Id = "p", Sections = [new DashboardSection { Id = "s", Cards = [Auto("sensor.t")] }] });

        Assert.Throws<DashboardValidationException>(() => DashboardService.Validate(
            new Dashboard { Id = "p", Sections = [new DashboardSection { Id = "s", Cards = [new DashboardCard { Id = "c" }] }] }));
    }
}
