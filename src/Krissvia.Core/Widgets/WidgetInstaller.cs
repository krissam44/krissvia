using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Krissvia.Core.Widgets;

/// <summary>Un fichier d'un widget à installer, avec son chemin relatif (<c>widget.json</c>, <c>img/spot.png</c>…).</summary>
public sealed record WidgetFile(string Path, byte[] Content);

/// <summary>Une installation refusée, avec sa raison, lisible telle quelle par l'administrateur.</summary>
public sealed class WidgetInstallException(string message) : Exception(message);

/// <summary>
/// Installe et retire les widgets du dossier local (WIDGETS.md §14) : ce que fait l'écran Paramètres › Widgets, pour
/// ne pas avoir à chercher sur le serveur le dossier « à côté de la base ».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Le widget est validé AVANT d'écrire quoi que ce soit, avec la même règle que le catalogue : un widget qui
/// s'installe est un widget qui se charge.</item>
/// <item>Écrit dans un dossier caché (point en tête, ignoré du catalogue), puis basculé d'un coup : le tableau de bord
/// ne voit jamais un widget à moitié copié.</item>
/// <item>Un identifiant déjà pris par un widget standard, de plugin ou Blazor est refusé (le catalogue l'écarterait
/// en silence) ; pris par un widget local, c'est une mise à jour, qui remplace tout le dossier.</item>
/// <item>Seulement des fichiers de page web, et rien hors du dossier du widget.</item>
/// </list>
/// </remarks>
public static class WidgetInstaller
{
    public const int MaxFiles = 100;
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Ce qu'un widget peut contenir : son script, ses styles, ses images et polices, sa documentation.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".js", ".mjs", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico",
        ".woff", ".woff2", ".ttf", ".txt", ".md",
    };

    /// <summary>Les fichiers d'une archive .zip (les dossiers en sont déduits).</summary>
    public static IReadOnlyList<WidgetFile> FromZip(Stream zip)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            if (archive.Entries.Count(e => e.Name.Length > 0) > MaxFiles) throw new WidgetInstallException($"plus de {MaxFiles} fichiers dans l'archive");
            var fichiers = new List<WidgetFile>();
            long total = 0;
            foreach (var entree in archive.Entries.Where(e => e.Name.Length > 0))
            {
                // La taille annoncée peut mentir : on compte ce qu'on lit vraiment.
                using var flux = entree.Open();
                using var copie = new MemoryStream();
                var tampon = new byte[81920];
                int lus;
                while ((lus = flux.Read(tampon)) > 0)
                {
                    total += lus;
                    if (total > MaxBytes) throw new WidgetInstallException($"archive de plus de {MaxBytes / 1024 / 1024} Mo une fois décompressée");
                    copie.Write(tampon, 0, lus);
                }

                fichiers.Add(new WidgetFile(entree.FullName, copie.ToArray()));
            }

            return fichiers;
        }
        catch (InvalidDataException ex)
        {
            throw new WidgetInstallException($"archive .zip illisible ({ex.Message})");
        }
    }

    /// <summary>
    /// Installe un widget dans <paramref name="localDirectory"/> et rend son identifiant. <c>widget.json</c> est à la
    /// racine, ou dans l'unique dossier qui contient tout (une archive faite d'un dossier).
    /// </summary>
    /// <param name="catalog">Le catalogue actuel, pour les identifiants déjà pris.</param>
    public static string Install(string localDirectory, IReadOnlyList<WidgetFile> files, WidgetCatalogSnapshot catalog)
    {
        var fichiers = Normaliser(files);
        var manifeste = LireManifeste(fichiers);

        if (catalog.Find(manifeste.Id) is { } existant && existant.Origin != WidgetCatalog.Local)
            throw new WidgetInstallException($"l'identifiant « {manifeste.Id} » est déjà celui du widget « {existant.Manifest.Name} » ({Origine(existant.Origin)}) : donnez un autre « id » au vôtre");

        Directory.CreateDirectory(localDirectory);
        var cible = Path.Combine(localDirectory, manifeste.Id);
        var ancien = DossierDe(localDirectory, manifeste.Id);
        if (Directory.Exists(cible) && !string.Equals(Path.GetFullPath(cible), ancien is null ? null : Path.GetFullPath(ancien), StringComparison.OrdinalIgnoreCase))
            throw new WidgetInstallException($"un dossier « {manifeste.Id} » existe déjà dans le dossier des widgets sans être ce widget : retirez-le d'abord");
        var travail = Path.Combine(localDirectory, $".installation-{Guid.NewGuid():N}");
        try
        {
            foreach (var (chemin, contenu) in fichiers)
            {
                var destination = Path.Combine(travail, Path.Combine(chemin.Split('/')));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, contenu);
            }

            // Bascule : l'ancienne version (son dossier peut porter un autre nom que l'id) part, la nouvelle prend sa place.
            if (ancien is not null) Directory.Delete(ancien, recursive: true);
            Directory.Move(travail, cible);
            return manifeste.Id;
        }
        finally
        {
            if (Directory.Exists(travail)) Directory.Delete(travail, recursive: true);
        }
    }

    /// <summary>Retire un widget local. Vrai s'il existait ; un widget d'une autre origine ne se retire pas d'ici.</summary>
    public static bool Uninstall(string localDirectory, string id)
    {
        if (DossierDe(localDirectory, id) is not { } dossier) return false;
        Directory.Delete(dossier, recursive: true);
        return true;
    }

    /// <summary>Le dossier local dont le <c>widget.json</c> porte cet identifiant, ou null.</summary>
    private static string? DossierDe(string localDirectory, string id)
    {
        if (!Directory.Exists(localDirectory)) return null;
        foreach (var dossier in Directory.GetDirectories(localDirectory).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            var chemin = Path.Combine(dossier, "widget.json");
            if (!File.Exists(chemin)) continue;
            try
            {
                if (WidgetCatalog.ReadManifest(File.ReadAllText(chemin))?.Id == id) return dossier;
            }
            catch (JsonException)
            {
                // Un voisin illisible n'empêche pas d'installer : le catalogue le signale de son côté.
            }
        }

        return null;
    }

    /// <summary>Chemins vérifiés, préfixe commun retiré : <c>eclairage/widget.json</c> devient <c>widget.json</c>.</summary>
    private static List<(string Chemin, byte[] Contenu)> Normaliser(IReadOnlyList<WidgetFile> files)
    {
        if (files.Count == 0) throw new WidgetInstallException("aucun fichier");
        if (files.Count > MaxFiles) throw new WidgetInstallException($"plus de {MaxFiles} fichiers");
        if (files.Sum(f => (long)f.Content.Length) > MaxBytes) throw new WidgetInstallException($"plus de {MaxBytes / 1024 / 1024} Mo");

        var fichiers = new List<(string Chemin, byte[] Contenu)>();
        foreach (var f in files)
        {
            var chemin = f.Path.Replace('\\', '/').TrimStart('/');
            var segments = chemin.Split('/');
            if (chemin.Contains(':') || segments.Any(s => s is "" or "." or ".." || s.StartsWith('.')))
                throw new WidgetInstallException($"« {f.Path} » : chemin refusé (« .. », chemin absolu ou fichier caché)");
            if (!Extensions.Contains(Path.GetExtension(chemin)))
                throw new WidgetInstallException($"« {f.Path} » : type de fichier refusé (acceptés : {string.Join(" ", Extensions.Order())})");
            fichiers.Add((chemin, f.Content));
        }

        if (fichiers.Select(f => f.Chemin).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fichiers.Count)
            throw new WidgetInstallException("deux fichiers portent le même nom");

        // Une archive faite d'un dossier : tout est sous « eclairage/ », widget.json compris.
        if (!fichiers.Any(f => f.Chemin == "widget.json")
            && fichiers.Select(f => f.Chemin.Split('/')[0]).Distinct().ToList() is [var dossier]
            && fichiers.Any(f => f.Chemin == $"{dossier}/widget.json"))
        {
            fichiers = [.. fichiers.Select(f => (f.Chemin[(dossier.Length + 1)..], f.Contenu))];
        }

        if (!fichiers.Any(f => f.Chemin == "widget.json"))
            throw new WidgetInstallException("widget.json introuvable : il doit être à la racine, ou dans le seul dossier de l'archive");
        return fichiers;
    }

    private static WidgetManifest LireManifeste(List<(string Chemin, byte[] Contenu)> fichiers)
    {
        WidgetManifest? manifeste;
        try
        {
            manifeste = WidgetCatalog.ReadManifest(Encoding.UTF8.GetString(fichiers.First(f => f.Chemin == "widget.json").Contenu));
        }
        catch (JsonException ex)
        {
            throw new WidgetInstallException($"widget.json illisible ({ex.Message})");
        }

        if (manifeste is null) throw new WidgetInstallException("widget.json vide");
        if (WidgetCatalog.Validate(manifeste, new DossierEnMemoire(fichiers)) is { } refus) throw new WidgetInstallException($"widget.json : {refus}");
        return manifeste;
    }

    private static string Origine(string origin) =>
        origin == WidgetCatalog.Standard ? "standard"
        : WidgetCatalog.PluginOf(origin) is { } plugin ? $"livré par le plugin {plugin}"
        : origin == WidgetCatalog.Blazor ? "Blazor" : origin;

    private sealed class DossierEnMemoire(List<(string Chemin, byte[] Contenu)> fichiers) : IWidgetFolder
    {
        public string Name => "";

        public string? ReadText(string relativePath) =>
            fichiers.FirstOrDefault(f => f.Chemin == relativePath) is { Contenu: { } c } ? Encoding.UTF8.GetString(c) : null;

        public bool Exists(string relativePath) => fichiers.Any(f => f.Chemin == relativePath);
    }
}
