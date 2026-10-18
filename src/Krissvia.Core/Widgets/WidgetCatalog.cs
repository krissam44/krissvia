using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Widgets;

/// <summary>Une entité attendue par un widget, avec son rôle (WIDGETS.md §3).</summary>
public sealed record WidgetSlot
{
    public required string Name { get; init; }
    public string Type { get; init; } = "entity";
    public IReadOnlyList<EntityKind> Kinds { get; init; } = [];
    public IReadOnlyList<string> DeviceClasses { get; init; } = [];
    public IReadOnlyList<string> Usages { get; init; } = [];
    public bool Required { get; init; }
}

/// <summary>Taille d'une carte, en cellules de la grille.</summary>
public sealed record WidgetSize(int Cols = 1, int Rows = 1);

/// <summary>Le manifeste <c>widget.json</c> (WIDGETS.md §3).</summary>
public sealed record WidgetManifest
{
    public int Contract { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string Element { get; init; } = "";
    public string Script { get; init; } = "";
    public IReadOnlyList<WidgetSlot> Slots { get; init; } = [];
    public WidgetSize Size { get; init; } = new();
    public JsonElement? Settings { get; init; }
}

/// <summary>Un widget du catalogue : son manifeste, d'où il vient et l'adresse de son script.</summary>
/// <param name="Origin"><c>standard</c> (livré avec Krissvia) ou <c>local</c> (dossier de l'installation).</param>
public sealed record CatalogWidget(WidgetManifest Manifest, string Origin, string ScriptUrl)
{
    public bool IsStandard => Origin == WidgetCatalog.Standard;
}

/// <summary>Le catalogue, et ce qui n'a pas pu y entrer : un widget illisible se signale, il ne fait rien tomber.</summary>
public sealed record WidgetCatalogSnapshot(IReadOnlyList<CatalogWidget> Widgets, IReadOnlyList<string> Errors)
{
    public CatalogWidget? Find(string id) => Widgets.FirstOrDefault(w => w.Manifest.Id == id);
}

/// <summary>Le dossier d'un widget, lu sur le disque ou à travers les fichiers statiques du serveur.</summary>
public interface IWidgetFolder
{
    string Name { get; }

    /// <summary>Contenu d'un fichier du dossier, ou null s'il n'existe pas.</summary>
    string? ReadText(string relativePath);

    bool Exists(string relativePath);
}

/// <summary>Un ensemble de dossiers de widgets : leur origine et l'adresse où le serveur publie leurs fichiers.</summary>
public sealed record WidgetSource(string Origin, string BaseUrl, IReadOnlyList<IWidgetFolder> Folders)
{
    /// <summary>Les sous-dossiers d'un répertoire du disque (aucun s'il n'existe pas).</summary>
    public static WidgetSource FromDirectory(string directory, string origin, string baseUrl) =>
        new(origin, baseUrl, System.IO.Directory.Exists(directory)
            ? [.. System.IO.Directory.GetDirectories(directory).Select(d => new DossierDisque(d))]
            : []);

    private sealed class DossierDisque(string chemin) : IWidgetFolder
    {
        public string Name => Path.GetFileName(chemin);

        public string? ReadText(string relativePath) =>
            File.Exists(Path.Combine(chemin, relativePath)) ? File.ReadAllText(Path.Combine(chemin, relativePath)) : null;

        public bool Exists(string relativePath) => File.Exists(Path.Combine(chemin, relativePath));
    }
}

/// <summary>
/// Lecture des widgets : un sous-dossier par widget, avec son <c>widget.json</c> et son script. Relu à chaque appel :
/// un widget déposé dans le dossier apparaît sans redémarrer.
/// </summary>
public static partial class WidgetCatalog
{
    public const string Standard = "standard";
    public const string Local = "local";
    public const int ContractVersion = 1;
    /// <summary>Largeur maximale : 6 colonnes, toute la largeur d'un grand écran (« pleine largeur » de DomoMud).</summary>
    public const int MaxCols = 6;

    public const int MaxRows = 4;

    /// <summary>Le widget générique, qui sait tout afficher : le dernier recours (WIDGETS.md §6).</summary>
    public const string FallbackId = "valeur";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static WidgetCatalogSnapshot Load(IEnumerable<WidgetSource> sources)
    {
        var widgets = new List<CatalogWidget>();
        var erreurs = new List<string>();

        foreach (var source in sources)
        {
            foreach (var dossier in source.Folders.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                string? texte;
                WidgetManifest? manifeste;
                try
                {
                    texte = dossier.ReadText("widget.json");
                    if (texte is null) continue;
                    manifeste = JsonSerializer.Deserialize<WidgetManifest>(texte, Json);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    erreurs.Add($"{source.Origin}/{dossier.Name} : widget.json illisible ({ex.Message})");
                    continue;
                }

                var refus = manifeste is null ? "widget.json vide" : Validate(manifeste, dossier);
                if (refus is not null || manifeste is null)
                {
                    erreurs.Add($"{source.Origin}/{dossier.Name} : {refus}");
                    continue;
                }

                if (widgets.Any(w => w.Manifest.Id == manifeste.Id))
                {
                    erreurs.Add($"{source.Origin}/{dossier.Name} : l'identifiant « {manifeste.Id} » est déjà pris");
                    continue;
                }

                widgets.Add(new CatalogWidget(manifeste, source.Origin, $"{source.BaseUrl.TrimEnd('/')}/{dossier.Name}/{manifeste.Script}"));
            }
        }

        return new WidgetCatalogSnapshot(widgets, erreurs);
    }

    /// <summary>La raison d'un refus, ou null.</summary>
    public static string? Validate(WidgetManifest m, IWidgetFolder dossier)
    {
        if (m.Contract != ContractVersion) return $"contrat {m.Contract} non pris en charge (attendu : {ContractVersion})";
        if (!Identifiant().IsMatch(m.Id)) return "id : minuscules, chiffres et « - », 64 caractères au plus";
        if (m.Name.Length == 0) return "name manquant";
        if (!Element().IsMatch(m.Element)) return "element : doit commencer par « kv- » (minuscules, chiffres, « - »)";

        // Pas de chargement extérieur : le script est un fichier du dossier du widget (WIDGETS.md §9).
        if (!Script().IsMatch(m.Script) || m.Script.Split('/').Contains(".."))
            return "script : chemin relatif au dossier du widget, en .js";
        if (!dossier.Exists(m.Script)) return $"script « {m.Script} » introuvable";

        if (m.Slots.Count == 0) return "au moins un slot";
        if (m.Slots.Any(s => !Identifiant().IsMatch(s.Name))) return "nom de slot : minuscules, chiffres et « - »";
        if (m.Slots.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != m.Slots.Count) return "deux slots portent le même nom";
        if (m.Slots.Any(s => s.Type != "entity")) return "slot : seul le type « entity » existe";
        if (!m.Slots[0].Required) return "le premier slot (principal) doit être obligatoire";
        if (m.Size.Cols is < 1 or > MaxCols || m.Size.Rows is < 1 or > MaxRows) return $"size : de 1 à {MaxCols} colonnes et de 1 à {MaxRows} lignes";
        return null;
    }

    /// <summary>
    /// À quel point un slot convient à une entité : -1 s'il la refuse, puis 0 (slot sans filtre), 1 (famille),
    /// 2 (classe d'appareil), 3 (usage). Sert au choix du widget par défaut (WIDGETS.md §6).
    /// </summary>
    public static int Score(WidgetSlot slot, RegisteredEntity entity)
    {
        if (slot.Kinds.Count > 0 && !slot.Kinds.Contains(entity.Kind)) return -1;
        if (slot.DeviceClasses.Count > 0 && (entity.DeviceClass is null || !slot.DeviceClasses.Contains(entity.DeviceClass))) return -1;
        if (slot.Usages.Count > 0 && (entity.Usage is null || !slot.Usages.Contains(entity.Usage))) return -1;

        return slot.Usages.Count > 0 ? 3 : slot.DeviceClasses.Count > 0 ? 2 : slot.Kinds.Count > 0 ? 1 : 0;
    }

    /// <summary>Les widgets dont le slot principal accepte l'entité, le meilleur d'abord.</summary>
    public static IReadOnlyList<CatalogWidget> CompatibleWith(WidgetCatalogSnapshot catalog, RegisteredEntity entity) =>
    [
        .. catalog.Widgets
            .Select(w => (Widget: w, Score: Score(w.Manifest.Slots[0], entity)))
            .Where(x => x.Score >= 0)
            // À égalité, un widget installé exprès l'emporte sur un standard (WIDGETS.md §6).
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Widget.IsStandard)
            .ThenBy(x => x.Widget.Manifest.Id, StringComparer.Ordinal)
            .Select(x => x.Widget),
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex Identifiant();

    [GeneratedRegex("^kv-[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Element();

    [GeneratedRegex(@"^[A-Za-z0-9_\-][A-Za-z0-9_\-./]*\.js$")]
    private static partial Regex Script();
}
