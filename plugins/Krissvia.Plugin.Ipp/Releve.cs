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

        var noms = Liste("marker-names");
        var niveaux = Liste("marker-levels");
        var seuils = Liste("marker-low-levels");
        var types = Liste("marker-types");
        var couleurs = Liste("marker-colors");

        var consommables = new List<Consommable>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Max(niveaux.Count, noms.Count); i++)
        {
            var nom = NomPropre(noms.ElementAtOrDefault(i) as string) ?? $"Consommable {i + 1}";
            var brut = niveaux.ElementAtOrDefault(i) as int?;
            var seuil = seuils.ElementAtOrDefault(i) is int s and > 0 and < 100 ? s : seuilBas;
            consommables.Add(new Consommable(Unique(ids, Identifiant(nom)), nom, brut is >= 0 ? Math.Min(brut.Value, 100) : null, brut == -3,
                seuil, types.ElementAtOrDefault(i) as string, Couleur(couleurs.ElementAtOrDefault(i) as string)));
        }

        var motifs = Liste("printer-state-reasons").OfType<string>().Where(m => m is { Length: > 0 } && m != "none").ToList();
        return new ReleveImprimante(UuidSeul(Texte("printer-uuid")), Texte("printer-info") ?? Texte("printer-name"),
            Texte("printer-make-and-model"), etat, motifs, Texte("printer-state-message"), consommables);
    }

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
