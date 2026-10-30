using System.Text.Json;
using System.Text.Json.Serialization;

namespace Krissvia.Core.Overview;

/// <summary>Une pièce telle que l'API la donne (<c>GET /api/sensors</c>).</summary>
public sealed record RoomAverageView(string Room, double? Average, int Active, int Total, int Unreliable);

/// <summary>Une sonde telle que l'API la donne : l'état de santé est celui du cœur, à afficher tel quel.</summary>
public sealed record SensorView(
    string EntityId,
    string Name,
    string? Room,
    double? Temperature,
    double? Humidity,
    double? Battery,
    DateTimeOffset? MeasuredAt,
    SensorHealth Health);

/// <summary>
/// L'écran Sondes pour un client de l'API (appli mobile) : les moyennes par pièce et l'état de chaque sonde, calculés
/// ici une seule fois. Un client qui referait la règle des 60 minutes finirait par ne pas compter la même chose que le
/// web.
/// </summary>
public sealed record SensorOverviewView(IReadOnlyList<RoomAverageView> Rooms, IReadOnlyList<SensorView> Sensors, int StaleAfterMinutes)
{
    /// <summary>snake_case et énumérations en texte (<c>never_received</c>), comme le reste de l'API.</summary>
    public static JsonSerializerOptions Json { get; } = CreerOptions();

    public static SensorOverviewView From(SensorOverviewResult vue) => new(
        [.. vue.Rooms.Select(r => new RoomAverageView(r.Room, r.Average, r.Active, r.Total, r.Unreliable))],
        [
            .. vue.Sensors.Select(s => new SensorView(
                s.Entity.EntityId, s.Entity.DisplayName, s.Room, s.Temperature, s.Humidity, s.Battery, s.MeasuredAt, s.Health)),
        ],
        (int)SensorOverview.StaleAfter.TotalMinutes);

    private static JsonSerializerOptions CreerOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
