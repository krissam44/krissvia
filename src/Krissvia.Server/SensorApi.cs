using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server;

/// <summary>Sondes (<c>/api/sensors</c>) : ce que montre l'écran Sondes, moyennes par pièce comprises. Lecture seule.</summary>
internal static class SensorApi
{
    public static void MapSensorApi(this RouteGroupBuilder api)
    {
        api.MapGet("/sensors", async (IEntityRegistry registre, IEntityStateReader etats, TimeProvider horloge, CancellationToken ct) =>
        {
            var vue = SensorOverview.Build(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct), etats, horloge.GetUtcNow());
            return Results.Json(SensorOverviewView.From(vue), SensorOverviewView.Json);
        });
    }
}
