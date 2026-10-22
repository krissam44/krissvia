using System.Net;

using System.Text.Json;

namespace Krissvia.Plugin.Proxmox;

/// <summary>Un nœud du cluster (le serveur physique).</summary>
public sealed record ProxmoxNode(string Name, bool Online, double CpuFraction, int MaxCpu, long Mem, long MaxMem, long Disk, long MaxDisk, long Uptime);

/// <summary>Une machine virtuelle (<c>qemu</c>) ou un conteneur (<c>lxc</c>).</summary>
public sealed record ProxmoxGuest(
    int VmId, string Node, string Type, string Name, string Status,
    double CpuFraction, int MaxCpu, long Mem, long MaxMem, long Disk, long MaxDisk, long Uptime)
{
    public bool Running => Status == "running";
    public bool IsVm => Type == "qemu";
    public string TypeLabel => IsVm ? "VM" : "LXC";
}

/// <summary>Un relevé du cluster.</summary>
/// <param name="NoRights">Réponse vide : le jeton n'a le droit de rien voir (privilège séparé, rôle manquant).</param>
public sealed record ProxmoxSnapshot(IReadOnlyList<ProxmoxNode> Nodes, IReadOnlyList<ProxmoxGuest> Guests, bool NoRights);

/// <summary>Ce que le plugin demande à Proxmox. Simulé dans les tests.</summary>
public interface IProxmoxApi
{
    Task<ProxmoxSnapshot> ResourcesAsync(CancellationToken ct);

    /// <summary><c>start</c>, <c>shutdown</c> (arrêt propre) ou <c>reboot</c>.</summary>
    Task ActionAsync(ProxmoxGuest guest, string action, CancellationToken ct);
}

/// <summary>Jeton refusé (401) : ni le délai ni la patience n'y changeront rien.</summary>
public sealed class ProxmoxAuthenticationException(string message) : Exception(message);

/// <summary>
/// L'API REST de Proxmox VE, avec un jeton d'API (<c>Authorization: PVEAPIToken=user@realm!nom=secret</c>) : pas de
/// ticket ni de jeton CSRF à gérer. Certificat auto-signé accepté par défaut : c'est celui de tout nœud Proxmox.
/// </summary>
public sealed class ProxmoxHttpClient : IProxmoxApi, IDisposable
{
    private readonly HttpClient _http;

    public ProxmoxHttpClient(string host, int port, string tokenId, string tokenSecret, bool verifyTls, HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            var h = new HttpClientHandler();
            if (!verifyTls) h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            handler = h;
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}"), Timeout = TimeSpan.FromSeconds(12) };
        // « PVEAPIToken=id=secret », sans espace : AuthenticationHeaderValue en mettrait un, que Proxmox refuse.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", AuthorizationValue(tokenId, tokenSecret));
    }

    public static string AuthorizationValue(string tokenId, string tokenSecret) => $"PVEAPIToken={tokenId}={tokenSecret}";

    public async Task<ProxmoxSnapshot> ResourcesAsync(CancellationToken ct)
    {
        using var reponse = await _http.GetAsync("/api2/json/cluster/resources", ct);
        Verifier(reponse);
        return ProxmoxParser.Resources(await reponse.Content.ReadAsStringAsync(ct));
    }

    public async Task ActionAsync(ProxmoxGuest guest, string action, CancellationToken ct)
    {
        using var reponse = await _http.PostAsync($"/api2/json/nodes/{Uri.EscapeDataString(guest.Node)}/{guest.Type}/{guest.VmId}/status/{action}", null, ct);
        Verifier(reponse);
    }

    private static void Verifier(HttpResponseMessage reponse)
    {
        if (reponse.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProxmoxAuthenticationException("jeton refusé (401) : vérifier l'identifiant user@realm!nom et le secret");
        if (reponse.StatusCode == HttpStatusCode.Forbidden)
            throw new ProxmoxAuthenticationException("action refusée (403) : le jeton n'a pas le droit VM.PowerMgmt sur cette machine");
        reponse.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Lecture de <c>cluster/resources</c>, en tolérant les champs absents d'une version à l'autre.</summary>
public static class ProxmoxParser
{
    public static ProxmoxSnapshot Resources(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("réponse sans « data »");

        var noeuds = new List<ProxmoxNode>();
        var invites = new List<ProxmoxGuest>();
        foreach (var e in data.EnumerateArray())
        {
            switch (Texte(e, "type"))
            {
                case "node":
                    noeuds.Add(new ProxmoxNode(Texte(e, "node") ?? "?", Texte(e, "status") == "online", Reel(e, "cpu"), (int)Entier(e, "maxcpu"),
                        Entier(e, "mem"), Entier(e, "maxmem"), Entier(e, "disk"), Entier(e, "maxdisk"), Entier(e, "uptime")));
                    break;
                case "qemu" or "lxc" when Entier(e, "template") != 1:   // un modèle n'est pas un invité
                    var vmid = (int)Entier(e, "vmid");
                    invites.Add(new ProxmoxGuest(vmid, Texte(e, "node") ?? "", Texte(e, "type")!, Texte(e, "name") ?? $"vm{vmid}",
                        Texte(e, "status") ?? "unknown", Reel(e, "cpu"), (int)Entier(e, "maxcpu"), Entier(e, "mem"), Entier(e, "maxmem"),
                        Entier(e, "disk"), Entier(e, "maxdisk"), Entier(e, "uptime")));
                    break;
            }
        }

        return new ProxmoxSnapshot([.. noeuds.OrderBy(n => n.Name, StringComparer.Ordinal)], [.. invites.OrderBy(g => g.VmId)], data.GetArrayLength() == 0);
    }

    private static string? Texte(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Entier(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number ? (v.TryGetInt64(out var l) ? l : (long)v.GetDouble()) : 0;

    private static double Reel(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
