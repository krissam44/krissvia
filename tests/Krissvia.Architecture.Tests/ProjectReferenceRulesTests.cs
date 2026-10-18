using System.Xml.Linq;

namespace Krissvia.Architecture.Tests;

/// <summary>
/// Garde-fou des dépendances entre projets. Lit les .csproj : un ProjectReference non
/// autorisé fait échouer le test, même si le code ne l'utilise pas encore.
/// </summary>
public class ProjectReferenceRulesTests
{
    // Pour chaque projet : les seuls projets Krissvia qu'il a le droit de référencer.
    private static readonly Dictionary<string, string[]> Autorise = new()
    {
        ["Krissvia.Contracts"] = [],
        ["Krissvia.Core"] = ["Krissvia.Contracts"],
        ["Krissvia.Climate"] = ["Krissvia.Core", "Krissvia.Contracts"],
        ["Krissvia.Persistence"] = ["Krissvia.Core", "Krissvia.Contracts"],
        ["Krissvia.PluginHost"] = ["Krissvia.Core", "Krissvia.Contracts"],
        // Kit des plugins C# : ne voit que le contrat, comme un plugin Python.
        ["Krissvia.Sdk"] = ["Krissvia.Contracts"],
        ["Krissvia.Sdk.Mqtt"] = [],
        // Ce que référence un widget Blazor : rien d'autre de Krissvia (WIDGETS.md §2.2).
        ["Krissvia.Widgets.Blazor"] = [],
        ["Krissvia.Server"] =
        [
            "Krissvia.Contracts", "Krissvia.Core", "Krissvia.Climate",
            "Krissvia.Persistence", "Krissvia.PluginHost", "Krissvia.Widgets.Blazor"
        ],
        ["Krissvia.Plugin.Zigbee2Mqtt"] = ["Krissvia.Contracts", "Krissvia.Sdk", "Krissvia.Sdk.Mqtt"],
        ["Krissvia.Plugin.Shelly"] = ["Krissvia.Contracts", "Krissvia.Sdk"],
        ["Krissvia.Plugin.Teleinfo"] = ["Krissvia.Contracts", "Krissvia.Sdk", "Krissvia.Sdk.Mqtt"],
    };

    [Fact]
    public void Chaque_projet_ne_reference_que_les_projets_autorises()
    {
        var violations = new List<string>();

        foreach (var (nom, chemin, _) in ProjetsDeLaSolution())
        {
            if (!Autorise.TryGetValue(nom, out var permis))
            {
                violations.Add($"{nom} : projet absent de la table des dépendances autorisées (à déclarer).");
                continue;
            }

            foreach (var reference in References(chemin).Where(r => !permis.Contains(r)))
                violations.Add($"{nom} -> {reference} n'est pas autorisé.");
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Un_plugin_ne_reference_jamais_Core()
    {
        // Règle centrale de la plateforme : un plugin C# doit se comporter comme un plugin
        // Python. Il ne voit que le contrat, jamais les internes du cœur.
        var violations = ProjetsDeLaSolution()
            .Where(p => p.EstPlugin)
            .SelectMany(p => References(p.Chemin)
                .Where(r => r != "Krissvia.Contracts" && !r.StartsWith("Krissvia.Sdk", StringComparison.Ordinal))
                .Select(r => $"{p.Nom} -> {r}"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<(string Nom, string Chemin, bool EstPlugin)> ProjetsDeLaSolution()
    {
        var racine = RacineDepot();
        foreach (var dossier in new[] { "src", "plugins" })
        {
            var complet = Path.Combine(racine, dossier);
            if (!Directory.Exists(complet)) continue;

            foreach (var chemin in Directory.EnumerateFiles(complet, "*.csproj", SearchOption.AllDirectories))
                yield return (Path.GetFileNameWithoutExtension(chemin), chemin, dossier == "plugins");
        }
    }

    private static IEnumerable<string> References(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')));

    private static string RacineDepot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Krissvia.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Krissvia.slnx introuvable.");
    }
}
