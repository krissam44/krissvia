using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Presence;
using Krissvia.Climate.Thermostats;

namespace Krissvia.Server;

/// <summary>Un thermostat tel que l'API le montre : sa configuration et son état du moment.</summary>
internal sealed record ThermostatView(ThermostatSettings Settings, ThermostatSnapshot? State);

internal sealed record ModeRequest(ThermostatMode Mode);
internal sealed record TargetRequest(double? Temperature);
internal sealed record ScheduleRequest(bool Active);
internal sealed record AbsencePeriodRequest(DateTimeOffset Departure, DateTimeOffset Return, bool CancelOnPresence = true);
internal sealed record AbsenceForceRequest(bool? Away);

/// <summary>
/// API publique : thermostats (<c>/api/thermostats</c>) et délestage. JSON en snake_case, énumérations en texte
/// (<see cref="ThermostatService.Json"/>), le même format qu'en base.
/// </summary>
internal static class PublicApi
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

        // Le planning seul : un écran ou un script qui le modifie n'écrase pas un mode choisi entre-temps.
        groupe.MapPut("/{id}/schedule", async (string id, HttpRequest requete, ThermostatService service, CancellationToken ct) =>
            await Lire<WeeklySchedule>(requete, ct) is { } planning
                ? await Executer(async () => Resultat(id, await service.SetScheduleAsync(id, planning, ct)))
                : Refus("attendu : {\"days\": {\"monday\": [{\"start\": \"06:00:00\", \"end\": \"08:00:00\", \"mode\": \"comfort\"}]}, \"default_mode\": \"eco\"}"));

        groupe.MapPost("/{id}/schedule", async (string id, HttpRequest requete, ThermostatService service, CancellationToken ct) =>
            await Lire<ScheduleRequest>(requete, ct) is { } r
                ? await Executer(async () => Resultat(id, await service.SetScheduleActiveAsync(id, r.Active, ct)))
                : Refus("attendu : {\"active\": true}"));
    }

    /// <summary>
    /// Délestage (<c>/api/load-shedding</c>) : la configuration, un seul document, et l'état du moment.
    /// </summary>
    public static void MapLoadSheddingApi(this RouteGroupBuilder api)
    {
        api.MapGet("/load-shedding", (LoadSheddingService service) =>
            Json(new { configuration = service.Configuration, status = service.Status, load_error = service.LoadError }));

        api.MapPut("/load-shedding", async (HttpRequest requete, LoadSheddingService service, CancellationToken ct) =>
        {
            LoadSheddingConfiguration? corps;
            try
            {
                corps = await JsonSerializer.DeserializeAsync<LoadSheddingConfiguration>(requete.Body, ThermostatService.Json, ct);
            }
            catch (JsonException ex)
            {
                return Refus($"JSON invalide : {ex.Message}");
            }

            if (corps is null) return Refus("corps vide");
            try
            {
                return Json(await service.SaveAsync(corps, ct));
            }
            catch (LoadSheddingValidationException ex)
            {
                return Refus(ex.Message);
            }
        });
    }

    /// <summary>
    /// Absence (<c>/api/absence</c>) : l'état qui s'applique aux thermostats, la période programmée, le forçage et la
    /// présence détectée.
    /// </summary>
    public static void MapAbsenceApi(this RouteGroupBuilder api)
    {
        var groupe = api.MapGroup("/absence");

        groupe.MapGet("/", (AbsenceService absence, HomePresence presence) =>
            Json(new { state = absence.Current, period = absence.Period, forced = absence.Forced, presence = presence.Current }));

        groupe.MapPut("/period", async (HttpRequest requete, AbsenceService absence, CancellationToken ct) =>
        {
            if (await Lire<AbsencePeriodRequest>(requete, ct) is not { } r)
                return Refus("attendu : {\"departure\": \"2026-12-20T08:00:00+01:00\", \"return\": \"2026-12-27T18:00:00+01:00\", \"cancel_on_presence\": true}");
            try
            {
                await absence.ScheduleAsync(r.Departure, r.Return, r.CancelOnPresence, ct);
                return Json(absence.Period!);
            }
            catch (AbsenceValidationException ex)
            {
                return Refus(ex.Message);
            }
        });

        groupe.MapDelete("/period", async (AbsenceService absence, CancellationToken ct) =>
        {
            await absence.ClearAsync(ct);
            return Results.NoContent();
        });

        groupe.MapPut("/forced", async (HttpRequest requete, AbsenceService absence, CancellationToken ct) =>
        {
            if (await Lire<AbsenceForceRequest>(requete, ct) is not { } r) return Refus("attendu : {\"away\": true | false | null}");
            await absence.ForceAsync(r.Away, ct);
            return Json(absence.Current);
        });
    }

    /// <summary>Programmations des équipements (<c>/api/schedules</c>) : configuration et état du moment.</summary>
    public static void MapDeviceScheduleApi(this RouteGroupBuilder api)
    {
        var groupe = api.MapGroup("/schedules");

        groupe.MapGet("/", (DeviceScheduleService service, DeviceScheduleEngine moteur) =>
            Json(service.List().Select(p => new { settings = p, state = moteur.GetSnapshot(p.Id) })));

        groupe.MapPut("/{id}", async (string id, HttpRequest requete, DeviceScheduleService service, CancellationToken ct) =>
        {
            DeviceSchedule? corps;
            try
            {
                if (await JsonNode.ParseAsync(requete.Body, cancellationToken: ct) is not JsonObject objet) return Refus("objet JSON attendu");
                objet["id"] = id;
                corps = objet.Deserialize<DeviceSchedule>(ThermostatService.Json);
            }
            catch (JsonException ex)
            {
                return Refus($"JSON invalide : {ex.Message}");
            }

            if (corps is null) return Refus("corps vide");
            try
            {
                return Json(await service.SaveAsync(corps, ct));
            }
            catch (DeviceScheduleValidationException ex)
            {
                return Refus(ex.Message);
            }
        });

        groupe.MapDelete("/{id}", async (string id, DeviceScheduleService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct)
                ? Results.NoContent()
                : Results.Json(new { error = $"programmation « {id} » inconnue" }, ThermostatService.Json, statusCode: StatusCodes.Status404NotFound));
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
/// quel appareil du réseau pourrait allumer les radiateurs. Une session ouverte dans l'interface d'administration
/// vaut la clé. Sans clé configurée (<c>Api:Key</c>), l'API n'est
/// ouverte qu'en développement ; en production elle répond 503 au lieu de s'ouvrir à tous.
/// </summary>
internal sealed class ApiKeyFilter(IConfiguration configuration, IHostEnvironment environment) : IEndpointFilter
{
    public const string Header = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Session de l'interface d'administration : le cookie suffit (SameSite=Lax écarte les envois d'un autre site).
        if (context.HttpContext.User.Identity?.IsAuthenticated == true) return await next(context);

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
