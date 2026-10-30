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

/// <summary>
/// Résultat d'un ordre : <see cref="Motif"/> est à afficher tel quel en cas d'échec. <see cref="NonConfirme"/> : accepté,
/// mais l'appareil n'a pas montré le nouvel état dans le délai (<c>verification: not_confirmed</c>) — ni un succès, ni
/// un échec, à dire comme tel.
/// </summary>
public sealed record ResultatOrdre(bool Ok, string? Motif, bool NonConfirme = false)
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
    public async Task<(JsonElement? Entites, string? Erreur)> EntitesAsync(CancellationToken ct = default)
    {
        var (json, erreur, _) = await LireAsync("/api/entities", ct);
        return (json, erreur);
    }

    /// <summary>
    /// Un GET, rendu en JSON brut ; une erreur (réseau, clé, route) rend son motif lisible. <c>RouteAbsente</c> : le
    /// serveur répond, mais ne connaît pas la route — il est plus ancien que l'appli.
    /// </summary>
    private async Task<(JsonElement? Json, string? Erreur, bool RouteAbsente)> LireAsync(string route, CancellationToken ct)
    {
        if (connexion.BaseUrl.Length == 0) return (null, "Serveur non configuré.", false);
        try
        {
            using var requete = Requete(HttpMethod.Get, route);
            using var reponse = await http.SendAsync(requete, ct);
            if (await RefusAsync(reponse, route, ct) is ({ } refus, var absente)) return (null, refus, absente);

            await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(flux, cancellationToken: ct);
            return (document.RootElement.Clone(), null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, MessageLisible(ex), false);
        }
    }

    /// <summary>
    /// L'écran Sondes calculé par le serveur : moyennes par pièce et état de chaque sonde. <c>ServeurAncien</c> : la
    /// connexion est bonne, mais le serveur n'a pas encore <c>/api/sensors</c> — à dire autrement qu'une panne, sinon
    /// on cherche le problème dans l'adresse ou la clé.
    /// </summary>
    public async Task<(VueSondes? Vue, string? Erreur, bool ServeurAncien)> SondesAsync(CancellationToken ct = default)
    {
        var (json, erreur, absente) = await LireAsync("/api/sensors", ct);
        if (erreur is not null) return (null, erreur, absente);
        try
        {
            return VueSondes.Lire(json!.Value) is { } vue ? (vue, null, false) : (null, "Réponse vide du serveur.", false);
        }
        catch (JsonException ex)
        {
            return (null, MessageLisible(ex), false);
        }
    }

    /// <summary>L'écran Actionneurs calculé par le serveur : par pièce, avec ce qui pilote déjà chaque appareil.</summary>
    public async Task<(VueActionneurs? Vue, string? Erreur, bool ServeurAncien)> ActionneursAsync(CancellationToken ct = default)
    {
        var (json, erreur, absente) = await LireAsync("/api/actuators", ct);
        if (erreur is not null) return (null, erreur, absente);
        try
        {
            return VueActionneurs.Lire(json!.Value) is { } vue ? (vue, null, false) : (null, "Réponse vide du serveur.", false);
        }
        catch (JsonException ex)
        {
            return (null, MessageLisible(ex), false);
        }
    }

    /// <summary>Allumer ou éteindre : un ordre vérifié, dont le résultat dit si l'appareil a suivi.</summary>
    public Task<ResultatOrdre> AllumerAsync(string entityId, bool allumer, CancellationToken ct = default) =>
        OrdreAsync(entityId, allumer ? StandardCommands.TurnOn : StandardCommands.TurnOff, null, ct);

    /// <summary>Les compteurs : chiffres du moment, consommation depuis minuit et des <paramref name="jours"/> derniers jours.</summary>
    public async Task<(VueEnergie? Vue, string? Erreur, bool ServeurAncien)> EnergieAsync(int jours = 7, CancellationToken ct = default)
    {
        var (json, erreur, absente) = await LireAsync($"/api/energy?days={jours}", ct);
        if (erreur is not null) return (null, erreur, absente);
        try
        {
            return VueEnergie.Lire(json!.Value) is { } vue ? (vue, null, false) : (null, "Réponse vide du serveur.", false);
        }
        catch (JsonException ex)
        {
            return (null, MessageLisible(ex), false);
        }
    }

    /// <summary>L'état de l'alarme, les défauts du moment et le blocage du clavier.</summary>
    public async Task<(EtatAlarme? Etat, string? Erreur)> AlarmeAsync(CancellationToken ct = default)
    {
        var (json, erreur, _) = await LireAsync("/api/alarm", ct);
        if (erreur is not null) return (null, erreur);
        try
        {
            return EtatAlarme.Lire(json!.Value) is { } etat ? (etat, null) : (null, "Réponse vide du serveur.");
        }
        catch (JsonException ex)
        {
            return (null, MessageLisible(ex));
        }
    }

    /// <summary>
    /// Armer ou désarmer PAR CODE : la clé d'API ne suffit pas (ALARME.md §6). Le code part dans le corps, jamais dans
    /// l'adresse. Les refus du serveur — code incorrect (403), clavier bloqué (429), armement refusé ou alarme
    /// désactivée (409) — reviennent avec son motif.
    /// </summary>
    public async Task<ResultatCode> CommanderAlarmeAsync(bool armer, string code, CancellationToken ct = default)
    {
        if (connexion.BaseUrl.Length == 0) return new ResultatCode(false, "Serveur non configuré.");
        try
        {
            var route = armer ? "/api/alarm/arm" : "/api/alarm/disarm";
            using var requete = Requete(HttpMethod.Post, route);
            requete.Content = JsonContent.Create(new { code });
            using var reponse = await http.SendAsync(requete, ct);
            if (await RefusAsync(reponse, route, ct) is ({ } refus, _)) return new ResultatCode(false, Majuscule(refus));

            var corps = await reponse.Content.ReadFromJsonAsync<ReponseCode>(Thermostat.Json, ct);
            var qui = corps?.User is { Length: > 0 } u ? $" par {u}" : "";
            return new ResultatCode(true, (armer ? "Armement lancé" : "Alarme désarmée") + qui + ".");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ResultatCode(false, MessageLisible(ex));
        }
    }

    /// <summary>La présence détectée et l'absence qui s'applique aux thermostats (<c>GET /api/absence</c>).</summary>
    public async Task<(EtatPresence? Etat, string? Erreur)> PresenceAsync(CancellationToken ct = default)
    {
        var (json, erreur, _) = await LireAsync("/api/absence", ct);
        if (erreur is not null) return (null, erreur);
        try
        {
            return EtatPresence.Lire(json!.Value) is { } etat ? (etat, null) : (null, "Réponse vide du serveur.");
        }
        catch (JsonException ex)
        {
            return (null, MessageLisible(ex));
        }
    }

    /// <summary>
    /// Forcer présent ou absent, ou rendre la main à la période et à la détection (<c>PUT /api/absence/forced</c>,
    /// <c>{"away": true | false | null}</c>). Le forçage l'emporte sur tout : les thermostats suivent aussitôt.
    /// </summary>
    public async Task<ResultatOrdre> ForcerAbsenceAsync(Forcage forcage, CancellationToken ct = default)
    {
        if (connexion.BaseUrl.Length == 0) return ResultatOrdre.Echec("Serveur non configuré.");
        try
        {
            const string route = "/api/absence/forced";
            using var requete = Requete(HttpMethod.Put, route);
            bool? away = forcage switch { Forcage.Absent => true, Forcage.Present => false, _ => null };
            requete.Content = JsonContent.Create(new { away });
            using var reponse = await http.SendAsync(requete, ct);
            return await RefusAsync(reponse, route, ct) is ({ } refus, _) ? ResultatOrdre.Echec(refus) : ResultatOrdre.Reussi;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ResultatOrdre.Echec(MessageLisible(ex));
        }
    }

    private static string Majuscule(string texte) => texte.Length == 0 ? texte : char.ToUpperInvariant(texte[0]) + texte[1..];

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
            if (await RefusAsync(reponse, route, ct) is ({ } refus, _)) return ResultatOrdre.Echec(refus);

            var corps = await reponse.Content.ReadFromJsonAsync<ReponseOrdre>(Thermostat.Json, ct);
            return corps?.Outcome != "accepted"
                ? ResultatOrdre.Echec(corps?.Detail is { Length: > 0 } d ? d : $"Ordre refusé ({corps?.Outcome ?? "réponse vide"}).")
                : corps.Verification == "not_confirmed"
                    ? new ResultatOrdre(true, "l'appareil n'a pas confirmé l'ordre", NonConfirme: true)
                    : ResultatOrdre.Reussi;
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

    /// <summary>
    /// Le motif d'une réponse en erreur, null si elle a réussi. Le <c>error</c> du serveur se lit tel quel. Un 404 sans
    /// <c>error</c> est une route que le serveur ne connaît pas (un 404 de Krissvia sur une entité porte son motif).
    /// </summary>
    private static async Task<(string? Motif, bool RouteAbsente)> RefusAsync(HttpResponseMessage reponse, string route, CancellationToken ct)
    {
        if (reponse.IsSuccessStatusCode) return (null, false);

        string? erreur = null;
        try
        {
            erreur = (await reponse.Content.ReadFromJsonAsync<ReponseErreur>(Thermostat.Json, ct))?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Pas de corps JSON : le code suffit.
        }

        var absente = reponse.StatusCode == HttpStatusCode.NotFound && erreur is null;
        return (reponse.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Clé d'API refusée : vérifiez-la dans la connexion.",
            // En production sans clé configurée, l'API se ferme au lieu de s'ouvrir à tout le réseau.
            HttpStatusCode.ServiceUnavailable => "API fermée : aucune clé d'API n'est configurée sur le serveur (Api__Key).",
            // Une appli plus récente que le serveur tombe ici : le dire évite de chercher l'erreur dans l'adresse.
            HttpStatusCode.NotFound when erreur is null => $"Route {route} introuvable : serveur Krissvia à mettre à jour, ou adresse qui ne pointe pas sur Krissvia.",
            _ => erreur ?? $"Erreur du serveur ({(int)reponse.StatusCode}).",
        }, absente);
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

    private sealed record ReponseCode(string? User);
}
