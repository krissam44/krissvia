using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Krissvia.Plugin.Livebox;

/// <summary>Un appareil du réseau tel que la box le voit.</summary>
public sealed record LiveboxHost(string Mac, string Name, bool Active, string? Ip, string? Type);

/// <summary>La connexion Internet : chaque champ est facultatif, un firmware peut ne pas l'exposer.</summary>
public sealed record WanStatus(bool? Up, string? PublicIp, string? LinkType, int? DownKbps, int? UpKbps);

/// <summary>Ce que le plugin demande à la box : de quoi la simuler dans les tests.</summary>
public interface ILiveboxApi
{
    /// <summary>Les appareils connus de la box. Lève une exception si la box ne répond pas ou refuse la connexion.</summary>
    Task<IReadOnlyList<LiveboxHost>> HostsAsync(CancellationToken ct);

    /// <summary>L'état de la connexion, ou null si la box ne l'expose pas.</summary>
    Task<WanStatus?> WanAsync(CancellationToken ct);
}

public sealed class LiveboxAuthenticationException(string message) : Exception(message);

/// <summary>
/// L'API locale « sysbus » de la Livebox (DomoMud, Livebox 4 et suivantes) : <c>POST http://{box}/ws</c>.
/// Authentification : <c>sah.Device.Information:createContext</c> avec l'en-tête <c>Authorization: X-Sah-Login</c>,
/// qui rend un <c>contextID</c> à renvoyer ensuite dans <c>X-Context</c>, cookies de session compris.
/// </summary>
/// <remarks>Lecture seule : le plugin n'appelle que des méthodes de consultation (Devices:get, NMC:getWANStatus, getMIBs).</remarks>
public sealed class SysbusClient(string host, string username, string password) : ILiveboxApi, IDisposable
{
    private static readonly MediaTypeHeaderValue TypeSah = new("application/x-sah-ws-1-call+json");

    private CookieContainer _cookies = new();
    private HttpClient _http = Client(new CookieContainer());
    private string? _contexte;

    private static HttpClient Client(CookieContainer cookies) =>
        new(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { Timeout = TimeSpan.FromSeconds(10) };

    private Uri Adresse => new($"http://{host}/ws");

    public async Task<IReadOnlyList<LiveboxHost>> HostsAsync(CancellationToken ct) =>
        LiveboxParser.Hosts(await AppelAuthentifieAsync("Devices", "get", null, ct));

    public async Task<WanStatus?> WanAsync(CancellationToken ct)
    {
        var wan = await AppelAuthentifieAsync("NMC", "getWANStatus", null, ct);
        string? dsl = null;
        try
        {
            dsl = await AppelAuthentifieAsync("NeMo.Intf.dsl0", "getMIBs", new { mibs = "dsl" }, ct);
        }
        catch (HttpRequestException)
        {
            // Pas de DSL (fibre) : les débits restent vides.
        }

        return LiveboxParser.Wan(wan, dsl);
    }

    /// <summary>Un appel, avec une seule réauthentification si la session a expiré.</summary>
    private async Task<string> AppelAuthentifieAsync(string service, string methode, object? parametres, CancellationToken ct)
    {
        if (_contexte is null) await AuthentifierAsync(ct);
        var reponse = await AppelAsync(service, methode, parametres, ct);
        if (reponse is not null) return reponse;

        await AuthentifierAsync(ct);
        return await AppelAsync(service, methode, parametres, ct)
               ?? throw new HttpRequestException($"{service}:{methode} refusé par la box après réauthentification");
    }

    private async Task AuthentifierAsync(CancellationToken ct)
    {
        // Session neuve : les cookies de l'ancienne ne valent plus rien.
        _http.Dispose();
        _cookies = new CookieContainer();
        _http = Client(_cookies);
        _contexte = null;

        using var requete = new HttpRequestMessage(HttpMethod.Post, Adresse)
        {
            Content = Corps(new
            {
                service = "sah.Device.Information",
                method = "createContext",
                parameters = new { applicationName = "so_sdkut", username, password },
            }),
        };
        requete.Headers.TryAddWithoutValidation("Authorization", "X-Sah-Login");
        using var reponse = await _http.SendAsync(requete, ct);
        var texte = await reponse.Content.ReadAsStringAsync(ct);
        _contexte = LiveboxParser.ContextId(texte)
                    ?? throw new LiveboxAuthenticationException("authentification refusée par la box : vérifier l'identifiant et le mot de passe administrateur");
    }

    private async Task<string?> AppelAsync(string service, string methode, object? parametres, CancellationToken ct)
    {
        using var requete = new HttpRequestMessage(HttpMethod.Post, Adresse) { Content = Corps(new { service, method = methode, parameters = parametres ?? new { } }) };
        requete.Headers.TryAddWithoutValidation("X-Context", _contexte);
        requete.Headers.TryAddWithoutValidation("X-Prototype-Version", "1.7");
        using var reponse = await _http.SendAsync(requete, ct);
        if (!reponse.IsSuccessStatusCode) return null;
        var texte = await reponse.Content.ReadAsStringAsync(ct);
        return LiveboxParser.IsPermissionDenied(texte) ? null : texte;
    }

    private static StringContent Corps(object corps)
    {
        var contenu = new StringContent(JsonSerializer.Serialize(corps), Encoding.UTF8);
        contenu.Headers.ContentType = TypeSah;
        return contenu;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Lecture des réponses de la box, sans réseau : la forme exacte varie selon le firmware.</summary>
public static class LiveboxParser
{
    /// <summary>Le contextID d'une réponse d'authentification, ou null si elle est refusée.</summary>
    public static string? ContextId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("contextID", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Une session expirée répond 200 avec une erreur « Permission denied » (code 13) plutôt qu'un 401.</summary>
    public static bool IsPermissionDenied(string json) =>
        json.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) && json.Contains("\"errors\"", StringComparison.Ordinal);

    /// <summary>
    /// Les appareils de Devices:get. La liste est tantôt sous « status », tantôt sous « data », parfois imbriquée : on
    /// cherche dans tout le document les objets qui portent une adresse physique (DomoMud), une seule fois par adresse.
    /// </summary>
    public static IReadOnlyList<LiveboxHost> Hosts(string json)
    {
        var appareils = new List<LiveboxHost>();
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        Explorer(doc.RootElement, appareils, vus, 0);
        return appareils;
    }

    private static void Explorer(JsonElement e, List<LiveboxHost> appareils, HashSet<string> vus, int profondeur)
    {
        if (profondeur > 6) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Explorer(item, appareils, vus, profondeur + 1);
                break;
            case JsonValueKind.Object:
                if ((Texte(e, "PhysAddress") ?? Texte(e, "MACAddress")) is { Length: > 0 } mac && vus.Add(Normaliser(mac)))
                {
                    appareils.Add(new LiveboxHost(
                        Normaliser(mac),
                        Texte(e, "Name") ?? Texte(e, "HostName") ?? Texte(e, "UserHostname") ?? Normaliser(mac),
                        e.TryGetProperty("Active", out var a) && a.ValueKind == JsonValueKind.True,
                        Texte(e, "IPAddress"),
                        Texte(e, "DeviceType")));
                }

                foreach (var p in e.EnumerateObject()) Explorer(p.Value, appareils, vus, profondeur + 1);
                break;
        }
    }

    /// <summary>« aa-bb-cc… » ou « AA:BB:CC… » → « AA:BB:CC:… » : la forme qu'on compare et qu'on affiche.</summary>
    public static string Normaliser(string mac) => mac.Trim().Replace('-', ':').ToUpperInvariant();

    public static WanStatus? Wan(string? wanJson, string? dslJson)
    {
        string? ip = null, lien = null;
        bool? up = null;
        if (wanJson is not null && Donnees(wanJson) is { } wan)
        {
            ip = Texte(wan, "IPAddress") is { Length: > 0 } i ? i : null;
            lien = Texte(wan, "LinkType");
            up = Texte(wan, "LinkState") is { } etat ? string.Equals(etat, "up", StringComparison.OrdinalIgnoreCase) : null;
        }

        int? descendant = null, montant = null;
        if (dslJson is not null)
        {
            using var doc = JsonDocument.Parse(dslJson);
            descendant = Nombre(doc.RootElement, "DownstreamCurrRate", 0);
            montant = Nombre(doc.RootElement, "UpstreamCurrRate", 0);
        }

        return up is null && ip is null && descendant is null ? null : new WanStatus(up, ip, lien, descendant, montant);
    }

    /// <summary>{"result":{"data":{…}}} pour les services, {"data":{…}} pour l'authentification, parfois sous « status ».</summary>
    private static JsonElement? Donnees(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var racine = doc.RootElement;
            if (racine.TryGetProperty("result", out var r) && r.TryGetProperty("data", out var d1) && d1.ValueKind == JsonValueKind.Object) return d1.Clone();
            if (racine.TryGetProperty("data", out var d2) && d2.ValueKind == JsonValueKind.Object) return d2.Clone();
            if (racine.TryGetProperty("result", out var r2) && r2.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object) return s.Clone();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? Texte(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Nombre(JsonElement e, string nom, int profondeur)
    {
        if (profondeur > 8) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals(nom) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i)) return i;
                    if (Nombre(p.Value, nom, profondeur + 1) is { } r) return r;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (Nombre(item, nom, profondeur + 1) is { } r) return r;
                break;
        }

        return null;
    }
}
