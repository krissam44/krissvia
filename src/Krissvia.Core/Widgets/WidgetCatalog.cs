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

/// <summary>
/// Taille d'une carte : <see cref="Cols"/> en douzièmes de la largeur d'un grand écran (3 = un quart, comme un
/// <c>lg="3"</c> de MudGrid), <see cref="Rows"/> en lignes, par demi-ligne (1, 1,5, 2…).
/// </summary>
public sealed record WidgetSize(int Cols = 1, double Rows = 1);

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

    /// <summary>
    /// Widget Blazor : le nom complet du composant, à la place de <see cref="Element"/> et <see cref="Script"/>
    /// (WIDGETS.md §2.2). Rendu par le serveur, dans la page, au lieu d'un élément créé dans le navigateur.
    /// </summary>
    public string? Blazor { get; init; }
    public IReadOnlyList<WidgetSlot> Slots { get; init; } = [];
    public WidgetSize Size { get; init; } = new();
    public JsonElement? Settings { get; init; }
}

/// <summary>Un widget du catalogue : son manifeste, d'où il vient et l'adresse de son script.</summary>
/// <param name="Origin"><c>standard</c> (livré avec Krissvia) ou <c>local</c> (dossier de l'installation).</param>
/// <param name="ScriptUrl">Vide pour un widget Blazor.</param>
public sealed record CatalogWidget(WidgetManifest Manifest, string Origin, string ScriptUrl)
{
    public bool IsStandard => Origin == WidgetCatalog.Standard;

    public bool IsBlazor => Manifest.Blazor is not null;
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
    /// <summary>
    /// Les sous-dossiers d'un répertoire du disque (aucun s'il n'existe pas). Un dossier dont le nom commence par un
    /// point est ignoré : c'est là qu'une installation écrit avant de basculer (<see cref="WidgetInstaller"/>).
    /// </summary>
    public static WidgetSource FromDirectory(string directory, string origin, string baseUrl) =>
        new(origin, baseUrl, System.IO.Directory.Exists(directory)
            ? [.. System.IO.Directory.GetDirectories(directory).Where(d => !Path.GetFileName(d).StartsWith('.')).Select(d => new DossierDisque(d))]
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

    /// <summary>Préfixe de l'origine des widgets livrés par un plugin : <c>plugin:meteo</c>.</summary>
    public const string PluginOriginPrefix = "plugin:";

    /// <summary>Adresse sous laquelle le serveur publie les widgets des plugins : <c>/widgets/plugins/{id}/…</c>.</summary>
    public const string PluginBaseUrl = "/widgets/plugins";

    public static string PluginOrigin(string pluginId) => PluginOriginPrefix + pluginId;

    /// <summary>Le plugin d'où vient un widget, ou null (standard, local, Blazor).</summary>
    public static string? PluginOf(string origin) =>
        origin.StartsWith(PluginOriginPrefix, StringComparison.Ordinal) ? origin[PluginOriginPrefix.Length..] : null;

    /// <summary>
    /// Le fichier qu'un navigateur demande sous un dossier de widgets, ou null s'il en sortirait. Le chemin vient de
    /// l'URL : « .. », chemin absolu ou lecteur Windows ne doivent rien servir d'autre que les widgets — c'est le dossier
    /// d'un plugin, où se trouvent aussi ses réglages. (Un lien symbolique posé DANS le dossier par l'administrateur
    /// est suivi : il est de son fait.)
    /// </summary>
    public static string? SafeFile(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':')) return null;
        var segments = relativePath.Split('/');
        if (segments.Any(s => s is "" or "." or "..")) return null;

        var racine = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var chemin = Path.GetFullPath(Path.Combine(racine, Path.Combine(segments)));
        return chemin.StartsWith(racine, StringComparison.Ordinal) && File.Exists(chemin) ? chemin : null;
    }

    /// <summary>Origine des widgets Blazor, déclarés dans les bibliothèques de <c>Widgets:Assemblies</c>.</summary>
    public const string Blazor = "blazor";
    /// <summary>Contrat 2 : largeurs en douzièmes. Le contrat 1 (largeurs en sixièmes) est encore lu, converti.</summary>
    public const int ContractVersion = 2;

    /// <summary>Lit un <c>widget.json</c> ; lève <see cref="JsonException"/> s'il est mal formé.</summary>
    public static WidgetManifest? ReadManifest(string json) => JsonSerializer.Deserialize<WidgetManifest>(json, Json);

    /// <summary>Le premier contrat, dont la grille n'avait que 6 colonnes.</summary>
    public const int LegacyContractVersion = 1;

    /// <summary>Largeur maximale : 12 colonnes, toute la largeur d'un grand écran (la grille de MudGrid et de DomoMud).</summary>
    public const int MaxCols = 12;

    public const int MaxRows = 4;

    /// <summary>
    /// Hauteur valable : de 1 à <see cref="MaxRows"/> lignes, par demi-ligne. Une ligne entière laissait le choix entre
    /// une carte trop serrée (la carte Énergie coupée en 1) et une carte à moitié vide (en 2).
    /// </summary>
    public static bool IsValidRows(double rows) => rows is >= 1 and <= MaxRows && rows * 2 == Math.Floor(rows * 2);

    /// <summary>Le widget générique, qui sait tout afficher : le dernier recours (WIDGETS.md §6).</summary>
    public const string FallbackId = "valeur";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <param name="components">Les manifestes des widgets Blazor, tirés des attributs de leurs composants.</param>
    public static WidgetCatalogSnapshot Load(IEnumerable<WidgetSource> sources, IEnumerable<WidgetManifest>? components = null)
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
                    manifeste = ReadManifest(texte);
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

                // Contrat 1 : largeur en sixièmes, la même carte en douzièmes.
                if (manifeste.Contract == LegacyContractVersion)
                    manifeste = manifeste with { Size = manifeste.Size with { Cols = manifeste.Size.Cols * 2 } };

                widgets.Add(new CatalogWidget(manifeste, source.Origin, $"{source.BaseUrl.TrimEnd('/')}/{dossier.Name}/{manifeste.Script}"));
            }
        }

        foreach (var composant in components ?? [])
        {
            if (Validate(composant, null) is { } refus)
                erreurs.Add($"{Blazor}/{composant.Blazor} : {refus}");
            else if (widgets.Any(w => w.Manifest.Id == composant.Id))
                erreurs.Add($"{Blazor}/{composant.Blazor} : l'identifiant « {composant.Id} » est déjà pris");
            else
                widgets.Add(new CatalogWidget(composant, Blazor, ""));
        }

        return new WidgetCatalogSnapshot(widgets, erreurs);
    }

    /// <summary>La raison d'un refus, ou null.</summary>
    /// <param name="dossier">Le dossier d'un Web Component ; null pour un widget Blazor, qui n'a pas de script.</param>
    public static string? Validate(WidgetManifest m, IWidgetFolder? dossier)
    {
        if (m.Contract is not (ContractVersion or LegacyContractVersion))
            return $"contrat {m.Contract} non pris en charge (attendu : {ContractVersion})";
        if (!Identifiant().IsMatch(m.Id)) return "id : minuscules, chiffres et « - », 64 caractères au plus";
        if (m.Name.Length == 0) return "name manquant";

        if (dossier is null)
        {
            if (string.IsNullOrWhiteSpace(m.Blazor)) return "blazor : nom du composant manquant";
        }
        else
        {
            if (m.Blazor is not null) return "blazor : un widget Blazor se déclare par un attribut, pas dans un dossier";
            if (!Element().IsMatch(m.Element)) return "element : doit commencer par « kv- » (minuscules, chiffres, « - »)";

            // Pas de chargement extérieur : le script est un fichier du dossier du widget (WIDGETS.md §9).
            if (!Script().IsMatch(m.Script) || m.Script.Split('/').Contains(".."))
                return "script : chemin relatif au dossier du widget, en .js";
            if (!dossier.Exists(m.Script)) return $"script « {m.Script} » introuvable";
        }

        if (m.Slots.Count == 0) return "au moins un slot";
        if (m.Slots.Any(s => !Identifiant().IsMatch(s.Name))) return "nom de slot : minuscules, chiffres et « - »";
        if (m.Slots.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != m.Slots.Count) return "deux slots portent le même nom";
        if (m.Slots.Any(s => s.Type != "entity")) return "slot : seul le type « entity » existe";
        if (!m.Slots[0].Required) return "le premier slot (principal) doit être obligatoire";
        var maxCols = m.Contract == LegacyContractVersion ? MaxCols / 2 : MaxCols;
        if (m.Size.Cols < 1 || m.Size.Cols > maxCols || !IsValidRows(m.Size.Rows))
            return $"size : de 1 à {maxCols} colonnes et de 1 à {MaxRows} lignes, par demi-ligne";
        return null;
    }

    /// <summary>
    /// À quel point un slot convient à une entité : -1 s'il la refuse, puis 0 (slot sans filtre), 1 (famille),
    /// 2 (classe d'appareil), 3 (usage). Sert au choix du widget par défaut (WIDGETS.md §6).
    /// </summary>
    /// <param name="ignoreUsage">
    /// Vrai pour un choix EXPLICITE (écran Entités, carte) : l'usage du slot ne refuse plus l'entité. Un widget
    /// « spot » pour une lumière d'usage éclairage, un « chauffe-eau » pour une prise : c'est l'utilisateur qui sait.
    /// La famille et la classe d'appareil, elles, restent exigées — le widget ne saurait pas lire une autre valeur.
    /// </param>
    public static int Score(WidgetSlot slot, RegisteredEntity entity, bool ignoreUsage = false)
    {
        if (slot.Kinds.Count > 0 && !slot.Kinds.Contains(entity.Kind)) return -1;
        if (slot.DeviceClasses.Count > 0 && (entity.DeviceClass is null || !slot.DeviceClasses.Contains(entity.DeviceClass))) return -1;
        var usageAccepte = slot.Usages.Count == 0 || (EntityUsages.Of(entity) is { } usage && slot.Usages.Contains(usage));
        if (!usageAccepte && !ignoreUsage) return -1;

        return usageAccepte && slot.Usages.Count > 0 ? 3 : slot.DeviceClasses.Count > 0 ? 2 : slot.Kinds.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Les widgets qu'on peut CHOISIR pour une entité : ceux qui l'acceptent, le meilleur d'abord, puis ceux qui ne
    /// diffèrent que par l'usage (voir <see cref="IsOtherUsage"/>), par nom. Le choix automatique, lui, ne prend que
    /// les premiers (<see cref="CompatibleWith"/>).
    /// </summary>
    public static IReadOnlyList<CatalogWidget> ChoosableFor(WidgetCatalogSnapshot catalog, RegisteredEntity entity) =>
    [
        .. CompatibleWith(catalog, entity),
        .. catalog.Widgets
            .Where(w => IsOtherUsage(w, entity))
            .OrderBy(w => w.Manifest.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>Vrai si le widget accepte l'entité seulement en ignorant l'usage de son slot principal.</summary>
    public static bool IsOtherUsage(CatalogWidget widget, RegisteredEntity entity) =>
        Score(widget.Manifest.Slots[0], entity) < 0 && Score(widget.Manifest.Slots[0], entity, ignoreUsage: true) >= 0;

    /// <summary>Les widgets dont le slot principal accepte l'entité, le meilleur d'abord.</summary>
    public static IReadOnlyList<CatalogWidget> CompatibleWith(WidgetCatalogSnapshot catalog, RegisteredEntity entity) =>
    [
        .. catalog.Widgets
            .Select(w => (Widget: w, Score: Score(w.Manifest.Slots[0], entity)))
            .Where(x => x.Score >= 0)
            // À égalité, un widget installé exprès l'emporte sur un standard (WIDGETS.md §6).
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Widget.IsStandard)
            // Puis le plus spécifique : un slot qui n'accepte que la puissance apparente (Linky) passe devant celui
            // qui accepte toute mesure électrique.
            .ThenBy(x => x.Widget.Manifest.Slots[0].DeviceClasses.Count is var n && n > 0 ? n : int.MaxValue)
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
