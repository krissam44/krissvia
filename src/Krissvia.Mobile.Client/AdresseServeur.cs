using System.Net;

namespace Krissvia.Mobile.Client;

/// <summary>L'adresse du serveur telle que l'utilisateur la tape, ramenée à une forme sur laquelle on concatène les routes.</summary>
public static class AdresseServeur
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Sans schéma : <c>http://</c> pour une adresse du réseau local (IP, <c>localhost</c>, nom sans point ou en
    /// <c>.local</c>), où il n'y a pas de certificat ; <c>https://</c> sinon.</item>
    /// <item>Retire la barre finale, et un <c>/api</c> final : le client ajoute déjà <c>/api/…</c>, et pointer sur
    /// <c>/api</c> est le réflexe naturel — l'appli de DomoMud en a fait un 404 très déroutant (<c>/api/api/…</c>).</item>
    /// </list>
    /// À appliquer à l'enregistrement ET à la lecture : une adresse enregistrée avant une règle resterait fautive.
    /// </summary>
    public static string Normaliser(string? saisie)
    {
        var url = (saisie ?? string.Empty).Trim();
        if (url.Length == 0) return url;

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = (EstLocale(url) ? "http://" : "https://") + url;

        url = url.TrimEnd('/');
        if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            url = url[..^4].TrimEnd('/');

        return url;
    }

    /// <summary>L'adresse du flux en direct : <c>ws</c> ou <c>wss</c> selon le serveur, restreint aux entités nommées.</summary>
    public static Uri Flux(string baseUrl, IEnumerable<string> entites)
    {
        var url = Normaliser(baseUrl);
        var ws = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + url[8..] : "ws://" + url[7..];
        // Les identifiants sont des minuscules, chiffres, « . » et « _ » : seule la virgule sépare, mais on échappe quand même.
        var liste = string.Join(',', entites.Select(Uri.EscapeDataString));
        return new Uri($"{ws}/api/live?entities={liste}");
    }

    private static bool EstLocale(string url)
    {
        var hote = url.Split('/', 2)[0];
        // Le port, sauf dans une IPv6 entre crochets.
        if (!hote.StartsWith('[') && hote.LastIndexOf(':') is var deuxPoints and > 0) hote = hote[..deuxPoints];
        hote = hote.Trim('[', ']');

        return IPAddress.TryParse(hote, out _)
            || hote.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || !hote.Contains('.')
            || hote.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }
}
