using Krissvia.Climate.Alarm;
using Krissvia.Core.Export;
using Krissvia.Core.History;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server;

/// <summary>
/// Sondes (<c>/api/sensors</c>), détecteurs (<c>/api/detectors</c>), actionneurs (<c>/api/actuators</c>) et compteurs (<c>/api/energy</c>) : ce que montrent
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

        // Les ouvertures et les détecteurs de mouvement, pour la page Alarme de l'appli mobile.
        api.MapGet("/detectors", async (IEntityRegistry registre, IEntityStateReader etats, AlarmService alarme, CancellationToken ct) =>
        {
            var surveilles = alarme.Settings.Sensors.Select(c => c.EntityId).ToHashSet(StringComparer.Ordinal);
            var vue = DetectorOverview.Build(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct), etats, surveilles);
            return Results.Json(DetectorOverviewView.From(vue), DetectorOverviewView.Json);
        });

        api.MapGet("/actuators", async (IEntityRegistry registre, ActuatorSources sources, CancellationToken ct) =>
            Results.Json(ActuatorOverviewView.From(sources.Build(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct))),
                ActuatorOverviewView.Json));

        // ?days=1..30 (7 par défaut) : la consommation jour par jour, aujourd'hui compris, et la courbe de puissance du jour.
        api.MapGet("/energy", async (int? days, IEntityRegistry registre, IHistoryRepository historique, IEntityStateReader etats,
            ElectricityPrice prix, TimeProvider horloge, CancellationToken ct) =>
        {
            var jours = Math.Clamp(days ?? 7, 1, 30);
            var kwh = await prix.GetAsync(ct);
            var compteurs = Components.Historique.Compteur.Trouver(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct));
            var grille = Components.Historique.LectureCompteur.GrilleDuJour(horloge);
            var vues = new List<EnergyMeterView>();
            foreach (var c in compteurs)
            {
                var conso = await Components.Historique.LectureCompteur.LireAsync(c, jours, historique, etats, horloge, ct);
                var courbe = await Components.Historique.LectureCompteur.PuissanceDuJourAsync(c, grille, historique, horloge, ct);
                vues.Add(new EnergyMeterView(c.Nom, c.Index.EntityId,
                    c.Puissance is { } p ? Components.Historique.LectureCompteur.Nombre(etats, p.EntityId) : null, c.Puissance?.Unit,
                    c.Tension is { } t ? Components.Historique.LectureCompteur.Nombre(etats, t.EntityId) : null,
                    conso.DepuisMinuit, conso.DepuisMinuit * kwh,
                    [.. conso.Jours.Select(j => new EnergyDayView(j.Day, j.Kwh, j.Kwh * kwh, j.PeakPower))],
                    [.. courbe.Select(p => new EnergyPowerPointView(p.At, p.Mean, p.Min, p.Max))]));
            }
            return Results.Json(new EnergyOverviewView(kwh, vues), EnergyOverviewView.Json);
        });

        // Toute la consommation de tous les compteurs, depuis leur premier relevé, en classeur Excel (feuilles Jours et
        // Semaines) : pour ses propres statistiques. Le bouton « Exporter » de l'écran Électricité y mène.
        api.MapGet("/energy/export.xlsx", async (IEntityRegistry registre, IHistoryRepository historique, IEntityStateReader etats,
            ElectricityPrice prix, TimeProvider horloge, CancellationToken ct) =>
        {
            var kwh = await prix.GetAsync(ct);
            var compteurs = Components.Historique.Compteur.Trouver(await registre.ListAsync(ct), await registre.ListDevicesAsync(ct));
            var aExporter = new List<EnergyExportMeter>();
            foreach (var c in compteurs)
                aExporter.Add(new EnergyExportMeter(c.Nom, (await Components.Historique.LectureCompteur.LireAsync(c, null, historique, etats, horloge, ct)).Jours));

            var aujourdhui = EnergyDays.Jour(horloge.GetUtcNow(), horloge.LocalTimeZone);
            var classeur = XlsxWorkbook.Write(EnergyExport.Build(aExporter, kwh, aujourdhui));
            return Results.File(classeur, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"krissvia-consommation-{aujourdhui:yyyy-MM-dd}.xlsx");
        });
    }
}
