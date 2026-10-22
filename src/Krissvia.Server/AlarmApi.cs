using System.Text.Json;
using Krissvia.Climate.Alarm;

namespace Krissvia.Server;

internal sealed record AlarmCodeRequest(string? Code);

/// <summary>
/// Alarme (<c>/api/alarm</c>) : l'état, les défauts du moment, le journal, les réglages, et l'armement ou le
/// désarmement PAR CODE.
/// </summary>
/// <remarks>
/// La clé d'API ne suffit pas à désarmer : un script qui la connaît (Node-RED, une box) ne doit pas devenir la porte
/// dérobée du digicode. Le code voyage dans le corps, jamais dans l'URL (journaux, historique). Un code faux répond
/// <b>403</b> et non 401 : la clé est bonne, un 401 ferait croire à une configuration cassée. Trois codes faux bloquent
/// 60 s (429, en-tête <c>Retry-After</c>), quel que soit le clavier. Un armement refusé (porte ouverte, capteur muet)
/// répond 409 avec la liste.
/// </remarks>
internal static class AlarmApi
{
    public static void MapAlarmApi(this RouteGroupBuilder api)
    {
        var groupe = api.MapGroup("/alarm");

        groupe.MapGet("/", (AlarmService service, AlarmEngine engine) => Json(new
        {
            enabled = service.Settings.Enabled,
            state = AlarmEngine.Valeur(engine.State, engine.LastFaults),
            faults_now = engine.CurrentFaults(),
            locked_seconds = engine.LockedSeconds,
        }));

        groupe.MapPost("/arm", (HttpRequest requete, AlarmEngine engine, CancellationToken ct) => CommanderAsync(true, requete, engine, ct));
        groupe.MapPost("/disarm", (HttpRequest requete, AlarmEngine engine, CancellationToken ct) => CommanderAsync(false, requete, engine, ct));

        groupe.MapGet("/journal", (AlarmService service) => Json(service.Journal));

        // Les réglages (capteurs, délais, sirènes) ; les codes, eux, ne se donnent que dans la page Alarme.
        groupe.MapGet("/settings", (AlarmService service) => Json(service.Settings));
        groupe.MapPut("/settings", async (HttpRequest requete, AlarmService service, CancellationToken ct) =>
        {
            AlarmSettings? corps;
            try
            {
                corps = await JsonSerializer.DeserializeAsync<AlarmSettings>(requete.Body, AlarmService.Json, ct);
            }
            catch (JsonException ex)
            {
                return Erreur($"JSON invalide : {ex.Message}", StatusCodes.Status400BadRequest);
            }

            if (corps is null) return Erreur("corps vide", StatusCodes.Status400BadRequest);
            try
            {
                await service.SaveSettingsAsync(corps, ct);
                return Json(service.Settings);
            }
            catch (AlarmValidationException ex)
            {
                return Erreur(ex.Message, StatusCodes.Status400BadRequest);
            }
        });
    }

    private static async Task<IResult> CommanderAsync(bool armer, HttpRequest requete, AlarmEngine engine, CancellationToken ct)
    {
        AlarmCodeRequest? corps;
        try
        {
            corps = await JsonSerializer.DeserializeAsync<AlarmCodeRequest>(requete.Body, AlarmService.Json, ct);
        }
        catch (JsonException)
        {
            corps = null;
        }

        if (corps?.Code is not { Length: > 0 } code) return Erreur("attendu : {\"code\": \"1234\"}", StatusCodes.Status400BadRequest);

        var r = await engine.CommandWithCodeAsync(armer, code, ct);
        switch (r.Outcome)
        {
            case AlarmCodeOutcome.Done when armer && engine.State.Mode == AlarmMode.Disarmed:
                // Code bon, armement refusé : une porte ouverte, un capteur muet. Un script doit le voir sans lire l'état.
                return Results.Json(new { error = $"armement refusé : {engine.LastFaults}", user = r.User, faults = engine.LastFaults },
                    AlarmService.Json, statusCode: StatusCodes.Status409Conflict);
            case AlarmCodeOutcome.Done:
                return Json(new { user = r.User, state = AlarmEngine.Valeur(engine.State, engine.LastFaults) });
            case AlarmCodeOutcome.WrongCode:
                return Erreur("code incorrect", StatusCodes.Status403Forbidden);
            case AlarmCodeOutcome.Locked:
                requete.HttpContext.Response.Headers.RetryAfter = r.LockedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Erreur($"trop de codes faux : clavier bloqué {r.LockedSeconds} s", StatusCodes.Status429TooManyRequests);
            default:
                return Erreur("l'alarme est désactivée", StatusCodes.Status409Conflict);
        }
    }

    private static IResult Json(object valeur) => Results.Json(valeur, AlarmService.Json);

    private static IResult Erreur(string message, int statut) => Results.Json(new { error = message }, AlarmService.Json, statusCode: statut);
}
