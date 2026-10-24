namespace Krissvia.Plugin.Ipp;

/// <summary>Ce que dit un relevé : un problème à signaler ou non, et les alertes à montrer sur la carte.</summary>
public sealed record Diagnostic(bool Probleme, IReadOnlyList<string> Alertes)
{
    /// <summary>
    /// Les pannes franches, même sans suffixe : une imprimante qui ne peut plus imprimer sans qu'on intervienne.
    /// Une liste fermée plutôt que la règle de la RFC 8011 §5.4.12 (« sans suffixe = erreur ») : appliquée telle quelle,
    /// elle ferait d'un <c>paused</c>, d'un <c>sleep</c> ou d'un mot-clé maison une alerte. Une panne absente d'ici
    /// se voit quand même : l'imprimante passe « arrêtée », ou l'envoie avec <c>-error</c>.
    /// </summary>
    private static readonly HashSet<string> Pannes = new(StringComparer.Ordinal)
    {
        "media-jam", "media-empty", "media-needed", "toner-empty", "marker-supply-empty", "developer-empty", "marker-waste-full",
        "door-open", "cover-open", "interlock-open", "input-tray-missing", "output-tray-missing", "output-area-full",
        "fuser-over-temp", "fuser-under-temp", "opc-life-over",
    };

    private static readonly Dictionary<string, string> Libelles = new(StringComparer.Ordinal)
    {
        ["media-jam"] = "Bourrage papier",
        ["media-empty"] = "Plus de papier",
        ["media-needed"] = "Papier à charger",
        ["media-low"] = "Papier bientôt épuisé",
        ["toner-empty"] = "Toner vide",
        ["toner-low"] = "Toner bas",
        ["marker-supply-empty"] = "Consommable épuisé",
        ["marker-supply-low"] = "Consommable bas",
        ["developer-empty"] = "Développeur épuisé",
        ["developer-low"] = "Développeur bas",
        ["marker-waste-full"] = "Récupérateur de toner plein",
        ["marker-waste-almost-full"] = "Récupérateur de toner presque plein",
        ["door-open"] = "Porte ouverte",
        ["cover-open"] = "Capot ouvert",
        ["interlock-open"] = "Capot ouvert",
        ["input-tray-missing"] = "Bac d'alimentation absent",
        ["output-tray-missing"] = "Bac de sortie absent",
        ["output-area-full"] = "Bac de sortie plein",
        ["output-area-almost-full"] = "Bac de sortie presque plein",
        ["opc-life-over"] = "Tambour en fin de vie",
        ["opc-near-eol"] = "Tambour bientôt en fin de vie",
        ["fuser-over-temp"] = "Four trop chaud",
        ["fuser-under-temp"] = "Four trop froid",
        ["paused"] = "En pause",
        ["moving-to-paused"] = "Mise en pause",
        ["offline"] = "Hors ligne",
        ["shutdown"] = "Arrêt en cours",
        ["spool-area-full"] = "File d'attente pleine",
        ["connecting-to-device"] = "Connexion à l'imprimante",
        ["timed-out"] = "Ne répond plus",
    };

    /// <summary>
    /// Un problème quand l'imprimante est <b>arrêtée</b> (« intervention nécessaire », RFC 8011), qu'elle envoie un motif
    /// en <c>-error</c> ou une panne de la liste, ou qu'un consommable est <b>bas</b> — d'après elle (<c>toner-low</c>,
    /// tout motif en <c>-low</c>, <c>-almost-empty</c>, <c>-near-eol</c>, <c>-almost-full</c>, hors <c>-report</c>) ou
    /// d'après son niveau sous le seuil. Une imprimante éteinte n'est PAS un problème : elle devient indisponible.
    /// </summary>
    public static Diagnostic Evaluer(ReleveImprimante r)
    {
        var probleme = false;
        var alertes = new List<string>();
        if (r.Etat == EtatImprimante.Arretee)
        {
            probleme = true;
            alertes.Add("Imprimante arrêtée");
        }

        foreach (var motif in r.Motifs)
        {
            var (base_, suffixe) = Decouper(motif);
            if (suffixe == "report") continue;   // simple information, jamais montrée
            if (suffixe == "error" || (suffixe is null && Pannes.Contains(base_)) || Bas(base_)) probleme = true;
            alertes.Add(Libelles.GetValueOrDefault(base_) ?? base_);
        }

        foreach (var c in r.Consommables.Where(c => c.Bas))
        {
            probleme = true;
            alertes.Add($"{c.Nom} bas ({c.Niveau} %)");
        }

        return new Diagnostic(probleme, [.. alertes.Distinct(StringComparer.Ordinal)]);
    }

    private static bool Bas(string motif) =>
        motif.EndsWith("-low", StringComparison.Ordinal) || motif.EndsWith("-almost-empty", StringComparison.Ordinal)
        || motif.EndsWith("-near-eol", StringComparison.Ordinal) || motif.EndsWith("-almost-full", StringComparison.Ordinal);

    /// <summary>« toner-low-warning » → (« toner-low », « warning ») ; « media-jam » → (« media-jam », null).</summary>
    public static (string Base, string? Suffixe) Decouper(string motif)
    {
        foreach (var s in (ReadOnlySpan<string>)["report", "warning", "error"])
        {
            if (motif.EndsWith($"-{s}", StringComparison.Ordinal)) return (motif[..^(s.Length + 1)], s);
        }

        return (motif, null);
    }

    /// <summary>Le libellé de l'état, celui de l'entité « État » et de la carte.</summary>
    public static string LibelleEtat(EtatImprimante e) => e switch
    {
        EtatImprimante.Prete => "Prête",
        EtatImprimante.EnImpression => "En impression",
        EtatImprimante.Arretee => "Arrêtée",
        _ => "Inconnu",
    };
}
