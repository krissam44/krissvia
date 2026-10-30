using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server;

/// <summary>
/// Sondes (<c>/api/sensors</c>) et actionneurs (<c>/api/actuators</c>) : ce que montrent les écrans du même nom, pour
/// l'appli mobile. Lecture seule — un actionneur se commande par <c>/api/entities/{id}/commands</c>, ordre vérifié.
/// </summary>
internal static class SensorApi
{
    public static void MapSensorApi(this RouteGroupBuilder api)
    {
        api.MapGet("/sensors", async (IEntityRegistry registre, IEntityStateReader etats, TimeProvider horloge, CancellationToken ct) =>
        {
            var vue = SensorOverview.Build(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct), etats, horloge.GetUtcNow());
            return Results.Json(SensorOverviewView.From(vue), SensorOverviewView.Json);
        });

        api.MapGet("/actuators", async (IEntityRegistry registre, ActuatorSources sources, CancellationToken ct) =>
            Results.Json(ActuatorOverviewView.From(sources.Build(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct))),
                ActuatorOverviewView.Json));
    }
}
