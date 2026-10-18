using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Overview;

/// <summary>Ce qu'on sait d'une sonde : à jour, silencieuse depuis trop longtemps, hors ligne, ou jamais entendue.</summary>
public enum SensorHealth { Ok, Stale, Offline, NeverReceived }

/// <summary>Une sonde de température, avec l'humidité et la batterie de son appareil quand il les publie.</summary>
public sealed record SensorReading(
    RegisteredEntity Entity,
    string? Room,
    double? Temperature,
    double? Humidity,
    double? Battery,
    DateTimeOffset? MeasuredAt,
    SensorHealth Health);

/// <summary>Moyenne d'une pièce, sur ses seules sondes à jour.</summary>
/// <param name="Unreliable">Sondes de la pièce hors ligne ou silencieuses : écartées de la moyenne.</param>
public sealed record RoomAverage(string Room, double? Average, int Active, int Total, int Unreliable);

public sealed record SensorOverviewResult(IReadOnlyList<RoomAverage> Rooms, IReadOnlyList<SensorReading> Sensors);

/// <summary>
/// Écran Sondes : les sondes de température et les moyennes par pièce, calculées d'après la pièce de chaque appareil.
/// </summary>
/// <remarks>
/// DomoMud demandait de créer des groupes à la main ; la pièce, déjà renseignée dans l'écran Entités, suffit. Une
/// sonde silencieuse depuis plus de <see cref="StaleAfter"/> (règle de DomoMud) ou hors ligne n'entre pas dans la
/// moyenne : sa dernière valeur ferait croire à une mesure (principe « une absence de donnée n'est pas zéro »).
/// </remarks>
public static class SensorOverview
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(60);

    public static SensorOverviewResult Build(
        IReadOnlyList<RegisteredEntity> entities, IReadOnlyList<RegisteredDevice> devices, IEntityStateReader states, DateTimeOffset now)
    {
        var pieces = devices.ToDictionary(d => d.DeviceId, d => string.IsNullOrWhiteSpace(d.Room) ? null : d.Room.Trim(), StringComparer.Ordinal);
        var actives = entities.Where(e => e.Enabled && !e.Missing).ToList();

        double? Voisine(RegisteredEntity sonde, string classe) =>
            actives.FirstOrDefault(v => v.DeviceId == sonde.DeviceId && v.Kind == EntityKind.Sensor && v.DeviceClass == classe) is { } e
            && states.Get(e.EntityId) is { } etat && etat.TryGetNumber(out var valeur) ? valeur : null;

        var sondes = actives
            .Where(e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Temperature)
            .Select(e =>
            {
                var etat = states.Get(e.EntityId);
                var sante = etat is null ? SensorHealth.NeverReceived
                    : !etat.Available ? SensorHealth.Offline
                    : now - etat.MeasuredAt > StaleAfter ? SensorHealth.Stale
                    : SensorHealth.Ok;
                double? temperature = etat is not null && etat.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? etat.Value.GetDouble() : null;
                return new SensorReading(e, pieces.GetValueOrDefault(e.DeviceId), temperature,
                    Voisine(e, DeviceClasses.Humidity), Voisine(e, DeviceClasses.Battery), etat?.MeasuredAt, sante);
            })
            .OrderBy(s => s.Room is null)
            .ThenBy(s => s.Room, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Entity.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var moyennes = sondes
            .Where(s => s.Room is not null)
            .GroupBy(s => s.Room!, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var fiables = g.Where(s => s.Health == SensorHealth.Ok && s.Temperature is not null).ToList();
                return new RoomAverage(g.First().Room!,
                    fiables.Count == 0 ? null : Math.Round(fiables.Average(s => s.Temperature!.Value), 1),
                    fiables.Count, g.Count(), g.Count(s => s.Health is SensorHealth.Stale or SensorHealth.Offline));
            })
            .OrderBy(r => r.Room, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new SensorOverviewResult(moyennes, sondes);
    }
}
