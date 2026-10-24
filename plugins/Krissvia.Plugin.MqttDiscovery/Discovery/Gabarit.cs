using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Krissvia.Plugin.MqttDiscovery.Discovery;

/// <summary>
/// Le sous-ensemble des gabarits Jinja de Home Assistant que publient vraiment les appareils : <c>{{ value }}</c>,
/// <c>{{ value_json.temperature }}</c>, <c>{{ value_json['x'].y }}</c>, suivis de filtres simples (<c>float</c>,
/// <c>int</c>, <c>round(1)</c>, <c>lower</c>, <c>upper</c>, <c>trim</c>, <c>string</c>, <c>is_defined</c>).
/// </summary>
/// <remarks>
/// Pas d'interpréteur Jinja : un gabarit hors de ce sous-ensemble (calcul, condition) est REFUSÉ et l'entité n'est pas
/// déclarée, avec un avertissement. Une valeur fausse affichée comme une mesure serait pire qu'une entité absente.
/// </remarks>
internal sealed partial class Gabarit
{
    private readonly string[] _chemin;
    private readonly bool _json;
    private readonly IReadOnlyList<(string Nom, string? Argument)> _filtres;

    private Gabarit(string source, bool supporte, bool json, string[] chemin, IReadOnlyList<(string, string?)> filtres)
    {
        Source = source;
        Supporte = supporte;
        _json = json;
        _chemin = chemin;
        _filtres = filtres;
    }

    public string Source { get; }

    /// <summary>Faux : gabarit hors du sous-ensemble ; <see cref="Evaluer"/> ne rend jamais rien.</summary>
    public bool Supporte { get; }

    private static readonly HashSet<string> FiltresConnus = ["float", "int", "round", "lower", "upper", "trim", "string", "is_defined"];

    /// <summary>Null si aucun gabarit n'est configuré : la charge brute est la valeur.</summary>
    public static Gabarit? Lire(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        var m = Forme().Match(source);
        if (!m.Success) return Refuse(source);

        var chemin = new List<string>();
        foreach (Capture acces in m.Groups["acces"].Captures)
        {
            var a = acces.Value.Trim();
            chemin.Add(a.StartsWith('.') ? a[1..] : a[1..^1].Trim().Trim('\'', '"'));
        }

        var filtres = new List<(string, string?)>();
        foreach (Capture f in m.Groups["filtre"].Captures)
        {
            var fm = Filtre().Match(f.Value);
            var nom = fm.Groups["nom"].Value;
            if (!FiltresConnus.Contains(nom)) return Refuse(source);
            filtres.Add((nom, fm.Groups["arg"].Success ? fm.Groups["arg"].Value.Trim().Trim('\'', '"') : null));
        }

        return new Gabarit(source, true, m.Groups["racine"].Value == "value_json", [.. chemin], filtres);
    }

    private static Gabarit Refuse(string source) => new(source, false, false, [], []);

    /// <summary>Le texte que rendrait Home Assistant, ou null : charge illisible, clé absente, filtre en échec.</summary>
    public string? Evaluer(string charge)
    {
        if (!Supporte) return null;

        string? texte;
        if (!_json)
        {
            texte = charge;
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(charge);
                var element = document.RootElement;
                foreach (var cle in _chemin)
                {
                    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(cle, out var enfant)) element = enfant;
                    else if (element.ValueKind == JsonValueKind.Array && int.TryParse(cle, out var i) && i >= 0 && i < element.GetArrayLength())
                        element = element[i];
                    else return null;
                }

                texte = Rendre(element);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        foreach (var (nom, argument) in _filtres)
        {
            if (texte is null) return null;
            texte = Appliquer(nom, argument, texte);
        }

        return texte;
    }

    private static string? Appliquer(string nom, string? argument, string texte)
    {
        switch (nom)
        {
            case "float":
                return Nombre(texte) is { } f ? Python(f) : argument;
            case "int":
                return Nombre(texte) is { } n ? ((long)Math.Truncate(n)).ToString(CultureInfo.InvariantCulture) : argument;
            case "round":
                var decimales = int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0;
                return Nombre(texte) is { } r ? Python(Math.Round(r, Math.Clamp(decimales, 0, 15), MidpointRounding.AwayFromZero)) : null;
            case "lower":
                return texte.ToLowerInvariant();
            case "upper":
                return texte.ToUpperInvariant();
            case "trim":
                return texte.Trim();
            default:   // string, is_defined : la valeur telle quelle (une clé absente a déjà rendu null)
                return texte;
        }
    }

    /// <summary>Un élément JSON rendu comme le fait Jinja : True/False pour un booléen, None (ici : rien) pour null.</summary>
    private static string? Rendre(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };

    private static double? Nombre(string texte) =>
        double.TryParse(texte.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>Un flottant comme Python l'écrit : 21 → « 21.0 ».</summary>
    private static string Python(double v)
    {
        var s = v.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }

    [GeneratedRegex(@"^\s*\{\{\s*(?<racine>value_json|value)(?<acces>\s*(\.[A-Za-z_][A-Za-z0-9_]*|\[\s*('[^']*'|""[^""]*""|\d+)\s*\]))*\s*(?<filtre>\|\s*[A-Za-z_]+\s*(\(\s*[^()]*\))?\s*)*\}\}\s*$")]
    private static partial Regex Forme();

    [GeneratedRegex(@"^\|\s*(?<nom>[A-Za-z_]+)\s*(\(\s*(?<arg>[^()]*)\))?\s*$")]
    private static partial Regex Filtre();
}
