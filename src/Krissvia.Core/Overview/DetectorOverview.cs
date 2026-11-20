using System.Text.Json;
using System.Text.Json.Serialization;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Overview;

/// <summary>Ce qu'on sait d'un détecteur : son état se lit, son plugin le dit indisponible, ou il n'a jamais rien publié.</summary>
public enum DetectorHealth { Ok, Offline, NeverReceived }

/// <summary>
/// Un capteur d'ouverture ou un détecteur de mouvement (radar, infrarouge).
/// </summary>
/// <param name="Active">Ouvert, ou mouvement détecté ; null quand l'état ne se lit pas (jamais reçu, indisponible).</param>
/// <param name="Since">Depuis quand l'état est celui-ci.</param>
/// <param name="Monitored">Surveillé par l'alarme (écran Alarme).</param>
public sealed record DetectorReading(
    RegisteredEntity Entity, string? Room, bool IsMotion, bool? Active, DateTimeOffset? Since, DetectorHealth Health, double? Battery, bool Monitored);

/// <summary>
/// Les ouvertures et les détecteurs de la maison, pour l'appli mobile : ce qui est ouvert, où ça bouge, et depuis quand.
/// </summary>
/// <remarks>
/// Pas de règle « muet depuis trop longtemps » : un contact ne publie qu'au changement d'état, douze heures de silence y
/// sont normales. Le silence d'un capteur surveillé est l'affaire de la supervision et du contrôle à l'armement.
/// </remarks>
public static class DetectorOverview
{
    public static IReadOnlyList<DetectorReading> Build(
        IReadOnlyList<RegisteredEntity> entities, IReadOnlyList<RegisteredDevice> devices, IEntityStateReader states,
        IReadOnlySet<string> monitored)
    {
        var pieces = devices.ToDictionary(d => d.DeviceId, d => string.IsNullOrWhiteSpace(d.Room) ? null : d.Room.Trim(), StringComparer.Ordinal);
        var actives = entities.Where(e => e.Enabled && !e.Missing).ToList();

        double? Batterie(RegisteredEntity capteur) =>
            actives.FirstOrDefault(v => v.DeviceId == capteur.DeviceId && v.Kind == EntityKind.Sensor && v.DeviceClass == DeviceClasses.Battery) is { } e
            && states.Get(e.EntityId) is { } etat && etat.TryGetNumber(out var valeur) ? valeur : null;

        return
        [
            .. actives
                .Where(e => e.Kind == EntityKind.BinarySensor && e.DeviceClass is DeviceClasses.Opening or DeviceClasses.Occupancy)
                .Select(e =>
                {
                    var etat = states.Get(e.EntityId);
                    var sante = etat is null ? DetectorHealth.NeverReceived : !etat.Available ? DetectorHealth.Offline : DetectorHealth.Ok;
                    bool? actif = sante == DetectorHealth.Ok && etat!.TryGetBool(out var b) ? b : null;
                    return new DetectorReading(e, pieces.GetValueOrDefault(e.DeviceId), e.DeviceClass == DeviceClasses.Occupancy, actif,
                        etat?.ChangedSince, sante, Batterie(e), monitored.Contains(e.EntityId));
                })
                .OrderBy(d => d.Room is null)
                .ThenBy(d => d.Room, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.IsMotion)
                .ThenBy(d => d.Entity.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}

/// <summary>Un détecteur tel que l'API le donne (<c>GET /api/detectors</c>).</summary>
/// <param name="Type"><c>opening</c> ou <c>motion</c>.</param>
public sealed record DetectorView(
    string EntityId, string Name, string? Room, string Type, bool? Active, DateTimeOffset? Since, DetectorHealth Health, double? Battery, bool Monitored);

/// <summary>Les détecteurs pour un client de l'API (appli mobile), dans l'ordre d'affichage : pièce, ouvertures, puis mouvements.</summary>
public sealed record DetectorOverviewView(IReadOnlyList<DetectorView> Detectors)
{
    public static JsonSerializerOptions Json { get; } = CreerOptions();

    public static DetectorOverviewView From(IReadOnlyList<DetectorReading> lectures) => new(
    [
        .. lectures.Select(d => new DetectorView(d.Entity.EntityId, d.Entity.DisplayName, d.Room, d.IsMotion ? "motion" : "opening",
            d.Active, d.Since, d.Health, d.Battery, d.Monitored)),
    ]);

    private static JsonSerializerOptions CreerOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
