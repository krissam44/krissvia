using Krissvia.Core.Registry;

namespace Krissvia.Core.Widgets;

/// <summary>Une carte prête à afficher : son widget et l'entité liée à chacun de ses slots.</summary>
/// <param name="Widget">Null si le widget n'existe plus : la carte affichera le widget « valeur » et un bandeau.</param>
public sealed record ResolvedCard(DashboardCard Card, CatalogWidget? Widget, IReadOnlyDictionary<string, string> Entities)
{
    public string WidgetId => Widget?.Manifest.Id ?? Card.Widget ?? WidgetCatalog.FallbackId;
}

/// <summary>
/// Le choix du widget, dans l'ordre de WIDGETS.md §6 : celui de la carte, sinon celui de l'entité (écran Entités),
/// sinon le plus précis du catalogue, sinon « valeur ».
/// </summary>
public static class WidgetResolver
{
    public static ResolvedCard Resolve(DashboardCard card, IReadOnlyList<RegisteredEntity> entities, WidgetCatalogSnapshot catalog)
    {
        if (card.Widget is { } choisi) return new ResolvedCard(card, catalog.Find(choisi), card.Entities);

        var principale = entities.FirstOrDefault(e => e.EntityId == card.Entity);
        if (principale is null || ChooseFor(principale, catalog) is not { } widget)
        {
            return new ResolvedCard(card, catalog.Find(WidgetCatalog.FallbackId),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["entite"] = card.Entity ?? "" });
        }

        return new ResolvedCard(card, widget, Bind(widget, principale, entities));
    }

    /// <summary>
    /// Le widget d'une entité : celui qu'a choisi l'utilisateur s'il existe encore et l'accepte, sinon le plus précis.
    /// </summary>
    public static CatalogWidget? ChooseFor(RegisteredEntity entity, WidgetCatalogSnapshot catalog) =>
        entity.Widget is { } choix && catalog.Find(choix) is { } widget && WidgetCatalog.Score(widget.Manifest.Slots[0], entity) >= 0
            ? widget
            : WidgetCatalog.CompatibleWith(catalog, entity).FirstOrDefault();

    /// <summary>
    /// Lie l'entité au slot principal et chaque slot secondaire à une entité du même appareil qui lui convient (la
    /// sonde et son humidité) : une carte automatique n'a rien à régler.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Bind(CatalogWidget widget, RegisteredEntity entity, IReadOnlyList<RegisteredEntity> entities)
    {
        var liens = new Dictionary<string, string>(StringComparer.Ordinal) { [widget.Manifest.Slots[0].Name] = entity.EntityId };
        foreach (var slot in widget.Manifest.Slots.Skip(1))
        {
            var voisine = entities
                .Where(v => v.DeviceId == entity.DeviceId && v.EntityId != entity.EntityId && v.Enabled && !v.Missing
                            && WidgetCatalog.Score(slot, v) > 0)
                .OrderByDescending(v => WidgetCatalog.Score(slot, v))
                .FirstOrDefault();
            if (voisine is not null) liens[slot.Name] = voisine.EntityId;
        }

        return liens;
    }
}
