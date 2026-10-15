using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Krissvia.PluginHost.Services;

/// <summary>
/// La fiche d'un plugin installé (<c>plugin.json</c>, à côté de son exécutable) : ce que la page Services affiche et
/// ce dont le superviseur a besoin pour le lancer.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Le plugin_id annoncé au hello : il doit correspondre, sinon la session sera refusée.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }
    public string Description { get; init; } = "";

    /// <summary>Nom de l'exécutable, sans extension (<c>.exe</c> ajouté sous Windows).</summary>
    public required string Executable { get; init; }

    /// <summary>Dossier où la fiche a été trouvée (renseigné par le catalogue).</summary>
    [JsonIgnore]
    public string Directory { get; init; } = "";

    /// <summary>Chemin complet de l'exécutable sur cette machine.</summary>
    [JsonIgnore]
    public string ExecutablePath =>
        Path.Combine(Directory, OperatingSystem.IsWindows() ? Executable + ".exe" : Executable);
}

/// <summary>
/// Trouve les plugins installés : chaque <c>plugin.json</c> sous les dossiers configurés. Deux fiches pour le même
/// identifiant (une compilation Debug et une Release, par exemple) : la plus récente l'emporte.
/// </summary>
public static partial class PluginCatalog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static (IReadOnlyList<PluginManifest> Plugins, IReadOnlyList<string> Errors) Scan(IEnumerable<string> dossiers)
    {
        var trouves = new Dictionary<string, (PluginManifest Fiche, DateTime Date)>(StringComparer.Ordinal);
        var erreurs = new List<string>();

        foreach (var dossier in dossiers.Where(System.IO.Directory.Exists))
        {
            foreach (var fichier in System.IO.Directory.EnumerateFiles(dossier, "plugin.json", SearchOption.AllDirectories))
            {
                // Les sorties intermédiaires de compilation ne sont pas des plugins installés.
                if (fichier.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                // La fiche source, à côté du .csproj : c'est sa copie dans le dossier de sortie qui est le plugin installé.
                if (System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(fichier)!, "*.csproj").Any()) continue;

                try
                {
                    var fiche = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(fichier), Json)
                                ?? throw new JsonException("fiche vide");
                    if (!IdentifiantValide().IsMatch(fiche.Id)) throw new JsonException($"identifiant « {fiche.Id} » invalide");

                    fiche = fiche with { Directory = Path.GetDirectoryName(Path.GetFullPath(fichier))! };
                    if (!File.Exists(fiche.ExecutablePath)) throw new FileNotFoundException($"exécutable absent : {fiche.ExecutablePath}");

                    var date = File.GetLastWriteTimeUtc(fiche.ExecutablePath);
                    if (!trouves.TryGetValue(fiche.Id, out var deja) || date > deja.Date) trouves[fiche.Id] = (fiche, date);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    erreurs.Add($"{fichier} : {ex.Message}");
                }
            }
        }

        return (trouves.Values.Select(t => t.Fiche).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), erreurs);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdentifiantValide();
}
