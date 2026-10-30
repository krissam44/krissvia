using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server;

/// <summary>
/// Ce qui pilote déjà chaque interrupteur — thermostat, programmation, délesteur —, réuni pour l'écran Actionneurs et
/// pour <c>GET /api/actuators</c> : un seul endroit, pour que le web et l'appli mobile ne divergent pas.
/// </summary>
internal sealed class ActuatorSources(ThermostatService thermostats, DeviceScheduleService programmations, LoadShedder delesteur, IEntityStateReader etats)
{
    public IReadOnlyList<ActuatorRoom> Build(IReadOnlyList<RegisteredEntity> entites, IReadOnlyList<RegisteredDevice> appareils)
    {
        var pilotes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in thermostats.List())
            foreach (var radiateur in t.SwitchEntityIds) pilotes.TryAdd(radiateur, t.Name is { Length: > 0 } n ? n : t.Id);

        var programmes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in programmations.List().Where(p => p.Enabled))
            foreach (var prise in p.SwitchEntityIds) programmes.TryAdd(prise, p.Name);

        return ActuatorOverview.Build(entites, appareils, etats, pilotes, programmes, delesteur.IsSwitchShed);
    }
}
