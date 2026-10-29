using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;

namespace Krissvia.Plugin.MqttDiscovery.Discovery;

/// <summary>Ce qu'un ordre du cœur devient sur le broker : la charge à publier, ou pourquoi il est refusé.</summary>
internal sealed record OrdreMqtt(string? Charge, string? Erreur)
{
    public static OrdreMqtt Ok(string charge) => new(charge, null);
    public static OrdreMqtt Refus(string erreur) => new(null, erreur);
}

/// <summary>
/// Une entité découverte : sa description pour le cœur, la lecture de ses états et la traduction des ordres. Une par
/// configuration acceptée ; <see cref="Creer"/> dit pourquoi une configuration ne donne pas d'entité.
/// </summary>
internal sealed class EntiteHa
{
    private readonly Func<double, double> _conversion;

    private EntiteHa(ConfigHa config, EntityDescriptor descripteur, bool numerique, Func<double, double> conversion)
    {
        Config = config;
        Descripteur = descripteur;
        Numerique = numerique;
        _conversion = conversion;
    }

    public ConfigHa Config { get; }
    public EntityDescriptor Descripteur { get; }
    public string UniqueId => Config.UniqueId;

    /// <summary>Faux pour un sensor sans unité ni classe de mesure : sa valeur est un libellé (version, adresse IP).</summary>
    public bool Numerique { get; }

    /// <summary>Classes Home Assistant → classes du contrat, quand les noms diffèrent. Les autres passent telles quelles.</summary>
    private static readonly Dictionary<string, string> ClassesCapteur = new(StringComparer.Ordinal)
    {
        ["atmospheric_pressure"] = DeviceClasses.Pressure,
    };

    private static readonly Dictionary<string, string> ClassesBinaires = new(StringComparer.Ordinal)
    {
        // Home Assistant dit, comme le contrat, true = OUVERT : aucune inversion ici (contrairement au contact Zigbee brut).
        ["door"] = DeviceClasses.Opening,
        ["window"] = DeviceClasses.Opening,
        ["garage_door"] = DeviceClasses.Opening,
        ["motion"] = DeviceClasses.Occupancy,
    };

    /// <summary>Classes Home Assistant dont la valeur n'est pas un nombre.</summary>
    private static readonly HashSet<string> ClassesNonNumeriques = ["enum", "timestamp", "date"];

    /// <summary>Unités converties vers celles du contrat. Une unité absente d'ici passe telle quelle (dBm, s…).</summary>
    private static readonly Dictionary<string, (string Unite, Func<double, double> Conversion)> Conversions = new(StringComparer.Ordinal)
    {
        ["°F"] = (Units.Celsius, f => (f - 32) * 5 / 9),
        ["Wh"] = (Units.KilowattHour, v => v / 1000),
        ["kW"] = (Units.Watt, v => v * 1000),
        ["kVA"] = (Units.VoltAmpere, v => v * 1000),
        ["mV"] = (Units.Volt, v => v / 1000),
        ["mA"] = (Units.Ampere, v => v / 1000),
        ["Pa"] = (Units.Hectopascal, v => v / 100),
        ["kPa"] = (Units.Hectopascal, v => v * 10),
        ["mbar"] = (Units.Hectopascal, v => v),
    };

    public static EntiteHa? Creer(ConfigHa c, out string? refus)
    {
        refus = null;
        if (c.Categorie == "config")
        {
            refus = "réglage de l'appareil (entity_category: config)";
            return null;
        }

        if (c.Gabarit is { Supporte: false } g)
        {
            refus = $"gabarit non pris en charge : {g.Source}";
            return null;
        }

        var commandes = c.CommandeTopic is null ? [] : new List<string>();
        switch (c.Composant)
        {
            case "sensor":
                if (c.EtatTopic is null) return Sans(out refus, "pas de state_topic");
                var numerique = c.Unite is not null || c.ClasseEtat is not null ||
                                (c.ClasseHa is { } cl && !ClassesNonNumeriques.Contains(cl));
                var (unite, conversion) = c.Unite is { } u && Conversions.TryGetValue(u, out var conv) ? conv : (c.Unite, v => v);
                var classe = !numerique ? DeviceClasses.Enum
                    : c.ClasseHa is { } h ? ClassesCapteur.GetValueOrDefault(h, h) : null;
                // La sonde interne d'un ESP32 est une température de DIAGNOSTIC : celle de la puce, pas de la pièce. Classée
                // « temperature », elle irait dans les sondes, les moyennes par pièce et les choix d'un thermostat.
                if (classe == DeviceClasses.Temperature && c.Categorie == "diagnostic") classe = DeviceClasses.DeviceTemperature;
                return new EntiteHa(c, new EntityDescriptor
                {
                    UniqueId = c.UniqueId,
                    Kind = EntityKind.Sensor,
                    Name = c.Nom,
                    DeviceClass = classe,
                    Unit = numerique ? unite : null,
                    StateClass = c.ClasseEtat switch
                    {
                        "measurement" => StateClass.Measurement,
                        "total_increasing" => StateClass.TotalIncreasing,
                        _ => null,
                    },
                }, numerique, conversion);

            case "binary_sensor":
                if (c.EtatTopic is null) return Sans(out refus, "pas de state_topic");
                return new EntiteHa(c, new EntityDescriptor
                {
                    UniqueId = c.UniqueId,
                    Kind = EntityKind.BinarySensor,
                    Name = c.Nom,
                    DeviceClass = c.ClasseHa is { } b ? ClassesBinaires.GetValueOrDefault(b, b) : null,
                }, false, v => v);

            case "switch":
                if (c.CommandeTopic is not null) commandes.AddRange([StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle]);
                return new EntiteHa(c, new EntityDescriptor
                {
                    UniqueId = c.UniqueId,
                    Kind = EntityKind.Switch,
                    Name = c.Nom,
                    DeviceClass = c.ClasseHa,
                    Commands = commandes,
                }, false, v => v);

            case "light":
                if (c.CommandeTopic is not null)
                {
                    commandes.AddRange([StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle]);
                    if (c.SchemaJson && c.Luminosite) commandes.Add(StandardCommands.SetBrightness);
                }

                return new EntiteHa(c, new EntityDescriptor
                {
                    UniqueId = c.UniqueId,
                    Kind = EntityKind.Light,
                    Name = c.Nom,
                    Commands = commandes,
                }, false, v => v);

            default:
                return Sans(out refus, $"composant « {c.Composant} » non pris en charge");
        }
    }

    private static EntiteHa? Sans(out string? refus, string motif)
    {
        refus = motif;
        return null;
    }

    // ── États ────────────────────────────────────────────────────────────────

    /// <summary>
    /// La valeur pour le cœur, ou null : charge illisible, valeur inconnue (« unknown », « nan » d'une sonde en panne).
    /// <paramref name="precedente"/> sert aux lumières, dont un message peut ne porter que l'état ou que la luminosité.
    /// </summary>
    public JsonElement? Lire(string charge, JsonElement? precedente)
    {
        if (Config.Composant == "light" && Config.SchemaJson) return LireLumiereJson(charge, precedente);

        var texte = Config.Gabarit is { } g ? g.Evaluer(charge) : charge;
        if (texte is null) return null;

        switch (Config.Composant)
        {
            case "sensor" when Numerique:
                if (!double.TryParse(texte.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return null;
                return JsonSerializer.SerializeToElement(Math.Round(_conversion(v), 6));
            case "sensor":
                return texte is "" or "unknown" or "unavailable" or "None" ? null : JsonSerializer.SerializeToElement(texte);
            case "binary_sensor":
                return Etat(texte, Config.ChargeOn, Config.ChargeOff) is { } b ? Booleen(b) : null;
            case "switch":
                return Etat(texte, Config.EtatOn, Config.EtatOff) is { } s ? Booleen(s) : null;
            case "light":
                if (Etat(texte, Config.EtatOn, Config.EtatOff) is not { } allume) return null;
                return Fusion(precedente, o => o["on"] = allume);
            default:
                return null;
        }
    }

    /// <summary>Schéma json (ESPHome, Zigbee2MQTT) : <c>{"state":"ON","brightness":128}</c>, l'un ou l'autre pouvant manquer.</summary>
    private JsonElement? LireLumiereJson(string charge, JsonElement? precedente)
    {
        try
        {
            using var document = JsonDocument.Parse(charge);
            var r = document.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            bool? allume = r.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() switch { "ON" => true, "OFF" => false, _ => null }
                : null;
            int? luminosite = Config.Luminosite && r.TryGetProperty("brightness", out var b) && b.TryGetDouble(out var brute)
                ? (int)Math.Round(Math.Clamp(brute / Config.EchelleLuminosite, 0, 1) * 100)
                : null;
            if (allume is null && luminosite is null) return null;

            return Fusion(precedente, o =>
            {
                if (allume is { } a) o["on"] = a;
                if (luminosite is { } l) o["brightness"] = l;
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Fusion(JsonElement? precedente, Action<JsonObject> modifier)
    {
        var o = precedente is { ValueKind: JsonValueKind.Object } p ? (JsonObject)JsonNode.Parse(p.GetRawText())! : new JsonObject();
        modifier(o);
        return JsonSerializer.SerializeToElement(o);
    }

    private static JsonElement Booleen(bool v) => JsonSerializer.SerializeToElement(v);

    private bool? Etat(string texte, string on, string off)
    {
        if (texte == on) return true;
        if (texte == off) return false;
        if (!Config.BooleensSouples) return null;
        // Un gabarit rend un booléen JSON en « True » / « False » : la casse ne compte pas.
        return texte.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => null,
        };
    }

    // ── Ordres ───────────────────────────────────────────────────────────────

    /// <summary>
    /// La charge à publier sur <c>command_topic</c>. <paramref name="connue"/> est la dernière valeur reçue : une bascule
    /// se calcule dessus (un appareil Home Assistant ne connaît pas de « TOGGLE »), et se refuse si on n'en sait rien.
    /// </summary>
    public OrdreMqtt Commander(string commande, JsonElement? args, JsonElement? connue)
    {
        if (Config.CommandeTopic is null) return OrdreMqtt.Refus("entité en lecture seule (pas de command_topic)");
        if (Config.Composant is not ("switch" or "light")) return OrdreMqtt.Refus($"commande « {commande} » non prise en charge");

        if (commande == StandardCommands.Toggle)
        {
            if (Allume(connue) is not { } allume) return OrdreMqtt.Refus("état inconnu : impossible de basculer");
            commande = allume ? StandardCommands.TurnOff : StandardCommands.TurnOn;
        }

        var json = Config.Composant == "light" && Config.SchemaJson;
        switch (commande)
        {
            case StandardCommands.TurnOn:
                return OrdreMqtt.Ok(json ? """{"state":"ON"}""" : Config.ChargeOn);
            case StandardCommands.TurnOff:
                return OrdreMqtt.Ok(json ? """{"state":"OFF"}""" : Config.ChargeOff);
            case StandardCommands.SetBrightness when json && Config.Luminosite:
                if (args is not { ValueKind: JsonValueKind.Object } a || !a.TryGetProperty("brightness", out var b) ||
                    !b.TryGetDouble(out var pourcent) || pourcent is < 0 or > 100)
                    return OrdreMqtt.Refus("set_brightness attend {\"brightness\": 0..100}");
                // 0 % éteint : une luminosité nulle laisse certaines lampes allumées au minimum.
                if (pourcent == 0) return OrdreMqtt.Ok("""{"state":"OFF"}""");
                var brute = (int)Math.Max(1, Math.Round(pourcent * Config.EchelleLuminosite / 100));
                return OrdreMqtt.Ok(new JsonObject { ["state"] = "ON", ["brightness"] = brute }.ToJsonString());
            default:
                return OrdreMqtt.Refus($"commande « {commande} » non prise en charge");
        }
    }

    private static bool? Allume(JsonElement? v) => v switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
            => on.GetBoolean(),
        _ => null,
    };
}
