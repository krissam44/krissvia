using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Climate.Thermostats;

namespace Krissvia.Server;

/// <summary>Un thermostat tel que l'API le montre : sa configuration et son état du moment.</summary>
internal sealed record ThermostatView(ThermostatSettings Settings, ThermostatSnapshot? State);

internal sealed record ModeRequest(ThermostatMode Mode);
internal sealed record TargetRequest(double? Temperature);
internal sealed record ScheduleRequest(bool Active);

/// <summary>
/// API des thermostats (<c>/api/thermostats</c>). JSON en snake_case, énumérations en texte
/// (<see cref="ThermostatService.Json"/>), le même format qu'en base.
/// </summary>
internal static class ThermostatApi
{
    public static void MapThermostatApi(this RouteGroupBuilder api)
    {
        var groupe = api.MapGroup("/thermostats");

        groupe.MapGet("/", (ThermostatService service, ThermostatEngine moteur) =>
            Json(service.List().Select(t => new ThermostatView(t, moteur.GetSnapshot(t.Id)))));

        groupe.MapGet("/{id}", (string id, ThermostatService service, ThermostatEngine moteur) =>
            service.Get(id) is { } t ? Json(new ThermostatView(t, moteur.GetSnapshot(id))) : NonTrouve(id));

        // Création ou remplacement complet. L'identifiant de l'URL fait foi.
        groupe.MapPut("/{id}", async (string id, HttpRequest requete, ThermostatService service, CancellationToken ct) =>
        {
            ThermostatSettings? corps;
            try
            {
                // L'identifiant vient de l'URL : inutile (et source d'incohérence) de le répéter dans le corps.
                if (await JsonNode.ParseAsync(requete.Body, cancellationToken: ct) is not JsonObject objet) return Refus("objet JSON attendu");
                objet["id"] = id;
                corps = objet.Deserialize<ThermostatSettings>(ThermostatService.Json);
            }
            catch (JsonException ex)
            {
                return Refus($"JSON invalide : {ex.Message}");
            }

            if (corps is null) return Refus("corps vide");
            return await Executer(async () => Json(await service.SaveAsync(corps, ct)));
        });

        groupe.MapDelete("/{id}", async (string id, ThermostatService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? Results.NoContent() : NonTrouve(id));

        groupe.MapPost("/{id}/mode", async (string id, HttpRequest requete, ThermostatService service, CancellationToken ct) =>
            await Lire<ModeRequest>(requete, ct) is { } r
                ? await Executer(async () => Resultat(id, await service.SetModeAsync(id, r.Mode, ct)))
                : Refus("attendu : {\"mode\": \"comfort\" | \"comfort1\" | \"comfort2\" | \"eco\" | \"frost_protection\" | \"off\"}"));

        groupe.MapPost("/{id}/target", async (string id, HttpRequest requete, ThermostatService service, CancellationToken ct) =>
            await Lire<TargetRequest>(requete, ct) is { } r
                ? await Executer(async () => Resultat(id, await service.SetTargetAsync(id, r.Temperature, ct)))
                : Refus("attendu : {\"temperature\": 19.5} ou {\"temperature\": null}"));

        groupe.MapPost("/{id}/schedule", async (string id, HttpRequest requete, ThermostatService service, CancellationToken ct) =>
            await Lire<ScheduleRequest>(requete, ct) is { } r
                ? await Executer(async () => Resultat(id, await service.SetScheduleActiveAsync(id, r.Active, ct)))
                : Refus("attendu : {\"active\": true}"));
    }

    private static IResult Json(object valeur) => Results.Json(valeur, ThermostatService.Json);

    private static IResult Resultat(string id, ThermostatSettings? t) => t is null ? NonTrouve(id) : Json(t);

    private static IResult NonTrouve(string id) =>
        Results.Json(new { error = $"thermostat « {id} » inconnu" }, ThermostatService.Json, statusCode: StatusCodes.Status404NotFound);

    private static IResult Refus(string message) =>
        Results.Json(new { error = message }, ThermostatService.Json, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> Executer(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ThermostatValidationException ex)
        {
            return Refus(ex.Message);
        }
    }

    private static async Task<T?> Lire<T>(HttpRequest requete, CancellationToken ct) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(requete.Body, ThermostatService.Json, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Clé d'API (en-tête <c>X-Api-Key</c>, comparée en temps constant) sur tout <c>/api</c> : sans elle, n'importe
/// quel appareil du réseau pourrait allumer les radiateurs. Sans clé configurée (<c>Api:Key</c>), l'API n'est
/// ouverte qu'en développement ; en production elle répond 503 au lieu de s'ouvrir à tous.
/// </summary>
internal sealed class ApiKeyFilter(IConfiguration configuration, IHostEnvironment environment) : IEndpointFilter
{
    public const string Header = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var attendue = configuration["Api:Key"];
        if (string.IsNullOrEmpty(attendue))
        {
            return environment.IsDevelopment()
                ? await next(context)
                : Results.Json(new { error = "clé d'API non configurée (Api:Key)" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var recue = context.HttpContext.Request.Headers[Header].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(attendue), Encoding.UTF8.GetBytes(recue))
            ? await next(context)
            : Results.Json(new { error = $"en-tête {Header} absent ou faux" }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
