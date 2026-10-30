using System.Text.Json;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Overview;

/// <summary>Un interrupteur ou une lumière tel que l'API le donne (<c>GET /api/actuators</c>).</summary>
/// <param name="On">Null : jamais publié — pas « éteint ».</param>
/// <param name="Usage">Celui de l'écran Entités (<c>eclairage</c>, <c>prise</c>, <c>radiateur</c>, <c>compteur</c>), pour l'icône.</param>
/// <param name="Controllable">Basculable d'ici : joignable, et pas le radiateur d'un thermostat.</param>
public sealed record ActuatorView(
    string EntityId,
    string Name,
    string? Usage,
    bool? On,
    bool Available,
    string? DrivenBy,
    string? ScheduledBy,
    bool Shed,
    bool Controllable);

public sealed record ActuatorRoomView(string? Room, IReadOnlyList<ActuatorView> Actuators);

/// <summary>
/// L'écran Actionneurs pour un client de l'API (appli mobile) : par pièce, avec ce qui pilote déjà chaque appareil
/// (thermostat, programmation, délesteur). Le client n'a pas à savoir quel thermostat tient quel radiateur.
/// </summary>
public sealed record ActuatorOverviewView(IReadOnlyList<ActuatorRoomView> Rooms)
{
    public static JsonSerializerOptions Json => SensorOverviewView.Json;

    public static ActuatorOverviewView From(IReadOnlyList<ActuatorRoom> pieces) => new(
    [
        .. pieces.Select(p => new ActuatorRoomView(p.Room,
        [
            .. p.Actuators.Select(a => new ActuatorView(
                a.Entity.EntityId, a.Entity.DisplayName, EntityUsages.Of(a.Entity), a.On, a.Available,
                a.DrivenBy, a.ScheduledBy, a.Shed, a.Controllable)),
        ])),
    ]);
}
