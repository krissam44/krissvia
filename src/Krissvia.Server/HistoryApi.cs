using Krissvia.Core.History;
using Krissvia.Core.Registry;

namespace Krissvia.Server;

/// <summary>
/// <c>GET /api/entities/{id}/history?from=…&amp;to=…&amp;field=…</c> : la courbe d'une entité (WIDGETS.md §5, <c>kv.history</c>).
/// </summary>
/// <remarks>
/// Jusqu'à <see cref="MaxBrut"/>, les points bruts (au plus un par minute) ; au-delà, les heures consolidées (moyenne
/// pondérée par le temps, min, max) — une semaine en points bruts, c'est 10 000 points pour un écran de 400 pixels.
/// Le premier point brut peut précéder <c>from</c> : c'est la valeur en vigueur au début de la fenêtre.
/// </remarks>
internal static class HistoryApi
{
    public static readonly TimeSpan MaxBrut = TimeSpan.FromDays(3);
    public static readonly TimeSpan MaxFenetre = TimeSpan.FromDays(400);

    public static void MapHistoryApi(this RouteGroupBuilder api)
    {
        api.MapGet("/entities/{id}/history", async (string id, DateTimeOffset? from, DateTimeOffset? to, string? field, string? resolution,
            IEntityRegistry registre, IHistoryRepository historique, TimeProvider horloge, CancellationToken ct) =>
        {
            if (await registre.GetAsync(id, ct) is null)
                return Results.Json(new { error = $"entité « {id} » inconnue" }, statusCode: StatusCodes.Status404NotFound);

            var fin = to ?? horloge.GetUtcNow();
            var debut = from ?? fin.AddDays(-1);
            if (debut >= fin) return Erreur("from doit précéder to");
            if (fin - debut > MaxFenetre) return Erreur($"fenêtre de {MaxFenetre.TotalDays:0} jours au plus");

            var series = await historique.SeriesAsync(id, ct);
            var serie = HistoryValues.Serie(id, field is { Length: > 0 } f ? f : null);
            if (!series.Contains(serie))
            {
                // Un thermostat n'a pas de série « nue » : dire lesquelles il a plutôt que rendre une courbe vide.
                var champs = series.Where(s => s != id).Select(s => s[(id.Length + 1)..]).ToList();
                if (field is null && champs.Count > 0) return Erreur($"préciser field : {string.Join(", ", champs)}");
                if (field is not null) return Erreur(champs.Count > 0 ? $"champ inconnu ; disponibles : {string.Join(", ", champs)}" : "pas de champ pour cette entité");
            }

            var horaire = resolution switch
            {
                "hour" => true,
                "raw" => false,
                _ => fin - debut > MaxBrut,
            };
            if (!horaire && fin - debut > MaxBrut) return Erreur($"résolution brute : {MaxBrut.TotalDays:0} jours au plus");

            object points = horaire
                ? (await historique.ReadHourlyAsync(serie, debut, fin, ct))
                    .Select(b => new { t = b.Hour, v = b.Average, min = b.Min, max = b.Max, last = b.Last, coverage = b.Coverage })
                : (await historique.ReadAsync(serie, debut, fin, ct)).Select(p => new { t = p.At, v = p.Value });
            return Results.Json(new
            {
                entity_id = id,
                field,
                resolution = horaire ? "hour" : "raw",
                from = debut,
                to = fin,
                points,
            });
        });
    }

    private static IResult Erreur(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
}
