using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Mobile.Client;

/// <summary>Où joindre Krissvia : fourni par l'appli (Preferences et coffre de l'appareil), lu à chaque appel.</summary>
public interface IConnexion
{
    /// <summary>Adresse normalisée (<see cref="AdresseServeur.Normaliser"/>), vide tant que rien n'est réglé.</summary>
    string BaseUrl { get; }

    string ApiKey { get; }
}

/// <summary>Résultat d'un ordre : <see cref="Motif"/> est à afficher tel quel en cas d'échec.</summary>
public sealed record ResultatOrdre(bool Ok, string? Motif)
{
    public static readonly ResultatOrdre Reussi = new(true, null);

    public static ResultatOrdre Echec(string motif) => new(false, motif);
}

/// <summary>
/// Client de l'API publique de Krissvia (API.md), en clé d'API (<c>X-Api-Key</c>). Le téléphone est un client léger :
/// il lit les entités et envoie des ordres, sans aucune logique domotique.
/// </summary>
public sealed class KrissviaApi(HttpClient http, IConnexion connexion)
{
    public const string EnTeteCle = "X-Api-Key";

    /// <summary>Signe les ordres au journal du serveur : « api:mobile ».</summary>
    public const string Source = "mobile";

    /// <summary>Les thermostats et leur dernier état, d'un coup (<c>/api/entities</c>, filtré ici sur les climate).</summary>
    public Task<(JsonElement? Entites, string? Erreur)> EntitesAsync(CancellationToken ct = default) => LireAsync("/api/entities", ct);

    /// <summary>Un GET, rendu en JSON brut ; une erreur (réseau, clé, route) rend son motif lisible.</summary>
    private async Task<(JsonElement? Json, string? Erreur)> LireAsync(string route, CancellationToken ct)
    {
        if (connexion.BaseUrl.Length == 0) return (null, "Serveur non configuré.");
        try
        {
            using var requete = Requete(HttpMethod.Get, route);
            using var reponse = await http.SendAsync(requete, ct);
            if (await RefusAsync(reponse, route, ct) is { } refus) return (null, refus);

            await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(flux, cancellationToken: ct);
            return (document.RootElement.Clone(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, MessageLisible(ex));
        }
    }

    /// <summary>L'écran Sondes calculé par le serveur : moyennes par pièce et état de chaque sonde.</summary>
    public async Task<(VueSondes? Vue, string? Erreur)> SondesAsync(CancellationToken ct = default)
    {
        var (json, erreur) = await LireAsync("/api/sensors", ct);
        if (erreur is not null) return (null, erreur);
        try
        {
            return VueSondes.Lire(json!.Value) is { } vue ? (vue, null) : (null, "Réponse vide du serveur.");
        }
        catch (JsonException ex)
        {
            return (null, MessageLisible(ex));
        }
    }

    /// <summary>Teste l'adresse et la clé : null si tout va bien, sinon le motif.</summary>
    public async Task<string?> TesterAsync(CancellationToken ct = default) => (await EntitesAsync(ct)).Erreur;

    public Task<ResultatOrdre> ChoisirModeAsync(string entityId, string mode, CancellationToken ct = default) =>
        OrdreAsync(entityId, StandardCommands.SetMode, new { mode }, ct);

    /// <summary>Relatif : appliqué par le serveur à la consigne suivie, deux « + » rapides font bien +1 °C.</summary>
    public Task<ResultatOrdre> AjusterConsigneAsync(string entityId, double delta, CancellationToken ct = default) =>
        OrdreAsync(entityId, StandardCommands.AdjustTarget, new { delta }, ct);

    public Task<ResultatOrdre> ActiverPlanningAsync(string entityId, bool actif, CancellationToken ct = default) =>
        OrdreAsync(entityId, StandardCommands.SetSchedule, new { active = actif }, ct);

    /// <summary>
    /// Un ordre par le bus de commandes (<c>POST /api/entities/{id}/commands</c>). Le nouvel état n'est pas lu ici : il
    /// arrive par le flux, et l'écran n'affiche rien d'optimiste.
    /// </summary>
    public async Task<ResultatOrdre> OrdreAsync(string entityId, string commande, object? args, CancellationToken ct = default)
    {
        if (connexion.BaseUrl.Length == 0) return ResultatOrdre.Echec("Serveur non configuré.");
        try
        {
            var route = $"/api/entities/{Uri.EscapeDataString(entityId)}/commands";
            using var requete = Requete(HttpMethod.Post, route);
            requete.Content = JsonContent.Create(new { command = commande, args, source = Source });
            using var reponse = await http.SendAsync(requete, ct);
            if (await RefusAsync(reponse, route, ct) is { } refus) return ResultatOrdre.Echec(refus);

            var corps = await reponse.Content.ReadFromJsonAsync<ReponseOrdre>(Thermostat.Json, ct);
            return corps?.Outcome == "accepted"
                ? ResultatOrdre.Reussi
                : ResultatOrdre.Echec(corps?.Detail is { Length: > 0 } d ? d : $"Ordre refusé ({corps?.Outcome ?? "réponse vide"}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ResultatOrdre.Echec(MessageLisible(ex));
        }
    }

    internal HttpRequestMessage Requete(HttpMethod methode, string route)
    {
        var requete = new HttpRequestMessage(methode, connexion.BaseUrl + route);
        if (connexion.ApiKey.Length > 0) requete.Headers.TryAddWithoutValidation(EnTeteCle, connexion.ApiKey);
        return requete;
    }

    /// <summary>Le motif d'une réponse en erreur, null si elle a réussi. Le <c>error</c> du serveur se lit tel quel.</summary>
    private static async Task<string?> RefusAsync(HttpResponseMessage reponse, string route, CancellationToken ct)
    {
        if (reponse.IsSuccessStatusCode) return null;

        string? erreur = null;
        try
        {
            erreur = (await reponse.Content.ReadFromJsonAsync<ReponseErreur>(Thermostat.Json, ct))?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Pas de corps JSON : le code suffit.
        }

        return reponse.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Clé d'API refusée : vérifiez-la dans la connexion.",
            // En production sans clé configurée, l'API se ferme au lieu de s'ouvrir à tout le réseau.
            HttpStatusCode.ServiceUnavailable => "API fermée : aucune clé d'API n'est configurée sur le serveur (Api__Key).",
            // Une appli plus récente que le serveur tombe ici : le dire évite de chercher l'erreur dans l'adresse.
            HttpStatusCode.NotFound when erreur is null => $"Route {route} introuvable : serveur Krissvia à mettre à jour, ou adresse qui ne pointe pas sur Krissvia.",
            _ => erreur ?? $"Erreur du serveur ({(int)reponse.StatusCode}).",
        };
    }

    /// <summary>
    /// Une exception réseau en message lisible, SANS masquer la cause : un « serveur injoignable » générique empêche de
    /// distinguer un DNS qui échoue d'un certificat refusé par le téléphone.
    /// </summary>
    internal static string MessageLisible(Exception ex)
    {
        var racine = ex;
        while (racine.InnerException is not null) racine = racine.InnerException;

        return ex switch
        {
            JsonException => "Réponse inattendue du serveur : l'adresse pointe-t-elle bien sur Krissvia ?",
            TaskCanceledException => "Délai dépassé : serveur injoignable ?",
            _ when racine is AuthenticationException => $"Connexion sécurisée refusée : certificat non reconnu par l'appareil. ({racine.Message})",
            HttpRequestException { StatusCode: null } => $"Serveur injoignable : vérifiez l'adresse et le réseau. ({racine.Message})",
            _ => racine.Message,
        };
    }

    private sealed record ReponseOrdre(string? Outcome, string? Verification, string? Detail);

    private sealed record ReponseErreur(string? Error);
}
