namespace Krissvia.Plugin.MqttDiscovery.Discovery;

/// <summary>
/// Une ligne des « Entités manuelles » (section <c>Entites</c> des réglages) : une valeur lue sur un topic quelconque,
/// pour ce qui ne s'annonce pas en MQTT Discovery (Rfx2Mqtt, un script, un vieux module).
/// </summary>
internal sealed class EntiteManuelle
{
    public string? Appareil { get; set; }
    public string? Nom { get; set; }

    /// <summary><c>sensor</c> (défaut), <c>binary_sensor</c>, <c>switch</c> ou <c>light</c>.</summary>
    public string? Type { get; set; }

    public string? Topic { get; set; }

    /// <summary>Chemin dans la charge JSON (<c>temperature</c>, <c>a.b</c>, <c>liste.0</c>) ; vide : la charge entière.</summary>
    public string? Champ { get; set; }

    public string? Classe { get; set; }
    public string? Unite { get; set; }
    public string? ValeurOn { get; set; }
    public string? ValeurOff { get; set; }
    public string? TopicCommande { get; set; }
    public string? TopicDispo { get; set; }
}

/// <summary>
/// Une entité manuelle devient la configuration qu'aurait publiée l'appareil s'il s'annonçait : tout le reste (lecture,
/// conversions d'unités, ordres, joignabilité, déclaration complète) est celui des entités découvertes.
/// </summary>
internal static class EntitesManuelles
{
    /// <summary>
    /// Début des identifiants. Identité = topic + champ : renommer l'entité ou son appareil ne casse rien ; changer de
    /// topic, c'est une autre source, donc une autre entité (l'ancienne devient disparue).
    /// </summary>
    public const string Prefixe = "manuel:";

    private static readonly HashSet<string> Types = ["sensor", "binary_sensor", "switch", "light"];

    public static ConfigHa? VersConfig(EntiteManuelle e, out string? refus)
    {
        refus = null;
        var topic = Propre(e.Topic);
        var appareil = Propre(e.Appareil);
        var type = Propre(e.Type) ?? "sensor";

        if (topic is null) return Refus(out refus, "pas de topic d'état");
        // Le traducteur range les entités par topic exact : un joker ne recevrait jamais rien.
        if (topic.Contains('+') || topic.Contains('#')) return Refus(out refus, "un topic d'état ne peut contenir ni + ni #");
        if (appareil is null) return Refus(out refus, "pas d'appareil");
        if (!Types.Contains(type)) return Refus(out refus, $"type « {type} » inconnu");

        var champ = Propre(e.Champ);
        Gabarit? gabarit = null;
        if (champ is not null)
        {
            var segments = champ.Split('.', StringSplitOptions.TrimEntries);
            if (segments.Any(s => s.Length == 0 || s.Contains('\''))) return Refus(out refus, $"champ JSON « {champ} » illisible");
            // Entre crochets plutôt qu'avec des points : une clé « rain-rate » ou « 1h » reste lisible par le gabarit.
            gabarit = Gabarit.Lire("{{ value_json" + string.Concat(segments.Select(s => s.All(char.IsAsciiDigit) ? $"[{s}]" : $"['{s}']")) + " }}");
        }

        var unite = Propre(e.Unite);
        var classe = Propre(e.Classe);
        var on = Propre(e.ValeurOn);
        var off = Propre(e.ValeurOff);

        return new ConfigHa
        {
            Composant = type,
            UniqueId = champ is null ? Prefixe + topic : $"{Prefixe}{topic}#{champ}",
            Appareil = new AppareilHa(Prefixe + appareil, appareil, null, "Entité MQTT manuelle"),
            Nom = Propre(e.Nom),
            EtatTopic = topic,
            CommandeTopic = Propre(e.TopicCommande),
            Gabarit = gabarit,
            ClasseHa = classe,
            Unite = unite,
            // Un capteur avec unité ou classe est un nombre ; sans l'une ni l'autre, un libellé (règle des entités découvertes).
            ClasseEtat = type != "sensor" || (unite is null && classe is null) ? null
                : classe == "energy" ? "total_increasing" : "measurement",
            ChargeOn = on ?? "ON",
            ChargeOff = off ?? "OFF",
            EtatOn = on ?? "ON",
            EtatOff = off ?? "OFF",
            BooleensSouples = on is null && off is null,
            Dispo = Propre(e.TopicDispo) is { } dispo ? [new SourceDispo(dispo, "online", "offline", null)] : [],
        };
    }

    private static string? Propre(string? texte) => string.IsNullOrWhiteSpace(texte) ? null : texte.Trim();

    private static ConfigHa? Refus(out string? refus, string motif)
    {
        refus = motif;
        return null;
    }
}
