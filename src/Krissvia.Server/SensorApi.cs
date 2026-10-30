using Krissvia.Core.History;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server;

/// <summary>
/// Sondes (<c>/api/sensors</c>), actionneurs (<c>/api/actuators</c>) et compteurs (<c>/api/energy</c>) : ce que montrent
/// les écrans Sondes, Actionneurs et Électricité, pour l'appli mobile. Lecture seule — un actionneur se commande par <c>/api/entities/{id}/commands</c>, ordre vérifié.
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

        // ?days=1..30 (7 par défaut) : la consommation jour par jour, aujourd'hui compris.
        api.MapGet("/energy", async (int? days, IEntityRegistry registre, IHistoryRepository historique, IEntityStateReader etats,
            ElectricityPrice prix, TimeProvider horloge, CancellationToken ct) =>
        {
            var jours = Math.Clamp(days ?? 7, 1, 30);
            var kwh = await prix.GetAsync(ct);
            var compteurs = Components.Historique.Compteur.Trouver(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct));
            var vues = new List<EnergyMeterView>();
            foreach (var c in compteurs)
            {
                var conso = await Components.Historique.LectureCompteur.LireAsync(c, jours, historique, etats, horloge, ct);
                vues.Add(new EnergyMeterView(c.Nom, c.Index.EntityId,
                    c.Puissance is { } p ? Components.Historique.LectureCompteur.Nombre(etats, p.EntityId) : null, c.Puissance?.Unit,
                    c.Tension is { } t ? Components.Historique.LectureCompteur.Nombre(etats, t.EntityId) : null,
                    conso.DepuisMinuit, conso.DepuisMinuit * kwh,
                    [.. conso.Jours.Select(j => new EnergyDayView(j.Day, j.Kwh, j.Kwh * kwh, j.PeakPower))]));
            }
            return Results.Json(new EnergyOverviewView(kwh, vues), EnergyOverviewView.Json);
        });
    }
}
