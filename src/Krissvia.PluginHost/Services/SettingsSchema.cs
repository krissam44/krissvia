using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Krissvia.PluginHost.Services;

/// <summary>Type d'un réglage de plugin.</summary>
public enum SettingType { String, Integer, Number, Boolean, Object, Array }

/// <summary>Ce qu'un plugin a le droit de faire d'une entité qui n'est pas à lui (contrat 0.3).</summary>
public enum EntityAccess { None, Read, Command }

/// <summary>
/// Un réglage qui désigne une entité (<c>"format": "entity"</c>) : la page Services propose une liste filtrée, et
/// l'entité choisie devient une autorisation du plugin (docs/CONTRACT-V0.3.md §2).
/// </summary>
public sealed record EntitySetting(IReadOnlyList<string> Kinds, IReadOnlyList<string> DeviceClasses, EntityAccess Access);

/// <summary>
/// Un réglage décrit dans <c>plugin.json</c> (<c>"settings"</c>) — un sous-ensemble de JSON Schema : <c>type</c>,
/// <c>title</c>, <c>description</c>, <c>default</c>, <c>minimum</c>/<c>maximum</c>, <c>required</c>,
/// <c>format: "password"</c>, <c>format: "textarea"</c> (texte sur plusieurs lignes), <c>properties</c> (objet), <c>items</c> et <c>maxItems</c> (liste d'objets). Les clés sont celles de la
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

    /// <summary>
    /// Texte de plusieurs lignes (<c>"format": "textarea"</c>) : la page perso d'une platine, en JSONL. Saisi sur toute la
    /// largeur, en police à chasse fixe ; dans un tableau, le nombre de lignes au lieu du contenu.
    /// </summary>
    public bool Multiline { get; init; }

    public double? Minimum { get; init; }
    public double? Maximum { get; init; }

    /// <summary>Liste : nombre d'éléments au plus (<c>maxItems</c>) — six températures sur la page 2 d'une platine.</summary>
    public int? MaxItems { get; init; }

    /// <summary>Champs d'un objet, ou de chaque élément d'une liste.</summary>
    public IReadOnlyList<SettingField> Properties { get; init; } = [];

    /// <summary>Renseigné si le champ désigne une entité.</summary>
    public EntitySetting? Entity { get; init; }

    /// <summary>
    /// Objet que le cœur sait remplir lui-même (<c>"x-shared": "mqtt"</c> : le broker commun, pastille MQTT). Ses champs
    /// obligatoires ne le sont plus : vide, le plugin reçoit la valeur commune.
    /// </summary>
    public string? Shared { get; init; }

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
            Multiline = type == SettingType.String && Chaine(s, "format") == "textarea",
            Minimum = s.TryGetProperty("minimum", out var min) && min.TryGetDouble(out var vmin) ? vmin : null,
            Maximum = s.TryGetProperty("maximum", out var max) && max.TryGetDouble(out var vmax) ? vmax : null,
            Properties = proprietes,
            MaxItems = type == SettingType.Array && s.TryGetProperty("maxItems", out var mx) && mx.TryGetInt32(out var vmx) ? vmx : null,
            Shared = type == SettingType.Object ? Chaine(s, "x-shared") : null,
            Entity = type == SettingType.String && Chaine(s, "format") == "entity"
                ? new EntitySetting(Liste(s, "x-kinds"), Liste(s, "x-device-classes"),
                    Chaine(s, "x-access") == "command" ? EntityAccess.Command : EntityAccess.Read)
                : null,
        };
    }

    private static IReadOnlyList<string> Liste(JsonElement s, string nom) =>
        s.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    /// <summary>
    /// Les entités choisies dans les réglages, où qu'elles soient (objets, listes), avec le droit le plus large qu'un de
    /// leurs champs accorde. C'est l'autorisation du plugin : choisir l'entité, c'est l'autoriser.
    /// </summary>
    public static IReadOnlyDictionary<string, EntityAccess> EntityReferences(SettingField objet, JsonObject? valeur)
    {
        var resultat = new Dictionary<string, EntityAccess>(StringComparer.Ordinal);
        Parcourir(objet, valeur, resultat);
        return resultat;
    }

    private static void Parcourir(SettingField objet, JsonObject? valeur, Dictionary<string, EntityAccess> resultat)
    {
        if (valeur is null) return;
        foreach (var champ in objet.Properties)
        {
            switch (champ.Type, valeur[champ.Key])
            {
                case (SettingType.Object, JsonObject o):
                    Parcourir(champ, o, resultat);
                    break;
                case (SettingType.Array, JsonArray liste):
                    foreach (var element in liste.OfType<JsonObject>()) Parcourir(champ, element, resultat);
                    break;
                case (SettingType.String, JsonValue v) when champ.Entity is { } e && v.TryGetValue<string>(out var id) && id.Length > 0:
                    resultat[id] = resultat.TryGetValue(id, out var deja) && deja > e.Access ? deja : e.Access;
                    break;
            }
        }
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

    /// <summary>Les sections partagées d'un plugin, avec leur chemin de configuration (<c>Mqtt</c>) et leur nature (<c>mqtt</c>).</summary>
    public static IReadOnlyList<(string Path, string Kind)> SharedSections(SettingField? racine)
    {
        var sections = new List<(string, string)>();
        void Parcourir(SettingField objet, string chemin)
        {
            foreach (var champ in objet.Properties.Where(c => c.Type == SettingType.Object))
            {
                var ici = chemin.Length == 0 ? champ.Key : $"{chemin}__{champ.Key}";
                if (champ.Shared is { Length: > 0 } nature) sections.Add((ici, nature));
                else Parcourir(champ, ici);
            }
        }

        if (racine is not null) Parcourir(racine, "");
        return sections;
    }

    private static void Valider(SettingField objet, JsonObject valeur, string chemin, List<string> erreurs, bool partage = false)
    {
        foreach (var champ in objet.Properties)
        {
            var nom = chemin + champ.Label;
            var v = valeur[champ.Key];
            if (v is null || (v is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)))
            {
                // Dans une section partagée, le cœur fournit ce qui manque (broker commun).
                if (champ.Required && !partage) erreurs.Add($"{nom} : obligatoire.");
                continue;
            }

            switch (champ.Type)
            {
                case SettingType.Object:
                    if (v is JsonObject o) Valider(champ, o, nom + " › ", erreurs, partage || champ.Shared is not null);
                    else erreurs.Add($"{nom} : objet attendu.");
                    break;
                case SettingType.Array:
                    if (v is not JsonArray liste) { erreurs.Add($"{nom} : liste attendue."); break; }
                    if (champ.Required && liste.Count == 0) erreurs.Add($"{nom} : au moins un élément.");
                    if (champ.MaxItems is { } plafond && liste.Count > plafond) erreurs.Add($"{nom} : {plafond} au plus.");
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
                    if (!TryGetNumber(v, out var n))
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

    /// <summary>
    /// Le nombre d'une valeur de réglage, qu'elle vienne d'un JSON lu (un <c>JsonElement</c> derrière) ou du formulaire,
    /// qui écrit un entier en <c>long</c>. <c>JsonValue.GetValue&lt;double&gt;()</c> sur ce <c>long</c> LÈVE au lieu de
    /// convertir, et <c>TryGetValue&lt;double&gt;</c> répond faux : taper « 2 » dans un champ entier tuait la page
    /// (exception au rendu, circuit Blazor perdu), et la validation aurait de toute façon refusé « pas un nombre ».
    /// Vécu le 29/10/2026 sur le n° d'objet de la page perso openHASP.
    /// </summary>
    public static bool TryGetNumber(JsonNode? noeud, out double valeur)
    {
        valeur = 0;
        return noeud is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out valeur);
    }

    private static string? Chaine(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
