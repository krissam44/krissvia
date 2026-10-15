using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Krissvia.PluginHost.Services;

/// <summary>Type d'un réglage de plugin.</summary>
public enum SettingType { String, Integer, Number, Boolean, Object, Array }

/// <summary>
/// Un réglage décrit dans <c>plugin.json</c> (<c>"settings"</c>) — un sous-ensemble de JSON Schema : <c>type</c>,
/// <c>title</c>, <c>description</c>, <c>default</c>, <c>minimum</c>/<c>maximum</c>, <c>required</c>,
/// <c>format: "password"</c>, <c>properties</c> (objet), <c>items</c> (liste d'objets). Les clés sont celles de la
/// configuration du plugin (<c>Mqtt</c> → <c>Host</c>…) : c'est ce qui permet de les lui transmettre telles quelles.
/// </summary>
public sealed record SettingField
{
    public required string Key { get; init; }
    public required SettingType Type { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public JsonNode? Default { get; init; }
    public bool Required { get; init; }

    /// <summary>Mot de passe, jeton : saisi masqué.</summary>
    public bool Secret { get; init; }

    public double? Minimum { get; init; }
    public double? Maximum { get; init; }

    /// <summary>Champs d'un objet, ou de chaque élément d'une liste.</summary>
    public IReadOnlyList<SettingField> Properties { get; init; } = [];

    public string Label => Title.Length > 0 ? Title : Key;
}

/// <summary>Lecture, valeurs par défaut, validation et transmission des réglages de plugin. Pur.</summary>
public static class SettingsSchema
{
    /// <summary>Description racine (un objet), ou null si le plugin n'a pas de réglages.</summary>
    public static SettingField? Parse(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s ? Champ("", s, requis: false) : null;

    private static SettingField Champ(string cle, JsonElement s, bool requis)
    {
        var type = Chaine(s, "type") switch
        {
            "integer" => SettingType.Integer,
            "number" => SettingType.Number,
            "boolean" => SettingType.Boolean,
            "object" => SettingType.Object,
            "array" => SettingType.Array,
            _ => SettingType.String,
        };

        IReadOnlyList<SettingField> proprietes = [];
        var source = type == SettingType.Array && s.TryGetProperty("items", out var items) ? items : s;
        if (source.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var obligatoires = source.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                : [];
            proprietes = props.EnumerateObject().Select(p => Champ(p.Name, p.Value, obligatoires.Contains(p.Name))).ToList();
        }

        return new SettingField
        {
            Key = cle,
            Type = type,
            Title = Chaine(s, "title") ?? "",
            Description = Chaine(s, "description"),
            Default = s.TryGetProperty("default", out var d) ? JsonNode.Parse(d.GetRawText()) : null,
            Required = requis,
            Secret = Chaine(s, "format") == "password",
            Minimum = s.TryGetProperty("minimum", out var min) && min.TryGetDouble(out var vmin) ? vmin : null,
            Maximum = s.TryGetProperty("maximum", out var max) && max.TryGetDouble(out var vmax) ? vmax : null,
            Properties = proprietes,
        };
    }

    /// <summary>Un objet complet pour ce champ : la valeur enregistrée, complétée par les valeurs par défaut.</summary>
    public static JsonObject Complete(SettingField objet, JsonObject? enregistre)
    {
        var resultat = new JsonObject();
        foreach (var champ in objet.Properties)
        {
            var valeur = enregistre?[champ.Key];
            resultat[champ.Key] = champ.Type switch
            {
                SettingType.Object => Complete(champ, valeur as JsonObject),
                SettingType.Array => valeur is JsonArray liste
                    ? new JsonArray(liste.Select(e => (JsonNode?)Complete(champ, e as JsonObject)).ToArray())
                    : champ.Default?.DeepClone() ?? new JsonArray(),
                _ => valeur?.DeepClone() ?? champ.Default?.DeepClone(),
            };
        }

        return resultat;
    }

    /// <summary>Les erreurs, lisibles telles quelles (libellé du champ, et numéro de ligne dans une liste).</summary>
    public static IReadOnlyList<string> Validate(SettingField objet, JsonObject valeur)
    {
        var erreurs = new List<string>();
        Valider(objet, valeur, "", erreurs);
        return erreurs;
    }

    private static void Valider(SettingField objet, JsonObject valeur, string chemin, List<string> erreurs)
    {
        foreach (var champ in objet.Properties)
        {
            var nom = chemin + champ.Label;
            var v = valeur[champ.Key];
            if (v is null || (v is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)))
            {
                if (champ.Required) erreurs.Add($"{nom} : obligatoire.");
                continue;
            }

            switch (champ.Type)
            {
                case SettingType.Object:
                    if (v is JsonObject o) Valider(champ, o, nom + " › ", erreurs);
                    else erreurs.Add($"{nom} : objet attendu.");
                    break;
                case SettingType.Array:
                    if (v is not JsonArray liste) { erreurs.Add($"{nom} : liste attendue."); break; }
                    if (champ.Required && liste.Count == 0) erreurs.Add($"{nom} : au moins un élément.");
                    for (var i = 0; i < liste.Count; i++)
                    {
                        if (liste[i] is JsonObject element) Valider(champ, element, $"{nom} n° {i + 1} › ", erreurs);
                        else erreurs.Add($"{nom} n° {i + 1} : objet attendu.");
                    }
                    break;
                case SettingType.Boolean:
                    if (v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) erreurs.Add($"{nom} : oui ou non attendu.");
                    break;
                case SettingType.Integer or SettingType.Number:
                    if (v.GetValueKind() != JsonValueKind.Number || !v.AsValue().TryGetValue<double>(out var n))
                    {
                        erreurs.Add($"{nom} : nombre attendu.");
                        break;
                    }

                    if (champ.Type == SettingType.Integer && n != Math.Floor(n)) erreurs.Add($"{nom} : nombre entier attendu.");
                    if (champ.Minimum is { } min && n < min) erreurs.Add($"{nom} : au moins {min.ToString(CultureInfo.CurrentCulture)}.");
                    if (champ.Maximum is { } max && n > max) erreurs.Add($"{nom} : au plus {max.ToString(CultureInfo.CurrentCulture)}.");
                    break;
                default:
                    if (v.GetValueKind() != JsonValueKind.String) erreurs.Add($"{nom} : texte attendu.");
                    break;
            }
        }
    }

    /// <summary>
    /// Les réglages en variables d'environnement de configuration .NET (<c>Shelly__Devices__0__Host</c>). Une valeur
    /// vide n'est pas transmise : elle ne doit pas écraser ce que le plugin trouve dans son propre fichier.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToEnvironment(JsonNode? valeur)
    {
        var resultat = new Dictionary<string, string>(StringComparer.Ordinal);
        Aplatir(valeur, "", resultat);
        return resultat;
    }

    private static void Aplatir(JsonNode? noeud, string prefixe, Dictionary<string, string> resultat)
    {
        string Cle(string suite) => prefixe.Length == 0 ? suite : $"{prefixe}__{suite}";

        switch (noeud)
        {
            case JsonObject o:
                foreach (var (cle, v) in o) Aplatir(v, Cle(cle), resultat);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) Aplatir(a[i], Cle(i.ToString(CultureInfo.InvariantCulture)), resultat);
                break;
            case JsonValue v when prefixe.Length > 0:
                var texte = v.GetValueKind() switch
                {
                    JsonValueKind.String => v.GetValue<string>(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => v.ToJsonString(),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(texte)) resultat[prefixe] = texte;
                break;
        }
    }

    private static string? Chaine(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
