using Krissvia.Climate.Alarm;
using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.Registry;

namespace Krissvia.Climate;

/// <summary>
/// Les entités que visent les modules du cœur : thermostats, programmations, délestage, alarme. Renommer un appareil
/// orphelinait en silence tout ce qui le visait dans DomoMud (le radiateur « Chbre RdC » du 03/10/2026) ; la
/// supervision s'en sert pour le dire.
/// </summary>
public sealed class ClimateEntityReferences(
    ThermostatService thermostats, DeviceScheduleService schedules, LoadSheddingService loadShedding, AlarmService alarm) : IEntityReferenceSource
{
    public Task<IReadOnlyList<EntityReference>> GetReferencesAsync(CancellationToken ct = default)
    {
        var r = new List<EntityReference>();

        foreach (var t in thermostats.List())
        {
            var nom = $"Thermostat {(t.Name is { Length: > 0 } n ? n : t.Id)}";
            void Ajouter(string role, string? entite)
            {
                if (entite is { Length: > 0 }) r.Add(new EntityReference("thermostat", t.Id, role, entite, nom));
            }

            Ajouter("sonde", t.TemperatureEntityId);
            foreach (var s in t.SwitchEntityIds) Ajouter("radiateur", s);
            Ajouter("sonde extérieure", t.OutdoorTemperatureEntityId);
            Ajouter("prévision", t.ForecastEntityId);
            Ajouter("ouverture", t.OpeningEntityId);
        }

        foreach (var p in schedules.List().Where(p => p.Enabled))
            foreach (var s in p.SwitchEntityIds)
                r.Add(new EntityReference("programmation", p.Id, "équipement", s, $"Programmation {(p.Name is { Length: > 0 } n ? n : p.Id)}"));

        var delestage = loadShedding.Configuration;
        if (delestage.Enabled)
        {
            foreach (var source in delestage.Sources)
                if (source is { Kind: PowerSourceKind.Entity, PowerEntityId: { Length: > 0 } e })
                    r.Add(new EntityReference("delestage", "mesure", "puissance", e, "Délestage"));
            foreach (var d in delestage.Devices)
                foreach (var s in d.SwitchEntityIds)
                    r.Add(new EntityReference("delestage", d.Id, "à couper", s, $"Délestage {(d.Name is { Length: > 0 } n ? n : d.Id)}"));
        }

        if (alarm.Settings is { Enabled: true } alarme)
        {
            foreach (var c in alarme.Sensors)
                r.Add(new EntityReference("alarme", "maison", "capteur", c.EntityId, "Alarme"));
            foreach (var sirene in alarme.Sirens)
                r.Add(new EntityReference("alarme", "maison", "sirène", sirene, "Alarme"));
        }

        return Task.FromResult<IReadOnlyList<EntityReference>>(r);
    }
}
