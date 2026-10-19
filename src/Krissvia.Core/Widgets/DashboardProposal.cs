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
/// affichage) ; et chaque carte suit son entité (<see cref="WidgetResolver"/>) : son widget est celui de l'entité, ses
/// slots secondaires les entités du même appareil — une sonde reçoit ainsi son humidité sans réglage. Les entités <c>excluded</c> (les radiateurs que pilote un
/// thermostat) ne sont pas proposées : un bouton qui les bascule à la main se battrait avec la régulation.
/// </remarks>
public static class DashboardProposal
{
    private sealed record Famille(string Id, string Titre, Func<RegisteredEntity, bool> Prend);

    private static readonly Famille[] Familles =
    [
        new("thermostats", "Thermostats", e => e.Kind == EntityKind.Climate),
        new("meteo", "Météo", e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Weather),
        new("temperatures", "Températures", e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Temperature),
        new("radiateurs", "Radiateurs", EntityUsages.IsRadiator),
        new("interrupteurs", "Interrupteurs et lumières", e => e.Kind is EntityKind.Switch or EntityKind.Light && !EntityUsages.IsRadiator(e)),
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
                // La carte suit son entité : changer le widget de l'entité dans l'écran Entités la changera aussi.
                if (WidgetResolver.ChooseFor(entite, catalog) is null) continue;
                cartes.Add(new DashboardCard { Id = Guid.NewGuid().ToString("N")[..12], Entity = entite.EntityId });
            }

            if (cartes.Count > 0) sections.Add(new DashboardSection { Id = $"{famille.Id}-{Guid.NewGuid().ToString("N")[..6]}", Title = famille.Titre, Cards = cartes });
        }

        return sections;
    }
}
