using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>Un objet de la page perso qui montre la valeur d'une entité.</summary>
public sealed class AffichagePerso
{
    /// <summary>L'identifiant de l'objet dans la page (<c>"id"</c> de sa ligne JSONL).</summary>
    public int Objet { get; set; }
    public string Entite { get; set; } = "";

    /// <summary><c>text</c> (par défaut) pour un libellé ou un bouton, <c>val</c> pour un arc, une barre, un curseur.</summary>
    public string? Attribut { get; set; }

    /// <summary>
    /// Le texte autour de la valeur, <c>{}</c> à sa place : « Salon {} °C ». Sans <c>{}</c>, il suit la valeur (« °C »
    /// donne « 21 °C »). Vide : la valeur seule.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>Pour une entité dont la valeur est un objet (un thermostat) : le champ à montrer (<c>temperature</c>, <c>target</c>).</summary>
    public string? Champ { get; set; }
}

/// <summary>Un bouton de la page perso qui allume ou éteint une entité.</summary>
public sealed class BoutonPerso
{
    public int Objet { get; set; }
    public string Entite { get; set; } = "";
}

/// <summary>La page perso lue : ses lignes prêtes à partir, et ce qui a été écarté.</summary>
public sealed record PagePersoLue(IReadOnlyList<JsonObject> Lignes, IReadOnlyList<string> Ecartees);

/// <summary>
/// La page 5 d'une platine, écrite par l'utilisateur en JSONL (le format des <c>pages.jsonl</c> d'openHASP). Le plugin
/// la pousse avec les pages qu'il génère, et la relie aux entités choisies dans les réglages : des objets qui montrent
/// une valeur, des boutons qui commandent un interrupteur. Fonctions pures.
/// </summary>
public static class PagePerso
{
    /// <summary>Le plus grand identifiant d'objet d'openHASP ; au-delà, la platine ignore la ligne.</summary>
    public const int ObjetMaximum = 254;

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// Lit le JSONL. Chaque ligne devient un objet de la page 5, quelle que soit la page qu'elle annonçait : la page perso
    /// n'a qu'un numéro, et un objet posé ailleurs écraserait une page générée. Une ligne illisible, sans <c>id</c> ou
    /// d'<c>id</c> réservé (le fond de la page, <see cref="Objets.Fond"/>) est écartée et dite — jamais toute la page.
    /// Les lignes vides et les commentaires (<c>//</c>, comme dans un <c>pages.jsonl</c>) sont ignorés.
    /// </summary>
    public static PagePersoLue Lire(string? jsonl)
    {
        var lignes = new List<JsonObject>();
        var ecartees = new List<string>();
        if (string.IsNullOrWhiteSpace(jsonl)) return new PagePersoLue(lignes, ecartees);

        var numero = 0;
        foreach (var brute in jsonl.Split('\n'))
        {
            numero++;
            var ligne = brute.Trim();
            if (ligne.Length == 0 || ligne.StartsWith("//", StringComparison.Ordinal)) continue;

            JsonObject? objet;
            try
            {
                objet = JsonNode.Parse(ligne) as JsonObject;
            }
            catch (JsonException)
            {
                objet = null;
            }

            if (objet is null)
            {
                ecartees.Add($"ligne {numero} : pas un objet JSON");
                continue;
            }

            if (objet["id"] is not JsonValue v || !v.TryGetValue<int>(out var id) || id is < 1 or > ObjetMaximum)
            {
                ecartees.Add($"ligne {numero} : « id » manquant ou hors de 1 à {ObjetMaximum}");
                continue;
            }

            if (id == Objets.Fond)
            {
                ecartees.Add($"ligne {numero} : l'id {Objets.Fond} est celui du fond de la page");
                continue;
            }

            objet["page"] = Objets.PagePerso;
            lignes.Add(objet);
        }

        return new PagePersoLue(lignes, ecartees);
    }

    /// <summary>
    /// La valeur d'une entité, mise en forme pour un objet : nombre à la française, booléen en Oui / Non (Allumé /
    /// Éteint pour un interrupteur ou une lumière), texte tel quel ; « -- » quand l'entité est indisponible ou jamais
    /// reçue — jamais une valeur périmée qui passerait pour une mesure. Pour <c>val</c>, un entier (0 si inconnu).
    /// </summary>
    public static string Valeur(AffichagePerso a, EntityStateUpdate? etat)
    {
        var brute = etat is { Available: true, Value: { } v } ? Champ(v, a.Champ) : null;
        if (a.Attribut == "val")
            return (brute is { ValueKind: JsonValueKind.Number } n ? (int)Math.Round(n.GetDouble()) : brute?.ValueKind == JsonValueKind.True ? 1 : 0)
                .ToString(CultureInfo.InvariantCulture);

        var texte = brute switch
        {
            { ValueKind: JsonValueKind.Number } n => n.GetDouble().ToString("0.#", Francais),
            { ValueKind: JsonValueKind.True or JsonValueKind.False } b => Booleen(b.GetBoolean(), etat),
            { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
            { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False => Booleen(on.GetBoolean(), etat),
            _ => "--",
        };
        return a.Format is not { Length: > 0 } f ? texte
            : f.Contains("{}", StringComparison.Ordinal) ? f.Replace("{}", texte, StringComparison.Ordinal)
            : $"{texte} {f}";
    }

    private static JsonElement? Champ(JsonElement v, string? champ) =>
        champ is { Length: > 0 } ? v.ValueKind == JsonValueKind.Object && v.TryGetProperty(champ, out var c) ? c : null : v;

    private static string Booleen(bool vrai, EntityStateUpdate? etat) =>
        etat?.Kind is EntityKind.Switch or EntityKind.Light ? vrai ? "Allumé" : "Éteint" : vrai ? "Oui" : "Non";

    /// <summary>L'attribut écrit : <c>text</c> sauf demande contraire, et rien d'autre que <c>text</c> ou <c>val</c>.</summary>
    public static string Attribut(AffichagePerso a) => a.Attribut == "val" ? "val" : "text";
}
