using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krissvia.Core.Configuration;

namespace Krissvia.Core.Widgets;

/// <summary>
/// Une carte du tableau de bord. Soit elle <b>suit son entité</b> (<see cref="Widget"/> null, <see cref="Entity"/>) :
/// son widget est celui que l'entité a dans l'écran Entités, ou le plus précis, et ses slots secondaires se lient
/// aux entités du même appareil — changer le widget d'une sonde change toutes ses cartes. Soit elle <b>impose</b>
/// son widget et ses liens (<see cref="Widget"/>, <see cref="Entities"/>). La taille vient du manifeste si la carte
/// n'en fixe pas.
/// </summary>
public sealed record DashboardCard
{
    public required string Id { get; init; }

    /// <summary>Widget imposé par la carte ; null : celui de l'entité (<see cref="Entity"/>).</summary>
    public string? Widget { get; init; }

    /// <summary>L'entité que montre une carte qui suit son entité.</summary>
    public string? Entity { get; init; }

    public IReadOnlyDictionary<string, string> Entities { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Nom montré par la carte à la place de celui de son entité principale ; null : le nom de l'entité. Propre à la
    /// carte : la même sonde peut s'appeler « Salon » ici et « Sonde du buffet » dans l'écran Entités.
    /// </summary>
    public string? Title { get; init; }

    public JsonElement? Settings { get; init; }
    public int? Cols { get; init; }
    public double? Rows { get; init; }
}

/// <summary>Une section d'une page (« Météo », « Thermostats »…) : un titre et une grille de cartes, repliable.</summary>
public sealed record DashboardSection
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public bool Collapsed { get; init; }

    /// <summary>
    /// Largeur de la section en 24ᵉ d'un grand écran ; null : toute la ligne. Deux sections de 12 se partagent une
    /// ligne (« Radiateurs » et « Éclairage » côte à côte). Les colonnes de ses cartes restent celles de la page : une
    /// carte de 1/12 garde sa taille, une carte plus large que la section est ramenée à sa largeur.
    /// </summary>
    public int? Cols { get; init; }

    /// <summary>
    /// Couleur de la section (#RRGGBB) ; null : aucun fond. L'écran n'en peint qu'une teinte légère derrière les cartes,
    /// pour regrouper visuellement sans écraser leurs propres couleurs, en thème clair comme en sombre.
    /// </summary>
    public string? Color { get; init; }

    public IReadOnlyList<DashboardCard> Cards { get; init; } = [];
}

/// <summary>Une page du tableau de bord (« Confort », « Technique »…) : des sections, dans l'ordre.</summary>
public sealed record Dashboard
{
    /// <summary>Vient de l'URL ou du nom du document : inutile (et source d'incohérence) de le répéter dans le corps.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Accueil";

    /// <summary>Place de la page parmi les autres.</summary>
    public int Order { get; init; }

    public IReadOnlyList<DashboardSection> Sections { get; init; } = [];

    /// <summary>
    /// Nombre de colonnes dans lequel les largeurs sont comptées : toujours <see cref="DashboardService.GridColumns"/>
    /// à l'enregistrement. 12 : une page écrite avant le passage à 24 colonnes (29/10/2026), largeurs en douzièmes ;
    /// absent d'une page lue en base : écrite avant le passage à 12, largeurs en sixièmes.
    /// </summary>
    public int? Grid { get; init; }

    [JsonIgnore]
    public IEnumerable<DashboardCard> AllCards => Sections.SelectMany(s => s.Cards);
}

public sealed class DashboardValidationException(string message) : Exception(message);

/// <summary>
/// Pages du tableau de bord, une par document de la collection <c>tableaux</c>. La validation ne vérifie que la
/// forme : un widget ou une entité disparus ne bloquent pas l'enregistrement, la carte les signale à l'affichage
/// (un widget qui échoue ne casse pas le tableau de bord, WIDGETS.md §1).
/// </summary>
public sealed partial class DashboardService(IConfigurationStore store)
{
    public const string Collection = "tableaux";
    public const string MainId = "principal";

    /// <summary>
    /// La grille de 24 colonnes (29/10/2026) : celle de 12 de MudGrid et de DomoMud, coupée en deux pour les cartes de
    /// 1/8 (3) et de 3/8 (9), qu'aucun douzième ne donne. Une carte de 6 = quatre par ligne.
    /// </summary>
    public const int GridColumns = 24;

    /// <summary>La grille de 12 colonnes (19/10 → 29/10/2026) : ses largeurs valent le double en 24ᵉ.</summary>
    public const int TwelveColumnGrid = 12;

    /// <summary>Longueur maximale du nom propre à une carte.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>La première grille (2, 4 ou 6 colonnes selon l'écran) : ses largeurs valent le quadruple en 24ᵉ.</summary>
    private const int PremiereGrille = 6;

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _verrou = new(1, 1);

    /// <summary>Une modification enregistrée (par l'écran ou l'API) : les autres écrans ouverts se mettent à jour.</summary>
    public event Action? Changed;

    /// <summary>Les pages, dans l'ordre ; une page « Accueil » vide si aucune n'a jamais été enregistrée.</summary>
    public async Task<IReadOnlyList<Dashboard>> ListAsync(CancellationToken ct = default)
    {
        var pages = new List<Dashboard>();
        foreach (var document in await store.ListAsync(Collection, ct))
        {
            try
            {
                if (JsonSerializer.Deserialize<Dashboard>(document.Json, Json) is { } page)
                    pages.Add(EnColonnes(page, page.Grid ?? PremiereGrille) with { Id = document.Id });
            }
            catch (JsonException)
            {
                // Une page illisible ne doit pas priver des autres.
            }
        }

        return pages.Count == 0
            ? [new Dashboard { Id = MainId }]
            : [.. pages.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Une page comptée sur une grille plus grossière (<paramref name="grille"/> : 6 ou 12), en 24ᵉ : chaque carte et
    /// chaque section garde exactement sa largeur. Elle sera réécrite en 24ᵉ au prochain enregistrement.
    /// </summary>
    public static Dashboard EnColonnes(Dashboard page, int grille)
    {
        if (grille == GridColumns) return page with { Grid = GridColumns };
        var facteur = GridColumns / grille;
        return page with
        {
            Grid = GridColumns,
            Sections = [.. page.Sections.Select(s => s with
            {
                Cols = s.Cols * facteur,
                Cards = [.. s.Cards.Select(c => c with { Cols = c.Cols * facteur })],
            })],
        };
    }

    /// <summary>
    /// Ce qu'on enregistre : une page envoyée en douzièmes (<c>"grid": 12</c>, un script écrit avant le passage à 24)
    /// est convertie ; sans <c>grid</c>, ses largeurs sont déjà en 24ᵉ (le tableau de bord n'en écrit pas d'autres).
    /// </summary>
    private static Dashboard AEnregistrer(Dashboard page) => EnColonnes(page, page.Grid ?? GridColumns);

    /// <summary>Une page, ou null si elle n'existe pas.</summary>
    public async Task<Dashboard?> GetAsync(string id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(p => p.Id == id);

    /// <summary>Crée ou remplace une page.</summary>
    public async Task<Dashboard> SaveAsync(Dashboard page, CancellationToken ct = default)
    {
        Validate(page);
        page = AEnregistrer(page);
        await _verrou.WaitAsync(ct);
        try
        {
            await EcrireAsync(page, ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return page;
    }

    /// <summary>
    /// Enregistre toutes les pages d'un coup, dans l'ordre donné, et supprime celles qui n'y sont plus : c'est le
    /// « Enregistrer » du mode édition, qui porte sur l'ensemble des modifications (et « Annuler » n'écrit rien).
    /// </summary>
    public async Task<IReadOnlyList<Dashboard>> SaveAllAsync(IReadOnlyList<Dashboard> pages, CancellationToken ct = default)
    {
        if (pages.Count == 0) throw new DashboardValidationException("il faut au moins une page");
        foreach (var page in pages) Validate(page);
        var numerotees = pages.Select((p, i) => AEnregistrer(p) with { Order = i }).ToList();
        if (numerotees.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != numerotees.Count)
            throw new DashboardValidationException("deux pages portent le même identifiant");

        await _verrou.WaitAsync(ct);
        try
        {
            var gardees = numerotees.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var page in numerotees) await EcrireAsync(page, ct);
            foreach (var document in await store.ListAsync(Collection, ct))
                if (!gardees.Contains(document.Id)) await store.DeleteAsync(Collection, document.Id, ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return numerotees;
    }

    /// <summary>Supprime une page ; jamais la dernière. Faux si elle n'existait pas.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        bool supprimee;
        await _verrou.WaitAsync(ct);
        try
        {
            var documents = await store.ListAsync(Collection, ct);
            if (documents.Count == 1 && documents[0].Id == id) throw new DashboardValidationException("on ne supprime pas la dernière page");
            supprimee = await store.DeleteAsync(Collection, id, ct);
        }
        finally
        {
            _verrou.Release();
        }

        if (supprimee) Changed?.Invoke();
        return supprimee;
    }

    /// <summary>Un identifiant de page tiré de son nom, différent de ceux déjà pris.</summary>
    public static string NewPageId(string name, IEnumerable<string> taken)
    {
        var pris = taken.ToHashSet(StringComparer.Ordinal);
        // Un nom sans lettre ni chiffre donnerait le mot de repli des entités (« entite ») : on dit « page ».
        var base_ = name.Any(char.IsLetterOrDigit) ? Registry.EntityIdGenerator.Slugify(name).Replace('_', '-') : "page";
        if (!PageId().IsMatch(base_)) base_ = "page";
        var id = base_;
        for (var i = 2; pris.Contains(id); i++) id = $"{base_}-{i}";
        return id;
    }

    public static void Validate(Dashboard d)
    {
        if (!PageId().IsMatch(d.Id)) throw new DashboardValidationException("identifiant de page : minuscules, chiffres et « - »");
        if (string.IsNullOrWhiteSpace(d.Name)) throw new DashboardValidationException("une page doit avoir un nom");
        if (d.Grid is not (null or GridColumns or TwelveColumnGrid))
            throw new DashboardValidationException($"grid : {GridColumns} (largeurs en 24ᵉ), {TwelveColumnGrid} (en douzièmes), ou absent");
        var max = d.Grid ?? GridColumns;

        var sections = new HashSet<string>(StringComparer.Ordinal);
        var cartes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in d.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id)) throw new DashboardValidationException("une section n'a pas d'identifiant");
            if (!sections.Add(section.Id)) throw new DashboardValidationException($"deux sections portent l'identifiant « {section.Id} »");
            if (section.Cols < 1 || section.Cols > max)
                throw new DashboardValidationException($"section « {section.Id} » : de 1 à {max} colonnes");
            if (section.Color is { } couleur && !HexColor().IsMatch(couleur))
                throw new DashboardValidationException($"section « {section.Id} » : couleur au format #RRGGBB");

            foreach (var carte in section.Cards)
            {
                if (string.IsNullOrWhiteSpace(carte.Id)) throw new DashboardValidationException("une carte n'a pas d'identifiant");
                if (!cartes.Add(carte.Id)) throw new DashboardValidationException($"deux cartes portent l'identifiant « {carte.Id} »");
                if (carte.Widget is null ? string.IsNullOrWhiteSpace(carte.Entity) : string.IsNullOrWhiteSpace(carte.Widget))
                    throw new DashboardValidationException($"carte « {carte.Id} » : il faut un widget, ou une entité dont suivre le widget");
                if (carte.Cols < 1 || carte.Cols > max || carte.Rows is { } lignes && !WidgetCatalog.IsValidRows(lignes))
                    throw new DashboardValidationException($"carte « {carte.Id} » : de 1 à {max} colonnes et de 1 à {WidgetCatalog.MaxRows} lignes, par demi-ligne");
                if (carte.Title is { Length: > MaxTitleLength })
                    throw new DashboardValidationException($"carte « {carte.Id} » : nom de {MaxTitleLength} caractères au plus");
                if (carte.Entities.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)))
                    throw new DashboardValidationException($"carte « {carte.Id} » : slot ou entité vide");
            }
        }
    }

    // Sous le verrou.
    private Task EcrireAsync(Dashboard page, CancellationToken ct) =>
        store.SaveAsync(Collection, new ConfigurationDocument(page.Id, JsonSerializer.Serialize(page with { Grid = GridColumns }, Json)), ct);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex PageId();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
