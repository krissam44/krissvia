using Krissvia.PluginHost.Services;

namespace Krissvia.PluginHost.Tests;

/// <summary>Le catalogue des plugins installés, sur un vrai dossier temporaire.</summary>
public sealed class CataloguePluginsTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), $"krissvia-plugins-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_racine)) Directory.Delete(_racine, recursive: true);
    }

    private static string Exe(string nom) => OperatingSystem.IsWindows() ? nom + ".exe" : nom;

    private string Installer(string sousDossier, string id, string nom = "Plugin", bool avecExe = true, DateTime? date = null)
    {
        var dossier = Path.Combine(_racine, sousDossier);
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "plugin.json"),
            $$"""{"id":"{{id}}","name":"{{nom}}","description":"essai","executable":"Krissvia.Plugin.Essai"}""");
        if (avecExe)
        {
            var exe = Path.Combine(dossier, Exe("Krissvia.Plugin.Essai"));
            File.WriteAllText(exe, "");
            if (date is { } d) File.SetLastWriteTimeUtc(exe, d);
        }

        return dossier;
    }

    [Fact]
    public void Un_plugin_installe_est_trouve_avec_son_executable()
    {
        var dossier = Installer("shelly/bin/Debug/net10.0", "shelly", "Shelly");

        var (plugins, erreurs) = PluginCatalog.Scan([_racine]);

        var p = Assert.Single(plugins);
        Assert.Equal(("shelly", "Shelly", "essai"), (p.Id, p.Name, p.Description));
        Assert.Equal(Path.GetFullPath(Path.Combine(dossier, Exe("Krissvia.Plugin.Essai"))), p.ExecutablePath);
        Assert.Empty(erreurs);
    }

    [Fact]
    public void Une_fiche_sans_executable_ou_illisible_est_signalee_sans_bloquer_les_autres()
    {
        Installer("a", "shelly");
        Installer("b", "teleinfo", avecExe: false);
        Directory.CreateDirectory(Path.Combine(_racine, "c"));
        File.WriteAllText(Path.Combine(_racine, "c", "plugin.json"), "{pas du json");

        var (plugins, erreurs) = PluginCatalog.Scan([_racine]);

        Assert.Equal("shelly", Assert.Single(plugins).Id);
        Assert.Equal(2, erreurs.Count);
    }

    [Fact]
    public void Les_sorties_intermediaires_de_compilation_sont_ignorees()
    {
        Installer("shelly/obj/Debug/net10.0", "shelly");

        Assert.Empty(PluginCatalog.Scan([_racine]).Plugins);
    }

    [Fact]
    public void La_fiche_source_a_cote_du_csproj_n_est_pas_un_plugin_installe()
    {
        var source = Installer("Krissvia.Plugin.Shelly", "shelly", avecExe: false);
        File.WriteAllText(Path.Combine(source, "Krissvia.Plugin.Shelly.csproj"), "<Project />");
        Installer("Krissvia.Plugin.Shelly/bin/Debug/net10.0", "shelly");

        var (plugins, erreurs) = PluginCatalog.Scan([_racine]);

        Assert.Single(plugins);
        Assert.Empty(erreurs);
    }

    [Fact]
    public void Deux_fiches_du_meme_plugin_la_plus_recente_l_emporte()
    {
        Installer("shelly/bin/Debug/net10.0", "shelly", "Ancien", date: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Installer("shelly/bin/Release/net10.0", "shelly", "Récent", date: new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Récent", Assert.Single(PluginCatalog.Scan([_racine]).Plugins).Name);
    }

    [Fact]
    public void Un_identifiant_hors_format_est_refuse()
    {
        Installer("x", "Mon Plugin");

        var (plugins, erreurs) = PluginCatalog.Scan([_racine]);

        Assert.Empty(plugins);
        Assert.Contains("invalide", Assert.Single(erreurs));
    }

    [Fact]
    public void Un_dossier_absent_ne_fait_pas_d_erreur()
    {
        Assert.Empty(PluginCatalog.Scan([Path.Combine(_racine, "absent")]).Plugins);
    }

    private string InstallerPython(string id, string runtime = "python")
    {
        var dossier = Path.Combine(_racine, id);
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "plugin.json"),
            $$"""{"id":"{{id}}","name":"Météo","runtime":"{{runtime}}","executable":"meteo.py"}""");
        File.WriteAllText(Path.Combine(dossier, "meteo.py"), "");
        return dossier;
    }

    [Fact]
    public void Un_plugin_python_est_trouve_avec_son_script_tel_quel()
    {
        var dossier = InstallerPython("meteo");

        var (plugins, erreurs) = PluginCatalog.Scan([_racine]);

        var p = Assert.Single(plugins);
        Assert.Equal(PluginRuntimes.Python, p.Runtime);
        Assert.Equal(Path.GetFullPath(Path.Combine(dossier, "meteo.py")), p.ExecutablePath);   // pas de « .exe » ajouté
        Assert.Empty(erreurs);
    }

    [Fact]
    public void Un_runtime_inconnu_est_refuse()
    {
        InstallerPython("meteo", runtime: "cobol");

        var (plugins, erreurs) = PluginCatalog.Scan([_racine]);

        Assert.Empty(plugins);
        Assert.Contains("runtime « cobol » inconnu", Assert.Single(erreurs));
    }

    [Fact]
    public void Un_plugin_python_est_lance_par_l_interpreteur_avec_le_sdk_du_coeur()
    {
        var fiche = new PluginManifest { Id = "meteo", Name = "Météo", Runtime = "python", Executable = "meteo.py", Directory = _racine };
        var options = new PluginHostOptions { SdkDirectory = Path.Combine(_racine, "sdk"), Runtimes = { ["python"] = "/opt/py/bin/python3" } };

        var demarrage = ProcessPluginLauncher.StartInfo(fiche, options, new Dictionary<string, string> { ["Krissvia__Token"] = "abc" });

        Assert.Equal("/opt/py/bin/python3", demarrage.FileName);
        Assert.Equal(["-u", Path.Combine(_racine, "meteo.py")], demarrage.ArgumentList);
        Assert.StartsWith(Path.Combine(_racine, "sdk", "python"), demarrage.Environment["PYTHONPATH"]);
        Assert.Equal("utf-8", demarrage.Environment["PYTHONIOENCODING"]);
        Assert.Equal("abc", demarrage.Environment["Krissvia__Token"]);
        Assert.Equal(_racine, demarrage.WorkingDirectory);
    }

    [Fact]
    public void Un_plugin_natif_est_lance_directement_et_sans_arguments()
    {
        var fiche = new PluginManifest { Id = "shelly", Name = "Shelly", Executable = "Krissvia.Plugin.Shelly", Directory = _racine };

        var demarrage = ProcessPluginLauncher.StartInfo(fiche, new PluginHostOptions(), new Dictionary<string, string>());

        Assert.Equal(fiche.ExecutablePath, demarrage.FileName);
        Assert.Empty(demarrage.ArgumentList);
        Assert.False(demarrage.Environment.ContainsKey("PYTHONIOENCODING"));
    }

    [Fact]
    public void Sans_reglage_l_interpreteur_python_est_celui_du_path()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "python" : "python3", PluginRuntimes.Interpreter("python", new PluginHostOptions()));
    }
}
