using System.Text.Json;
using System.Text.Json.Nodes;

namespace Krissvia.Plugin.MqttDiscovery.Discovery;

/// <summary>Une source de joignabilité : le topic, et ce qu'il publie quand l'appareil est en ligne ou hors ligne.</summary>
internal sealed record SourceDispo(string Topic, string EnLigne, string HorsLigne, Gabarit? Gabarit);

/// <summary>L'appareil physique auquel une configuration rattache son entité.</summary>
internal sealed record AppareilHa(string Id, string? Nom, string? Fabricant, string? Modele);

/// <summary>
/// Une configuration MQTT Discovery lue : clés abrégées développées, <c>~</c> remplacé, valeurs par défaut de Home
/// Assistant appliquées. Rien ici ne parle encore au cœur.
/// </summary>
internal sealed record ConfigHa
{
    /// <summary><c>sensor</c>, <c>binary_sensor</c>, <c>switch</c>, <c>light</c>… (deuxième segment du topic).</summary>
    public required string Composant { get; init; }

    /// <summary>L'identité de l'entité pour le cœur : appareil + <c>unique_id</c> (voir <see cref="ConfigParser"/>).</summary>
    public required string UniqueId { get; init; }

    public required AppareilHa Appareil { get; init; }
    public string? Nom { get; init; }
    public string? EtatTopic { get; init; }
    public string? CommandeTopic { get; init; }
    public Gabarit? Gabarit { get; init; }
    public string? ClasseHa { get; init; }
    public string? Unite { get; init; }
    public string? ClasseEtat { get; init; }
    public string? Categorie { get; init; }

    /// <summary><c>payload_on</c> / <c>payload_off</c> : ce qu'on publie pour commander (et ce qu'on lit, à défaut de <c>state_on</c>).</summary>
    public string ChargeOn { get; init; } = "ON";
    public string ChargeOff { get; init; } = "OFF";
    public string EtatOn { get; init; } = "ON";
    public string EtatOff { get; init; } = "OFF";

    /// <summary>Lumière : schéma <c>json</c> (ESPHome, Zigbee2MQTT) ou <c>default</c> (une charge ON/OFF par topic).</summary>
    public bool SchemaJson { get; init; }
    public bool Luminosite { get; init; }
    public double EchelleLuminosite { get; init; } = 255;

    public IReadOnlyList<SourceDispo> Dispo { get; init; } = [];

    /// <summary><c>latest</c> (défaut), <c>all</c> ou <c>any</c>.</summary>
    public string ModeDispo { get; init; } = "latest";
}

/// <summary>Lecture d'une configuration MQTT Discovery, telle que la publient ESPHome, Tasmota ou Zigbee2MQTT.</summary>
internal static class ConfigParser
{
    /// <summary>
    /// Les abréviations de Home Assistant (<c>homeassistant/components/mqtt/abbreviations.py</c>), réduites à ce que le
    /// plugin lit. ESPHome n'envoie QUE la forme abrégée : une clé oubliée ici est une clé ignorée.
    /// </summary>
    private static readonly Dictionary<string, string> Abreviations = new(StringComparer.Ordinal)
    {
        ["avty"] = "availability",
        ["avty_mode"] = "availability_mode",
        ["avty_t"] = "availability_topic",
        ["avty_tpl"] = "availability_template",
        ["bri_scl"] = "brightness_scale",
        ["cmd_t"] = "command_topic",
        ["dev"] = "device",
        ["dev_cla"] = "device_class",
        ["ent_cat"] = "entity_category",
        ["obj_id"] = "object_id",
        ["pl_avail"] = "payload_available",
        ["pl_not_avail"] = "payload_not_available",
        ["pl_on"] = "payload_on",
        ["pl_off"] = "payload_off",
        ["stat_cla"] = "state_class",
        ["stat_off"] = "state_off",
        ["stat_on"] = "state_on",
        ["stat_t"] = "state_topic",
        ["stat_val_tpl"] = "state_value_template",
        ["t"] = "topic",
        ["uniq_id"] = "unique_id",
        ["unit_of_meas"] = "unit_of_measurement",
        ["val_tpl"] = "value_template",
    };

    private static readonly Dictionary<string, string> AbreviationsAppareil = new(StringComparer.Ordinal)
    {
        ["cns"] = "connections",
        ["ids"] = "identifiers",
        ["mf"] = "manufacturer",
        ["mdl"] = "model",
        ["sw"] = "sw_version",
    };

    /// <summary>
    /// La configuration publiée sur <paramref name="topic"/> (<c>prefixe/composant/[noeud/]objet/config</c>), ou null si
    /// le topic n'en est pas une ou si la charge est illisible.
    /// </summary>
    public static ConfigHa? Lire(string prefixe, string topic, string charge)
    {
        if (Composant(prefixe, topic) is not { } composant) return null;

        JsonObject brut;
        try
        {
            if (JsonNode.Parse(charge) is not JsonObject o) return null;
            brut = o;
        }
        catch (JsonException)
        {
            return null;
        }

        var c = Developper(brut, Abreviations);
        var baseTopic = Texte(c, "~");

        string? Topic(string cle) => Tilde(Texte(c, cle), baseTopic);

        var appareil = Appareil(c["device"] as JsonObject);
        var objet = topic[(prefixe.Length + 1)..^"/config".Length];   // « sensor/esp-salon/temperature »

        // ESPHome, par défaut, donne le MÊME unique_id à la même sonde de deux modules (« ESPsensortemperature ») :
        // préfixé par l'appareil, il ne fait plus confondre la température du salon avec celle de la chambre.
        var uniqueId = Texte(c, "unique_id") is { Length: > 0 } u ? u : objet;
        var idAppareil = appareil?.Id ?? uniqueId;

        var chargeOn = Texte(c, "payload_on") ?? "ON";
        var chargeOff = Texte(c, "payload_off") ?? "OFF";

        return new ConfigHa
        {
            Composant = composant,
            UniqueId = appareil is null ? uniqueId : $"{idAppareil}/{uniqueId}",
            Appareil = appareil ?? new AppareilHa(idAppareil, null, null, null),
            Nom = NomEntite(Texte(c, "name"), appareil?.Nom),
            EtatTopic = Topic("state_topic"),
            CommandeTopic = Topic("command_topic"),
            Gabarit = Gabarit.Lire(Texte(c, "value_template") ?? Texte(c, "state_value_template")),
            ClasseHa = Texte(c, "device_class"),
            Unite = Texte(c, "unit_of_measurement"),
            ClasseEtat = Texte(c, "state_class"),
            Categorie = Texte(c, "entity_category"),
            ChargeOn = chargeOn,
            ChargeOff = chargeOff,
            EtatOn = Texte(c, "state_on") ?? chargeOn,
            EtatOff = Texte(c, "state_off") ?? chargeOff,
            SchemaJson = Texte(c, "schema") == "json",
            Luminosite = c["brightness"] is JsonValue b && b.TryGetValue<bool>(out var avec) && avec,
            EchelleLuminosite = c["brightness_scale"] is JsonValue e && e.TryGetValue<double>(out var echelle) && echelle > 0 ? echelle : 255,
            Dispo = SourcesDispo(c, baseTopic),
            ModeDispo = Texte(c, "availability_mode") ?? "latest",
        };
    }

    /// <summary>Le composant (<c>sensor</c>…) si <paramref name="topic"/> est un topic de configuration, null sinon.</summary>
    public static string? Composant(string prefixe, string topic)
    {
        if (!topic.StartsWith(prefixe + "/", StringComparison.Ordinal) || !topic.EndsWith("/config", StringComparison.Ordinal)) return null;

        var segments = topic[(prefixe.Length + 1)..].Split('/');
        // composant/objet/config ou composant/noeud/objet/config
        return segments.Length is 3 or 4 ? segments[0] : null;
    }

    private static IReadOnlyList<SourceDispo> SourcesDispo(JsonObject c, string? baseTopic)
    {
        var enLigne = Texte(c, "payload_available") ?? "online";
        var horsLigne = Texte(c, "payload_not_available") ?? "offline";
        var sources = new List<SourceDispo>();

        if (Tilde(Texte(c, "availability_topic"), baseTopic) is { } topic)
            sources.Add(new SourceDispo(topic, enLigne, horsLigne, Gabarit.Lire(Texte(c, "availability_template"))));

        if (c["availability"] is JsonArray liste)
            foreach (var element in liste.OfType<JsonObject>())
            {
                var s = Developper(element, Abreviations);
                if (Tilde(Texte(s, "topic"), baseTopic) is not { } t) continue;
                sources.Add(new SourceDispo(t, Texte(s, "payload_available") ?? "online", Texte(s, "payload_not_available") ?? "offline",
                    Gabarit.Lire(Texte(s, "value_template"))));
            }

        return sources;
    }

    private static AppareilHa? Appareil(JsonObject? brut)
    {
        if (brut is null) return null;
        var d = Developper(brut, AbreviationsAppareil);

        // identifiers : une chaîne (ESPHome) ou une liste (Zigbee2MQTT) ; à défaut, la première connexion (adresse MAC).
        var id = d["identifiers"] switch
        {
            JsonArray a => a.Select(Valeur).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
            JsonValue v => Valeur(v),
            _ => null,
        };
        if (string.IsNullOrEmpty(id) && d["connections"] is JsonArray cns && cns.FirstOrDefault() is JsonArray paire && paire.Count == 2)
            id = Valeur(paire[1]);
        if (string.IsNullOrEmpty(id)) return null;

        return new AppareilHa(id, Texte(d, "name"), Texte(d, "manufacturer"), Texte(d, "model"));
    }

    /// <summary>
    /// Le nom de l'entité SANS celui de l'appareil, que le cœur ajoute lui-même. Les anciens ESPHome publiaient
    /// « esp-salon Temperature » : on retire le préfixe. Un nom absent (null) est l'entité principale de l'appareil.
    /// </summary>
    private static string? NomEntite(string? nom, string? appareil)
    {
        if (string.IsNullOrWhiteSpace(nom)) return null;
        if (appareil is { Length: > 0 } && nom.StartsWith(appareil + " ", StringComparison.OrdinalIgnoreCase)) nom = nom[(appareil.Length + 1)..];
        return nom.Trim();
    }

    private static JsonObject Developper(JsonObject brut, Dictionary<string, string> abreviations)
    {
        var o = new JsonObject();
        foreach (var (cle, valeur) in brut)
        {
            var nom = abreviations.GetValueOrDefault(cle, cle);
            // La forme longue gagne si les deux sont présentes : c'est elle que Home Assistant documente.
            if (nom != cle && brut.ContainsKey(nom)) continue;
            o[nom] = valeur?.DeepClone();
        }

        return o;
    }

    /// <summary><c>~/state</c> → base + <c>/state</c> ; <c>state/~</c> → <c>state/</c> + base (règle de Home Assistant).</summary>
    private static string? Tilde(string? topic, string? baseTopic)
    {
        if (topic is null || baseTopic is null) return topic;
        if (topic.StartsWith('~')) return baseTopic + topic[1..];
        if (topic.EndsWith('~')) return topic[..^1] + baseTopic;
        return topic;
    }

    private static string? Texte(JsonObject o, string cle) => o[cle] is { } n ? Valeur(n) : null;

    /// <summary>
    /// Une valeur de configuration en texte, comme Home Assistant la comparera : un <c>payload_on: true</c> (JSON) vaut
    /// « True », la forme que produit un gabarit sur un booléen.
    /// </summary>
    internal static string? Valeur(JsonNode? n) => n switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };
}
