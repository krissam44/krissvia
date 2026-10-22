using System.Text.Json;
using System.Text.Json.Serialization;
using Krissvia.Core.Notifications;
using Krissvia.Core.Supervision;

namespace Krissvia.Server;

/// <summary>Supervision (<c>/api/supervision</c>) : le dernier contrôle et les alertes en cours. Lecture seule.</summary>
internal static class SupervisionApi
{
    private static readonly JsonSerializerOptions Json = CreerOptions();

    public static void MapSupervisionApi(this RouteGroupBuilder api)
    {
        api.MapGet("/supervision", (SupervisionWatch watch, AlertService alerts) => Results.Json(new
        {
            checked_at = watch.Last?.At,
            sensors_in_grace = watch.Last?.SensorsInGrace ?? false,
            items = watch.Last?.Items.Select(s => new
            {
                s.Item.Key, s.Item.Category, s.Item.Name, s.Item.Problem, s.Alerting, s.Item.Detail, s.Since,
            }),
            alerts = alerts.Active,
        }, Json));
    }

    private static JsonSerializerOptions CreerOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
