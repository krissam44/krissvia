using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Overview;

/// <summary>Un interrupteur ou une lumière, avec ce qui le pilote déjà.</summary>
/// <param name="On">Allumé ou éteint d'après le dernier état publié ; null s'il n'en a jamais publié.</param>
/// <param name="DrivenBy">Le thermostat qui le pilote : on ne le bascule pas à la main, la régulation le reprendrait.</param>
/// <param name="ScheduledBy">La programmation qui le pilote : un geste à la main tient jusqu'au créneau suivant.</param>
/// <param name="Shed">Coupé par le délesteur : le rallumer peut faire disjoncter.</param>
public sealed record Actuator(
    RegisteredEntity Entity,
    string? Room,
    bool? On,
    bool Available,
    string? DrivenBy,
    string? ScheduledBy,
    bool Shed)
{
    /// <summary>Peut-on le basculer d'ici ?</summary>
    public bool Controllable => Available && DrivenBy is null;
}

public sealed record ActuatorRoom(string? Room, IReadOnlyList<Actuator> Actuators);

/// <summary>Écran Actionneurs : interrupteurs et lumières par pièce, et la commande « tout allumer / éteindre ».</summary>
public static class ActuatorOverview
{
    public static IReadOnlyList<ActuatorRoom> Build(
        IReadOnlyList<RegisteredEntity> entities,
        IReadOnlyList<RegisteredDevice> devices,
        IEntityStateReader states,
        IReadOnlyDictionary<string, string> drivenBy,
        IReadOnlyDictionary<string, string> scheduledBy,
        Func<string, bool> isShed)
    {
        var pieces = devices.ToDictionary(d => d.DeviceId, d => string.IsNullOrWhiteSpace(d.Room) ? null : d.Room.Trim(), StringComparer.Ordinal);

        return
        [
            .. entities
                .Where(e => e.Enabled && !e.Missing && e.Kind is EntityKind.Switch or EntityKind.Light)
                .Select(e =>
                {
                    var etat = states.Get(e.EntityId);
                    return new Actuator(e, pieces.GetValueOrDefault(e.DeviceId), Allume(etat), etat is { Available: true },
                        drivenBy.GetValueOrDefault(e.EntityId), scheduledBy.GetValueOrDefault(e.EntityId), isShed(e.EntityId));
                })
                .GroupBy(a => a.Room, StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key is null)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new ActuatorRoom(g.Key, [.. g.OrderBy(a => a.Entity.DisplayName, StringComparer.CurrentCultureIgnoreCase)])),
        ];
    }

    /// <summary>
    /// Ce que « tout allumer » ou « tout éteindre » doit toucher. Jamais un radiateur de thermostat — DomoMud les
    /// allumait tous —, ni un appareil injoignable ; « tout allumer » laisse aussi les appareils délestés, et
    /// aucun des deux ne renvoie un ordre à un appareil déjà dans l'état voulu.
    /// </summary>
    public static IReadOnlyList<Actuator> Bulk(IEnumerable<ActuatorRoom> rooms, bool on) =>
    [
        .. rooms.SelectMany(r => r.Actuators)
            .Where(a => a.Controllable && a.On != on && !(on && a.Shed)),
    ];

    private static bool? Allume(EntityStateSnapshot? etat)
    {
        if (etat is null) return null;
        if (etat.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return etat.Value.GetBoolean();
        return etat.Value.ValueKind == JsonValueKind.Object && etat.Value.TryGetProperty("on", out var on)
               && on.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? on.GetBoolean()
            : null;
    }
}
