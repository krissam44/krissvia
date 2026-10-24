using System.Net.Http.Headers;

namespace Krissvia.Plugin.Ipp.Protocole;

/// <summary>L'imprimante a répondu, mais par une erreur IPP (attribut refusé, imprimante inconnue à cette adresse…).</summary>
public sealed class IppStatusException(ushort status) : Exception($"l'imprimante répond « {Libelle(status)} » (0x{status:X4})")
{
    public ushort Status { get; } = status;

    private static string Libelle(ushort s) => s switch
    {
        0x0400 => "requête invalide",
        0x0401 => "interdit",
        0x0402 => "authentification requise",
        0x0403 => "non autorisé",
        0x0406 => "introuvable : vérifier le chemin (/ipp/print, /ipp, /printers/…)",
        0x0500 => "erreur interne",
        0x0501 => "opération non prise en charge",
        0x0502 => "service indisponible",
        0x0503 => "version d'IPP non prise en charge",
        _ => s >= 0x0500 ? "erreur de l'imprimante" : "requête refusée",
    };
}

/// <summary>Les adresses d'une imprimante : celle qu'on saisit, celle du transport HTTP, celle qu'on cite dans la requête.</summary>
public static class IppAdresses
{
    public const int PortIpp = 631;
    public const string CheminParDefaut = "/ipp/print";

    /// <summary>
    /// Accepte <c>ipp://</c>, <c>ipps://</c>, <c>http://</c>, <c>https://</c>, ou une adresse seule (« 192.168.1.63 »,
    /// complétée en <c>ipp://192.168.1.63:631/ipp/print</c>, le chemin d'IPP Everywhere). Rend l'adresse en <c>ipp(s)://</c>,
    /// port explicite : deux saisies de la même imprimante donnent la même chaîne.
    /// </summary>
    public static bool TryNormaliser(string? saisie, out Uri adresse)
    {
        adresse = null!;
        saisie = saisie?.Trim();
        if (string.IsNullOrEmpty(saisie)) return false;
        if (!saisie.Contains("://", StringComparison.Ordinal)) saisie = $"ipp://{saisie}";
        if (!Uri.TryCreate(saisie, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return false;

        var schema = u.Scheme.ToLowerInvariant() switch
        {
            "ipp" or "http" => "ipp",
            "ipps" or "https" => "ipps",
            _ => null,
        };
        if (schema is null) return false;

        // Uri donne 80/443 aux schémas http(s) sans port : une imprimante IPP écoute sur 631, sauf port écrit.
        var debut = saisie.IndexOf("://", StringComparison.Ordinal) + 3;
        var fin = saisie.IndexOfAny(['/', '?', '#'], debut);
        var autorite = fin < 0 ? saisie[debut..] : saisie[debut..fin];
        var port = autorite.LastIndexOf(':') > autorite.LastIndexOf(']') ? u.Port : PortIpp;   // « ] » : adresse IPv6
        var chemin = u.AbsolutePath is "/" or "" ? CheminParDefaut : u.AbsolutePath;
        adresse = new UriBuilder(schema, u.Host, port, chemin).Uri;
        return true;
    }

    /// <summary>L'adresse HTTP où poster la requête : IPP passe par HTTP (RFC 8010 §4), IPPS par HTTPS.</summary>
    public static Uri Transport(Uri ipp) =>
        new UriBuilder(ipp.Scheme == "ipps" ? "https" : "http", ipp.Host, ipp.Port, ipp.AbsolutePath).Uri;
}

/// <summary>Ce que le plugin demande à une imprimante. Une seule opération : une lecture.</summary>
public interface IIppClient
{
    Task<IppResponse> GetPrinterAttributesAsync(Uri imprimante, CancellationToken ct);
}

/// <summary>
/// IPP sur HTTP : un POST <c>application/ipp</c> de la requête encodée. Le certificat d'une imprimante en IPPS est
/// toujours auto-signé : il est accepté, comme Gladys et CUPS le font. Aucune autre opération que
/// <c>Get-Printer-Attributes</c> ne peut partir d'ici.
/// </summary>
public sealed class IppHttpClient : IIppClient, IDisposable
{
    /// <summary>Les attributs lus : état, motifs, consommables, identité.</summary>
    public static readonly IReadOnlyList<string> Attributs =
    [
        "printer-state", "printer-state-reasons", "printer-state-message",
        "marker-names", "marker-levels", "marker-low-levels", "marker-high-levels", "marker-types", "marker-colors",
        "printer-supply", "printer-supply-description",
        "printer-name", "printer-info", "printer-make-and-model", "printer-uuid", "printer-location",
    ];

    /// <summary>Une réponse plus grosse n'est pas celle d'une imprimante.</summary>
    public const int TailleMaximale = 1024 * 1024;

    private readonly HttpClient _http;
    private int _requete;

    public IppHttpClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IppResponse> GetPrinterAttributesAsync(Uri imprimante, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requete);
        var corps = new ByteArrayContent(IppCodec.EncodeGetPrinterAttributes(imprimante.ToString(), id, Attributs));
        corps.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");

        using var requete = new HttpRequestMessage(HttpMethod.Post, IppAdresses.Transport(imprimante)) { Content = corps };
        using var reponse = await _http.SendAsync(requete, HttpCompletionOption.ResponseHeadersRead, ct);
        reponse.EnsureSuccessStatusCode();
        if (reponse.Content.Headers.ContentLength > TailleMaximale) throw new IppFormatException("réponse trop grande");

        await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
        using var tampon = new MemoryStream();
        var bloc = new byte[16 * 1024];
        int lus;
        while ((lus = await flux.ReadAsync(bloc, ct)) > 0)
        {
            if (tampon.Length + lus > TailleMaximale) throw new IppFormatException("réponse trop grande");
            tampon.Write(bloc, 0, lus);
        }

        var ipp = IppCodec.Decode(tampon.GetBuffer().AsSpan(0, (int)tampon.Length));
        return ipp.Success ? ipp : throw new IppStatusException(ipp.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
