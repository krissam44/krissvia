using System.Text.Json;

namespace Krissvia.Plugin.Shelly;

/// <summary>Accès HTTP à un appareil Shelly. Simulé dans les tests.</summary>
internal interface IShellyHttp
{
    /// <summary>Réponse JSON de <c>http://{host}{chemin}</c>, ou null si l'appareil ne répond pas correctement.</summary>
    Task<JsonElement?> GetAsync(string host, string chemin, CancellationToken ct);
}

internal sealed class ShellyHttp(HttpClient client) : IShellyHttp
{
    public async Task<JsonElement?> GetAsync(string host, string chemin, CancellationToken ct)
    {
        try
        {
            using var reponse = await client.GetAsync($"http://{host}{chemin}", ct);
            if (!reponse.IsSuccessStatusCode) return null;

            await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(flux, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;   // injoignable, trop lent ou réponse illisible : c'est un échec de lecture, pas une exception
        }
    }
}
