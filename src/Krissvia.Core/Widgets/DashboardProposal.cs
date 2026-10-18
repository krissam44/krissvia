using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Widgets;

/// <summary>
/// Propose un tableau de bord à partir des entités : une section par famille (thermostats, températures,
/// interrupteurs, capteurs, énergie), chaque entité avec le widget qui lui va le mieux.
/// </summary>
/// <remarks>
/// Repris de la génération automatique de DomoMud, avec deux différences : c'est une <em>proposition</em>, que
/// l'utilisateur relit dans le mode édition avant d'enregistrer (DomoMud l'écrivait tout seul au premier
/// affichage) ; et les slots secondaires d'un widget sont remplis avec les entités du même appareil — une sonde
/// reçoit ainsi son humidité sans réglage. Les entités <c>excluded</c> (les radiateurs que pilote un
/// thermostat) ne sont pas proposées : un bouton qui les bascule à la main se battrait avec la régulation.
/// </remarks>
public static class DashboardProposal
{
    private sealed record Famille(string Id, string Titre, Func<RegisteredEntity, bool> Prend);

    private static readonly Famille[] Familles =
    [
        new("thermostats", "Thermostats", e => e.Kind == EntityKind.Climate),
        new("temperatures", "Températures", e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Temperature),
        new("interrupteurs", "Interrupteurs et lumières", e => e.Kind is EntityKind.Switch or EntityKind.Light),
        new("capteurs", "Capteurs", e => e.Kind == EntityKind.BinarySensor),
        new("energie", "Énergie", e => e.Kind == EntityKind.Sensor
            && e.DeviceClass is DeviceClasses.Power or DeviceClasses.ApparentPower or DeviceClasses.Energy),
    ];

    public static IReadOnlyList<DashboardSection> Propose(
        IEnumerable<RegisteredEntity> entities, WidgetCatalogSnapshot catalog, IReadOnlySet<string>? excluded = null)
    {
        var actives = entities.Where(e => e.Enabled && !e.Missing).ToList();
        var proposables = actives.Where(e => excluded is null || !excluded.Contains(e.EntityId)).ToList();
        var sections = new List<DashboardSection>();

        foreach (var famille in Familles)
        {
            var cartes = new List<DashboardCard>();
            foreach (var entite in proposables.Where(famille.Prend).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                if (WidgetCatalog.CompatibleWith(catalog, entite).FirstOrDefault() is not { } widget) continue;

                var liens = new Dictionary<string, string>(StringComparer.Ordinal) { [widget.Manifest.Slots[0].Name] = entite.EntityId };
                foreach (var slot in widget.Manifest.Slots.Skip(1))
                {
                    var voisine = actives
                        .Where(v => v.DeviceId == entite.DeviceId && v.EntityId != entite.EntityId && WidgetCatalog.Score(slot, v) > 0)
                        .OrderByDescending(v => WidgetCatalog.Score(slot, v))
                        .FirstOrDefault();
                    if (voisine is not null) liens[slot.Name] = voisine.EntityId;
                }

                cartes.Add(new DashboardCard { Id = Guid.NewGuid().ToString("N")[..12], Widget = widget.Manifest.Id, Entities = liens });
            }

            if (cartes.Count > 0) sections.Add(new DashboardSection { Id = $"{famille.Id}-{Guid.NewGuid().ToString("N")[..6]}", Title = famille.Titre, Cards = cartes });
        }

        return sections;
    }
}
