using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krissvia.Climate;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Live;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server;

/// <summary>État d'une entité tel que l'API le montre. Absent (null) : jamais reçu, ce qui n'est pas zéro.</summary>
/// <param name="ChangedAt">Depuis quand la valeur est celle-ci (« allumé depuis 2 h ») ; <c>measured_at</c> date le dernier message.</param>
internal sealed record EntityStateView(JsonElement Value, bool Available, DateTimeOffset MeasuredAt, DateTimeOffset ChangedAt)
{
    public static EntityStateView? From(EntityStateSnapshot? etat) =>
        etat is null ? null : new(etat.Value, etat.Available, etat.MeasuredAt, etat.ChangedSince);
}

/// <summary>Une entité telle que l'API, le flux en direct et les widgets la voient (WIDGETS.md §4).</summary>
internal sealed record EntityView(
    string EntityId,
    string Name,
    EntityKind Kind,
    string? DeviceClass,
    string? Unit,
    IReadOnlyList<string> Commands,
    string? Room,
    string? Usage,
    bool Enabled,
    bool Missing,
    string IntegrationId,
    JsonElement? Extra,
    // Écrit même à null : « jamais reçu » doit se lire, pas se deviner à une absence (WIDGETS.md §4).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] EntityStateView? State);

/// <summary>Ordre envoyé à une entité. <see cref="Source"/> dit qui le donne, pour le journal (« widget:jauge-cuve »).</summary>
internal sealed record EntityCommandRequest(string? Command, JsonElement? Args, string? Source);

/// <summary>
/// Entités (<c>/api/entities</c>) et flux en direct (<c>/api/live</c>) : ce dont un tableau de bord ou un widget a
/// besoin, et rien de plus — ni les services du serveur, ni la configuration des modules.
/// </summary>
internal static partial class EntityApi
{
    public static void MapEntityApi(this RouteGroupBuilder api)
    {
        api.MapGet("/entities", async (IEntityRegistry registre, IEntityStateReader etats, CancellationToken ct) =>
            Json(await VuesAsync(registre, etats, null, ct)));

        api.MapGet("/entities/{id}", async (string id, IEntityRegistry registre, IEntityStateReader etats, CancellationToken ct) =>
            (await VuesAsync(registre, etats, [id], ct)).FirstOrDefault() is { } vue ? Json(vue) : Inconnue(id));

        // Seulement une entité disparue, et que plus rien ne vise : 409 avec la liste de ce qui s'en sert, sinon 400.
        api.MapDelete("/entities/{id}", async (string id, EntityRemoval suppression, CancellationToken ct) =>
        {
            try
            {
                var r = await suppression.DeleteAsync(id, ct);
                return r.Deleted ? Results.NoContent()
                    : r.UsedBy.Count > 0 ? Results.Json(new { error = "entité encore utilisée", used_by = r.UsedBy }, ThermostatService.Json, statusCode: StatusCodes.Status409Conflict)
                    : Inconnue(id);
            }
            catch (InvalidOperationException ex)
            {
                return Refus(ex.Message);
            }
        });

        // L'ordre est VÉRIFIÉ (ARCHITECTURE.md §7.3) : « accepted » dit que le plugin l'a pris, « confirmed » que l'état
        // a suivi. Un widget attend l'état suivant au lieu d'afficher « allumé » parce qu'il a cliqué.
        api.MapPost("/entities/{id}/commands", async (string id, HttpRequest requete, IEntityRegistry registre, ICommandBus bus, CancellationToken ct) =>
        {
            EntityCommandRequest? corps;
            try
            {
                corps = await JsonSerializer.DeserializeAsync<EntityCommandRequest>(requete.Body, ThermostatService.Json, ct);
            }
            catch (JsonException ex)
            {
                return Refus($"JSON invalide : {ex.Message}");
            }

            if (corps?.Command is not { Length: > 0 } commande) return Refus("attendu : {\"command\": \"turn_on\", \"args\": {…}, \"source\": \"widget:…\"}");
            if (await registre.GetAsync(id, ct) is not { } entite) return Inconnue(id);
            if (!entite.Commands.Contains(commande, StringComparer.Ordinal))
                return Refus(entite.Commands.Count == 0
                    ? $"« {id} » n'accepte aucune commande"
                    : $"« {id} » n'accepte pas « {commande} » (commandes : {string.Join(", ", entite.Commands)})");

            var ordre = new EntityCommand(id, commande)
            {
                Args = corps.Args is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } a ? a : null,
                Origin = corps.Source is { } s && SourceValide().IsMatch(s) ? $"api:{s}" : "api",
            };
            var resultat = await bus.SendVerifiedAsync(ordre, ct: ct);
            return Json(new { outcome = resultat.Outcome, verification = resultat.Verification, detail = resultat.Detail });
        });

        api.Map("/live", LiveAsync);
    }

    /// <summary>
    /// Flux en direct, par WebSocket. À la connexion : <c>{"type":"entities","entities":[…]}</c> ; ensuite
    /// <c>{"type":"states","states":{"id": état | null}}</c> à chaque changement, et de nouveau <c>entities</c> quand
    /// le registre change. <c>?entities=a,b</c> restreint aux entités nommées. Le client n'envoie rien.
    /// </summary>
    private static async Task LiveAsync(HttpContext contexte, LiveFeed flux, IEntityRegistry registre, IEntityStateReader etats, IHostApplicationLifetime vie)
    {
        if (!contexte.WebSockets.IsWebSocketRequest)
        {
            contexte.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Le cookie de session suffit à ouvrir le flux : une page d'un autre site ne doit pas pouvoir le faire à notre
        // place (détournement de WebSocket). Un client hors navigateur n'envoie pas d'Origin.
        if (contexte.Request.Headers.Origin.ToString() is { Length: > 0 } origine
            && !(Uri.TryCreate(origine, UriKind.Absolute, out var uri)
                 && string.Equals(uri.Authority, contexte.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            contexte.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var filtre = contexte.Request.Query["entities"].ToString() is { Length: > 0 } liste
            ? liste.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        var ct = contexte.RequestAborted;
        using var abonnement = flux.Subscribe(filtre);   // AVANT la photo : rien ne se perd entre les deux
        using var socket = await contexte.WebSockets.AcceptWebSocketAsync();
        // L'arrêt du serveur ferme aussi le flux : sinon le serveur web attendrait que le client parte de lui-même.
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct, vie.ApplicationStopping);
        var lecture = AttendreFermetureAsync(socket, fin);

        try
        {
            await EnvoyerAsync(socket, new { type = "entities", entities = await VuesAsync(registre, etats, filtre, fin.Token) }, fin.Token);
            while (!fin.IsCancellationRequested)
            {
                var lot = await abonnement.ReadAsync(fin.Token);
                if (lot.RegistryChanged)
                    await EnvoyerAsync(socket, new { type = "entities", entities = await VuesAsync(registre, etats, filtre, fin.Token) }, fin.Token);
                if (lot.ChangedEntities.Count > 0)
                    await EnvoyerAsync(socket, new
                    {
                        type = "states",
                        states = lot.ChangedEntities.ToDictionary(id => id, id => EntityStateView.From(etats.Get(id))),
                    }, fin.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Client parti.
        }

        if (socket.State == WebSocketState.Open)
        {
            // « Le serveur s'en va » (1001) plutôt qu'une fin normale : le tableau de bord sait qu'il doit se reconnecter.
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseAsync(vie.ApplicationStopping.IsCancellationRequested ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure,
                    vie.ApplicationStopping.IsCancellationRequested ? "arrêt du serveur" : null, delai.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // Client injoignable : il n'y a plus personne à prévenir.
            }
        }

        await lecture;
    }

    private static async Task<IReadOnlyList<EntityView>> VuesAsync(
        IEntityRegistry registre, IEntityStateReader etats, IReadOnlyCollection<string>? filtre, CancellationToken ct)
    {
        var pieces = (await registre.ListDevicesAsync(ct)).ToDictionary(d => d.DeviceId, d => d.Room, StringComparer.Ordinal);
        return
        [
            .. (await registre.ListAsync(ct))
                .Where(e => filtre is null || filtre.Contains(e.EntityId))
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .Select(e => new EntityView(
                    e.EntityId, e.DisplayName, e.Kind, e.DeviceClass, e.Unit, e.Commands,
                    pieces.GetValueOrDefault(e.DeviceId), e.Usage, e.Enabled, e.Missing, e.IntegrationId,
                    e.ExtraJson is { Length: > 0 } extra ? JsonSerializer.Deserialize<JsonElement>(extra) : null,
                    EntityStateView.From(etats.Get(e.EntityId)))),
        ];
    }

    // Le client n'envoie rien : on lit seulement pour voir la fermeture, qui arrête l'envoi.
    private static async Task AttendreFermetureAsync(WebSocket socket, CancellationTokenSource fin)
    {
        var tampon = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                if ((await socket.ReceiveAsync(tampon, fin.Token)).MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            await fin.CancelAsync();
        }
    }

    private static Task EnvoyerAsync(WebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ThermostatService.Json), WebSocketMessageType.Text, true, ct);

    private static IResult Json(object valeur) => Results.Json(valeur, ThermostatService.Json);

    private static IResult Inconnue(string id) =>
        Results.Json(new { error = $"entité « {id} » inconnue" }, ThermostatService.Json, statusCode: StatusCodes.Status404NotFound);

    private static IResult Refus(string message) =>
        Results.Json(new { error = message }, ThermostatService.Json, statusCode: StatusCodes.Status400BadRequest);

    [GeneratedRegex("^[a-z0-9][a-z0-9:_.-]{0,63}$")]
    private static partial Regex SourceValide();
}
