using System.Text;
using System.Text.RegularExpressions;
using Krissvia.Plugin.Ipp.Protocole;

namespace Krissvia.Plugin.Ipp;

/// <summary><c>printer-state</c> (RFC 8011 §5.4.11) : 3 = idle, 4 = processing, 5 = stopped.</summary>
public enum EtatImprimante { Inconnu, Prete, EnImpression, Arretee }

/// <summary>
/// Un consommable : toner, tambour, rouleau de transfert, four… <see cref="Niveau"/> en %, ou null quand l'imprimante
/// ne le connaît pas (-1, -2) ou dit seulement qu'il en reste (-3, <see cref="Restant"/>).
/// </summary>
public sealed record Consommable(string Id, string Nom, int? Niveau, bool Restant, int SeuilBas, string? Type, string? Couleur)
{
    public bool Bas => Niveau is { } n && n <= SeuilBas;
}

/// <summary>Un relevé : ce que l'imprimante a répondu à <c>Get-Printer-Attributes</c>, mis en forme.</summary>
public sealed partial record ReleveImprimante(
    string? Uuid, string? Nom, string? Modele, EtatImprimante Etat, IReadOnlyList<string> Motifs, string? Message,
    IReadOnlyList<Consommable> Consommables)
{
    /// <summary>
    /// Lit la réponse. <paramref name="seuilBas"/> sert aux consommables dont l'imprimante ne donne pas le seuil
    /// (<c>marker-low-levels</c>). Les listes <c>marker-*</c> sont parallèles mais pas toujours de même longueur : la
    /// référence est celle des niveaux, les noms manquants deviennent « Consommable 2 ».
    /// </summary>
    public static ReleveImprimante Lire(IppResponse r, int seuilBas)
    {
        string? Texte(string nom) => r.Printer(nom)?.Values.FirstOrDefault() as string is { Length: > 0 } t ? t : null;
        IReadOnlyList<object?> Liste(string nom) => r.Printer(nom)?.Values ?? [];

        var etat = r.Printer("printer-state")?.Values.FirstOrDefault() switch
        {
            3 => EtatImprimante.Prete,
            4 => EtatImprimante.EnImpression,
            5 => EtatImprimante.Arretee,
            _ => EtatImprimante.Inconnu,
        };

        var lignes = LignesMarker(Liste);
        if (lignes.Count == 0) lignes = LignesSupply(Liste);   // certaines Epson n'annoncent que printer-supply

        var consommables = new List<Consommable>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lignes.Count; i++)
        {
            var l = lignes[i];
            var nom = NomPropre(l.Nom) ?? $"Consommable {i + 1}";
            var seuil = Pourcentage(l.Bas, l.Haut) is int s and > 0 and < 100 ? s : seuilBas;
            consommables.Add(new Consommable(Unique(ids, Identifiant(nom)), nom, Pourcentage(l.Niveau, l.Haut), l.Niveau == -3, seuil, l.Type, Couleur(l.Couleur)));
        }

        var motifs = Liste("printer-state-reasons").OfType<string>().Where(m => m is { Length: > 0 } && m != "none").ToList();
        // Samsung met dans printer-info le modèle suivi du nom réseau : « Samsung M2020 Series (SEC30CDA7201287) ».
        // Le modèle seul se lit mieux ; un printer-info choisi par l'utilisateur (« Bureau ») est gardé tel quel.
        var modele = Texte("printer-make-and-model");
        var info = Texte("printer-info");
        var nomAffiche = modele is not null && info?.StartsWith(modele, StringComparison.OrdinalIgnoreCase) == true ? modele : info ?? Texte("printer-name");
        return new ReleveImprimante(UuidSeul(Texte("printer-uuid")), nomAffiche, modele, etat, motifs, Texte("printer-state-message"), consommables);
    }

    /// <summary>Un consommable tel qu'annoncé, avant conversion : niveaux bruts, à l'échelle de <see cref="Haut"/>.</summary>
    private sealed record Ligne(string? Nom, int? Niveau, int? Haut, int? Bas, string? Type, string? Couleur);

    /// <summary>
    /// Les attributs <c>marker-*</c>, parallèles mais pas toujours de même longueur : la référence est la plus longue
    /// des listes de noms et de niveaux.
    /// </summary>
    private static List<Ligne> LignesMarker(Func<string, IReadOnlyList<object?>> liste)
    {
        var (noms, niveaux, hauts, bas) = (liste("marker-names"), liste("marker-levels"), liste("marker-high-levels"), liste("marker-low-levels"));
        var (types, couleurs) = (liste("marker-types"), liste("marker-colors"));
        return [.. Enumerable.Range(0, Math.Max(niveaux.Count, noms.Count)).Select(i => new Ligne(
            noms.ElementAtOrDefault(i) as string, niveaux.ElementAtOrDefault(i) as int?, hauts.ElementAtOrDefault(i) as int?,
            bas.ElementAtOrDefault(i) as int?, types.ElementAtOrDefault(i) as string, couleurs.ElementAtOrDefault(i) as string))];
    }

    /// <summary>
    /// <c>printer-supply</c> (PWG 5100.13), l'autre façon standard d'annoncer les consommables : une chaîne
    /// « index=1;type=ink;maxcapacity=100;level=57;colorantname=cyan; » par consommable, et son nom dans
    /// <c>printer-supply-description</c>. Repris de l'intégration Gladys (guim31/gladys-ipp), pour les imprimantes
    /// qui n'envoient jamais les <c>marker-*</c>.
    /// </summary>
    private static List<Ligne> LignesSupply(Func<string, IReadOnlyList<object?>> liste)
    {
        var supplies = liste("printer-supply");
        var descriptions = liste("printer-supply-description");
        var lignes = new List<Ligne>();
        for (var i = 0; i < Math.Max(supplies.Count, descriptions.Count); i++)
        {
            var champs = Champs(supplies.ElementAtOrDefault(i));
            int? Entier(string cle) => int.TryParse(champs.GetValueOrDefault(cle), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
            var nom = descriptions.ElementAtOrDefault(i) as string is { Length: > 0 } d ? d : champs.GetValueOrDefault("colorantname");
            lignes.Add(new Ligne(nom, Entier("level"), Entier("maxcapacity"), null, champs.GetValueOrDefault("type"), null));
        }

        return lignes;
    }

    /// <summary>« clé=valeur; » → dictionnaire (clés en minuscules). La valeur est un octetString ou un texte selon l'imprimante.</summary>
    private static Dictionary<string, string> Champs(object? valeur)
    {
        var texte = valeur switch { byte[] o => Encoding.UTF8.GetString(o), string t => t, _ => "" };
        var champs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in texte.Split(';'))
        {
            var egal = part.IndexOf('=');
            if (egal > 0) champs.TryAdd(part[..egal].Trim().ToLowerInvariant(), part[(egal + 1)..].Trim());
        }

        return champs;
    }

    /// <summary>
    /// Un niveau brut en %, à l'échelle de son maximum (<c>marker-high-levels</c>, souvent 100, pas toujours). Négatif
    /// (-1 non fourni, -2 inconnu, -3 « il en reste ») : null, jamais un faux 0 %.
    /// </summary>
    public static int? Pourcentage(int? niveau, int? haut) =>
        niveau is not >= 0 ? null : (int)Math.Clamp(Math.Round(niveau.Value * 100.0 / (haut is > 0 ? haut.Value : 100)), 0, 100);

    /// <summary>
    /// Le nom d'un consommable sans son numéro de série. Samsung écrit « Black Toner_S/N_:CRUM-18020727263 » : le
    /// garder changerait le nom — et l'identifiant de l'entité — à chaque cartouche neuve.
    /// </summary>
    public static string? NomPropre(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom)) return null;
        var coupe = SerieRegex().Match(nom) is { Success: true } m ? nom[..m.Index] : nom;
        coupe = coupe.Trim().TrimEnd('_', ':', '-', ',', ';').Trim();
        return coupe.Length > 0 ? coupe : null;
    }

    /// <summary>« Black Toner » → « black_toner », « Fuser Life » → « fuser_life » : stable, sans accent ni espace.</summary>
    public static string Identifiant(string nom)
    {
        var s = new StringBuilder();
        foreach (var c in nom.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c)) s.Append(char.ToLowerInvariant(c));
            else if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && s.Length > 0 && s[^1] != '_') s.Append('_');
        }

        var id = s.ToString().Trim('_');
        return id.Length > 0 ? id : "consommable";
    }

    private static string Unique(HashSet<string> pris, string id)
    {
        var candidat = id;
        for (var n = 2; !pris.Add(candidat); n++) candidat = $"{id}_{n}";
        return candidat;
    }

    /// <summary><c>urn:uuid:…</c> → l'UUID seul, en minuscules : la clé de l'appareil, qui survit à un changement d'adresse.</summary>
    private static string? UuidSeul(string? urn) =>
        urn is null ? null : (urn.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) ? urn[9..] : urn).Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;

    /// <summary><c>#000000</c> ; <c>none</c> ou une couleur double (<c>#00FFFF#FF00FF</c>) : la première, ou rien.</summary>
    private static string? Couleur(string? c) =>
        c is { Length: >= 7 } && c[0] == '#' && c[1..7].All(Uri.IsHexDigit) ? c[..7].ToLowerInvariant() : null;

    [GeneratedRegex(@"[_\s]*S/N", RegexOptions.IgnoreCase)]
    private static partial Regex SerieRegex();
}
