using System.IO.Compression;
using System.Text;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;

namespace Krissvia.Core.Tests;

/// <summary>
/// Installation d'un widget local depuis l'écran Paramètres › Widgets, et choix explicite d'un widget prévu pour un
/// autre usage (WIDGETS.md §14). Le cas d'origine : le widget « spot » de l'utilisateur, déposé au mauvais endroit puis
/// impossible à choisir parce qu'aucune entité n'a l'usage « spot ».
/// </summary>
public sealed class WidgetInstallerTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "kv-installation-" + Guid.NewGuid().ToString("N"));
    private string Standards => Path.Combine(_racine, "standard");
    private string Locaux => Path.Combine(_racine, "local");

    public void Dispose()
    {
        if (Directory.Exists(_racine)) Directory.Delete(_racine, recursive: true);
    }

    private const string Spot = """
        { "contract": 2, "id": "eclairage-spot", "name": "Éclairage (spot)", "element": "kv-eclairage-spot",
          "script": "eclairage-spot.js", "slots": [ { "name": "interrupteur", "kinds": ["light", "switch"], "usages": ["spot"], "required": true } ],
          "size": { "cols": 2, "rows": 2 } }
        """;

    private static WidgetFile F(string chemin, string texte = "// widget") => new(chemin, Encoding.UTF8.GetBytes(texte));

    private static IReadOnlyList<WidgetFile> FichiersSpot(string prefixe = "") =>
        [F(prefixe + "widget.json", Spot), F(prefixe + "eclairage-spot.js"), F(prefixe + "spot1_on.png", "png"), F(prefixe + "spot1_off.png", "png")];

    private WidgetCatalogSnapshot Charger() => WidgetCatalog.Load(
    [
        WidgetSource.FromDirectory(Standards, WidgetCatalog.Standard, "/widgets/standard"),
        WidgetSource.FromDirectory(Locaux, WidgetCatalog.Local, "/widgets/local"),
    ]);

    private void Standard(string id, string slots)
    {
        var dossier = Path.Combine(Standards, id);
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "widget.json"), $$"""
            { "contract": 2, "id": "{{id}}", "name": "Standard {{id}}", "element": "kv-{{id}}", "script": "w.js", "slots": {{slots}}, "size": { "cols": 2, "rows": 1 } }
            """);
        File.WriteAllText(Path.Combine(dossier, "w.js"), "// standard");
    }

    private string Installer(IReadOnlyList<WidgetFile> fichiers) => WidgetInstaller.Install(Locaux, fichiers, Charger());

    private static byte[] Zip(params (string Chemin, byte[] Contenu)[] entrees)
    {
        using var flux = new MemoryStream();
        using (var archive = new ZipArchive(flux, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (chemin, contenu) in entrees)
            {
                using var e = archive.CreateEntry(chemin).Open();
                e.Write(contenu);
            }
        }

        return flux.ToArray();
    }

    /// <summary>Rien n'a été écrit : ni widget, ni dossier de travail oublié.</summary>
    private void AssertDossierLocalVide() =>
        Assert.True(!Directory.Exists(Locaux) || Directory.GetFileSystemEntries(Locaux).Length == 0,
            $"reste : {string.Join(", ", Directory.Exists(Locaux) ? Directory.GetFileSystemEntries(Locaux) : [])}");

    // ── Installation ─────────────────────────────────────────────────────────

    [Fact]
    public void Les_fichiers_choisis_deviennent_un_widget_local_du_catalogue_avec_ses_images()
    {
        Assert.Equal("eclairage-spot", Installer(FichiersSpot()));

        var w = Assert.Single(Charger().Widgets);
        Assert.Equal(WidgetCatalog.Local, w.Origin);
        Assert.Equal("/widgets/local/eclairage-spot/eclairage-spot.js", w.ScriptUrl);
        Assert.True(File.Exists(Path.Combine(Locaux, "eclairage-spot", "spot1_on.png")));
        Assert.Empty(Directory.GetDirectories(Locaux, ".*"));
    }

    [Fact]
    public void Une_archive_faite_d_un_dossier_s_installe_sous_l_identifiant_du_widget()
    {
        var zip = Zip([.. FichiersSpot("eclairage/").Select(f => (f.Path, f.Content)), ("eclairage/img/fond.svg", "<svg/>"u8.ToArray())]);

        Installer(WidgetInstaller.FromZip(new MemoryStream(zip)));

        Assert.True(File.Exists(Path.Combine(Locaux, "eclairage-spot", "widget.json")));
        Assert.True(File.Exists(Path.Combine(Locaux, "eclairage-spot", "img", "fond.svg")));
        Assert.Single(Charger().Widgets);
    }

    [Theory]
    [InlineData("../evasion.js", "chemin refusé")]
    [InlineData("/etc/x.js", null)]   // une barre en tête est retirée : le fichier reste dans le dossier
    [InlineData("C:/x.js", "chemin refusé")]
    [InlineData(".cache/x.js", "chemin refusé")]
    [InlineData("outil.exe", "type de fichier refusé")]
    public void Rien_ne_s_ecrit_hors_du_dossier_du_widget_ni_d_un_type_inattendu(string chemin, string? refus)
    {
        var fichiers = FichiersSpot().Append(F(chemin)).ToList();

        if (refus is null)
        {
            Installer(fichiers);
            Assert.True(File.Exists(Path.Combine(Locaux, "eclairage-spot", "etc", "x.js")));
            return;
        }

        var ex = Assert.Throws<WidgetInstallException>(() => Installer(fichiers));
        Assert.Contains(refus, ex.Message);
        AssertDossierLocalVide();
    }

    [Fact]
    public void Un_widget_qui_ne_se_chargerait_pas_ne_s_installe_pas()
    {
        var sansScript = Assert.Throws<WidgetInstallException>(() => Installer([F("widget.json", Spot)]));
        var sansManifeste = Assert.Throws<WidgetInstallException>(() => Installer([F("eclairage-spot.js")]));
        var illisible = Assert.Throws<WidgetInstallException>(() => Installer([F("widget.json", "{ pas du json"), F("w.js")]));

        Assert.Contains("script « eclairage-spot.js » introuvable", sansScript.Message);
        Assert.Contains("widget.json introuvable", sansManifeste.Message);
        Assert.Contains("widget.json illisible", illisible.Message);
        AssertDossierLocalVide();
    }

    [Fact]
    public void Un_identifiant_standard_est_refuse_avec_le_nom_du_widget_qui_le_porte()
    {
        Standard("eclairage-spot", """[{ "name": "e", "required": true }]""");

        var ex = Assert.Throws<WidgetInstallException>(() => Installer(FichiersSpot()));

        Assert.Contains("Standard eclairage-spot", ex.Message);
        AssertDossierLocalVide();
    }

    [Fact]
    public void Reinstaller_met_a_jour_tout_le_dossier_meme_range_sous_un_autre_nom()
    {
        // Le widget déposé à la main par l'utilisateur : dossier « eclairage », identifiant « eclairage-spot ».
        var ancien = Path.Combine(Locaux, "eclairage");
        Directory.CreateDirectory(ancien);
        foreach (var f in FichiersSpot().Append(F("vieux.png", "png"))) File.WriteAllBytes(Path.Combine(ancien, f.Path), f.Content);

        Installer(FichiersSpot());

        Assert.False(Directory.Exists(ancien));
        Assert.False(File.Exists(Path.Combine(Locaux, "eclairage-spot", "vieux.png")));
        Assert.Single(Charger().Widgets);
    }

    [Fact]
    public void Un_dossier_du_meme_nom_qui_n_est_pas_ce_widget_n_est_pas_ecrase()
    {
        var etranger = Path.Combine(Locaux, "eclairage-spot");
        Directory.CreateDirectory(etranger);
        File.WriteAllText(Path.Combine(etranger, "notes.txt"), "à moi");

        Assert.Throws<WidgetInstallException>(() => Installer(FichiersSpot()));

        Assert.Equal("à moi", File.ReadAllText(Path.Combine(etranger, "notes.txt")));
    }

    [Fact]
    public void Une_archive_qui_gonfle_au_dela_de_la_limite_est_refusee()
    {
        var zip = Zip(("widget.json", Encoding.UTF8.GetBytes(Spot)), ("eclairage-spot.js", new byte[WidgetInstaller.MaxBytes + 1]));

        var ex = Assert.Throws<WidgetInstallException>(() => WidgetInstaller.FromZip(new MemoryStream(zip)));

        Assert.Contains("décompressée", ex.Message);
    }

    [Fact]
    public void Retirer_ne_touche_qu_aux_widgets_locaux()
    {
        Standard("valeur", """[{ "name": "e", "required": true }]""");
        Installer(FichiersSpot());

        Assert.False(WidgetInstaller.Uninstall(Locaux, "valeur"));
        Assert.True(WidgetInstaller.Uninstall(Locaux, "eclairage-spot"));

        Assert.Equal(["valeur"], Charger().Widgets.Select(w => w.Manifest.Id));
    }

    [Fact]
    public void Un_dossier_cache_n_entre_pas_au_catalogue()
    {
        var travail = Path.Combine(Locaux, ".installation-en-cours");
        Directory.CreateDirectory(travail);
        foreach (var f in FichiersSpot()) File.WriteAllBytes(Path.Combine(travail, f.Path), f.Content);

        Assert.Empty(Charger().Widgets);
    }

    // ── Choix explicite d'un widget prévu pour un autre usage ────────────────

    private static RegisteredEntity Lumiere(string? widget = null) => new()
    {
        EntityId = "light.salon", DeviceId = "d", IntegrationId = "p", UniqueId = "u", Kind = EntityKind.Light, DisplayName = "Salon",
        Widget = widget,
    };

    private WidgetCatalogSnapshot CatalogueAvecSpot()
    {
        Standard("eclairage", """[{ "name": "interrupteur", "kinds": ["switch", "light"], "usages": ["eclairage"], "required": true }]""");
        Standard("ouverture", """[{ "name": "contact", "kinds": ["binary_sensor"], "required": true }]""");
        Installer(FichiersSpot());
        return Charger();
    }

    [Fact]
    public void Le_spot_se_choisit_pour_une_lumiere_mais_ne_s_impose_pas_tout_seul()
    {
        var catalogue = CatalogueAvecSpot();

        Assert.Equal(["eclairage"], WidgetCatalog.CompatibleWith(catalogue, Lumiere()).Select(w => w.Manifest.Id));
        Assert.Equal(["eclairage", "eclairage-spot"], WidgetCatalog.ChoosableFor(catalogue, Lumiere()).Select(w => w.Manifest.Id));
        Assert.True(WidgetCatalog.IsOtherUsage(catalogue.Find("eclairage-spot")!, Lumiere()));
        Assert.Equal("eclairage", WidgetResolver.ChooseFor(Lumiere(), catalogue)!.Manifest.Id);
    }

    [Fact]
    public void Choisi_pour_l_entite_le_spot_est_celui_de_ses_cartes()
    {
        var catalogue = CatalogueAvecSpot();

        Assert.Equal("eclairage-spot", WidgetResolver.ChooseFor(Lumiere(widget: "eclairage-spot"), catalogue)!.Manifest.Id);
    }

    [Fact]
    public void L_usage_s_efface_devant_le_choix_mais_pas_la_famille()
    {
        var catalogue = CatalogueAvecSpot();
        var spot = catalogue.Find("eclairage-spot")!.Manifest.Slots[0];

        Assert.Equal(-1, WidgetCatalog.Score(spot, Lumiere()));
        Assert.Equal(1, WidgetCatalog.Score(spot, Lumiere(), ignoreUsage: true));
        // Une ouverture choisie pour une lumière : le widget ne saurait pas lire sa valeur, le choix est écarté.
        Assert.DoesNotContain("ouverture", WidgetCatalog.ChoosableFor(catalogue, Lumiere()).Select(w => w.Manifest.Id));
        Assert.Equal("eclairage", WidgetResolver.ChooseFor(Lumiere(widget: "ouverture"), catalogue)!.Manifest.Id);
    }
}
