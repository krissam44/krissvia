using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Overview;

/// <summary>
/// Les moyennes par pièce de l'écran Sondes, publiées comme des sondes à part entière (intégration
/// <c>coeur.moyennes</c>) : un thermostat peut ainsi réguler sur la moyenne d'une pièce, comme avec les groupes de
/// sondes de DomoMud (<c>avg/{groupe}</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Un appareil « Moyenne {pièce} » par pièce qui a au moins une sonde ; température, et humidité si une sonde de
/// la pièce en mesure. La liste suit les pièces : une pièce qui n'a plus de sonde devient « manquante ».</item>
/// <item>La moyenne ne prend que les sondes à jour (règle de l'écran Sondes). Plus aucune : l'entité devient
/// indisponible, et le thermostat qui s'en sert coupe en sécurité comme pour une vraie sonde perdue.</item>
/// <item>L'instant publié est celui de la mesure la plus récente qui y entre : le failsafe d'un thermostat juge la
/// moyenne comme une vraie sonde. Republiée dès que la valeur ou cet instant change.</item>
/// </list>
/// </remarks>
public sealed class RoomAverageEntities(IEntityRegistry registry, IEntityStateReader states, IEntityStateWriter writer, TimeProvider clock)
{
    public const string IntegrationId = CoreIntegrations.RoomAverages;

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private string? _derniereDeclaration;
    private readonly Dictionary<string, (string Valeur, DateTimeOffset? Quand, bool Disponible)> _publie = new(StringComparer.Ordinal);

    public static string DeviceId(string piece) => "moyenne:" + EntityIdGenerator.Slugify(piece);

    public async Task UpdateAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var maintenant = clock.GetUtcNow();
            var vue = SensorOverview.Build(registry.Loaded, await registry.ListDevicesAsync(ct), states, maintenant);
            var pieces = vue.Sensors.Where(s => s.Room is not null).GroupBy(s => s.Room!, StringComparer.CurrentCultureIgnoreCase).ToList();

            // Déclaration seulement quand l'ensemble des pièces (ou leur humidité) change.
            var appareils = pieces.Select(g => new DeviceDescriptor
            {
                UniqueId = DeviceId(g.Key), Name = $"Moyenne {g.First().Room}", Model = "Moyenne des sondes de la pièce",
                Entities =
                [
                    new EntityDescriptor { UniqueId = DeviceId(g.Key) + ":temperature", Kind = EntityKind.Sensor, Name = "Température", DeviceClass = DeviceClasses.Temperature, Unit = Units.Celsius, StateClass = StateClass.Measurement },
                    .. g.Any(s => s.Humidity is not null)
                        ? [new EntityDescriptor { UniqueId = DeviceId(g.Key) + ":humidite", Kind = EntityKind.Sensor, Name = "Humidité", DeviceClass = DeviceClasses.Humidity, Unit = Units.Percent, StateClass = StateClass.Measurement }]
                        : Array.Empty<EntityDescriptor>(),
                ],
            }).ToList();
            var declaration = JsonSerializer.Serialize(appareils);
            if (declaration != _derniereDeclaration)
            {
                await registry.RegisterAsync(IntegrationId, appareils, ct);
                _derniereDeclaration = declaration;
            }

            var entites = registry.Loaded.Where(e => e.IntegrationId == IntegrationId).ToDictionary(e => e.UniqueId, e => e.EntityId, StringComparer.Ordinal);
            foreach (var g in pieces)
            {
                var fiables = g.Where(s => s.Health == SensorHealth.Ok && s.Temperature is not null).ToList();
                var quand = fiables.Count == 0 ? null : fiables.Max(s => s.MeasuredAt);
                Publier(entites.GetValueOrDefault(DeviceId(g.Key) + ":temperature"),
                    fiables.Count == 0 ? null : Math.Round(fiables.Average(s => s.Temperature!.Value), 1), quand);

                var humides = fiables.Where(s => s.Humidity is not null).ToList();
                Publier(entites.GetValueOrDefault(DeviceId(g.Key) + ":humidite"),
                    humides.Count == 0 ? null : Math.Round(humides.Average(s => s.Humidity!.Value)), quand);
            }
        }
        finally
        {
            _verrou.Release();
        }
    }

    private void Publier(string? entityId, double? valeur, DateTimeOffset? quand)
    {
        if (entityId is null) return;
        if (valeur is not { } v)
        {
            if (_publie.TryGetValue(entityId, out var avant) && !avant.Disponible) return;
            writer.SetAvailability(entityId, false);
            _publie[entityId] = ("", null, false);
            return;
        }

        var texte = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_publie.TryGetValue(entityId, out var p) && p.Disponible && p.Valeur == texte && p.Quand == quand) return;
        writer.Update(entityId, JsonSerializer.SerializeToElement(v), quand);
        _publie[entityId] = (texte, quand, true);
    }
}
