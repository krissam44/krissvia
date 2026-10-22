using Krissvia.Core.Registry;

namespace Krissvia.PluginHost.Services;

/// <summary>
/// Les entités choisies dans les réglages des plugins ACTIVÉS (contrat 0.3) : une platine dont le thermostat a été
/// renommé n'affiche plus qu'un tiret, sans rien dire. Un plugin désactivé exprès ne compte pas : il ne lit rien.
/// </summary>
public sealed class PluginSettingsReferences(PluginSupervisor supervisor, PluginSettingsService settings) : IEntityReferenceSource
{
    public const string OwnerKind = "plugin";

    public Task<IReadOnlyList<EntityReference>> GetReferencesAsync(CancellationToken ct = default)
    {
        var references = new List<EntityReference>();
        foreach (var service in supervisor.Snapshots().Where(s => s.Enabled))
        {
            if (!settings.HasSaved(service.Id) || supervisor.GetManifest(service.Id) is not { } fiche) continue;
            if (SettingsSchema.Parse(fiche.Settings) is not { } schema) continue;
            foreach (var entite in SettingsSchema.EntityReferences(schema, settings.Get(fiche)).Keys)
                references.Add(new EntityReference(OwnerKind, service.Id, "réglage", entite, $"Plugin {service.Name}"));
        }

        return Task.FromResult<IReadOnlyList<EntityReference>>(references);
    }
}
