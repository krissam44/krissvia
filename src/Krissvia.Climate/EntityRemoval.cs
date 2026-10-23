using Krissvia.Climate.Automations;
using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;

namespace Krissvia.Climate;

/// <summary>Ce qu'a donné une demande de suppression : supprimée, ou refusée parce que d'autres s'en servent.</summary>
/// <param name="UsedBy">Ce qui vise encore l'entité, lisible tel quel (« Automatisme « Lampe salon » »).</param>
public sealed record EntityRemovalResult(bool Deleted, IReadOnlyList<string> UsedBy);

/// <summary>
/// Supprimer une entité disparue, sans casser ce qui s'en sert : thermostats, programmations, délestage, alarme et
/// réglages des plugins (<see cref="IEntityReferenceSource"/>), mais aussi automatismes et cartes du tableau de bord.
/// Tant que l'un d'eux la vise, la suppression est refusée et la liste dit où aller la retirer : une entité effacée
/// sous un thermostat le laisserait réguler dans le vide, exactement le piège du radiateur renommé de DomoMud.
/// </summary>
public sealed class EntityRemoval(
    IEntityRegistry registry, IEnumerable<IEntityReferenceSource> sources, AutomationService automations, DashboardService dashboards)
{
    public async Task<IReadOnlyList<string>> UsagesAsync(string entityId, CancellationToken ct = default)
    {
        var usages = new List<string>();
        foreach (var source in sources)
            usages.AddRange((await source.GetReferencesAsync(ct)).Where(r => r.EntityId == entityId)
                .Select(r => r.OwnerName ?? $"{r.OwnerKind} {r.OwnerId}"));

        foreach (var a in automations.List())
        {
            var visees = new[] { a.Trigger.EntityId }
                .Concat(a.Conditions.Select(c => c.EntityId))
                .Concat(a.Actions.Select(x => x.EntityId));
            if (visees.Contains(entityId)) usages.Add($"Automatisme « {(a.Name is { Length: > 0 } n ? n : a.Id)} »");
        }

        foreach (var page in await dashboards.ListAsync(ct))
        {
            var cartes = page.Sections.SelectMany(s => s.Cards).Count(c => c.Entity == entityId || c.Entities.Values.Contains(entityId));
            if (cartes > 0) usages.Add($"Tableau de bord « {page.Name} » ({cartes} carte{(cartes > 1 ? "s" : "")})");
        }

        return [.. usages.Distinct()];
    }

    /// <summary>
    /// Supprime l'entité si plus rien ne la vise. Lève <see cref="InvalidOperationException"/> si son plugin la déclare
    /// encore (<see cref="IEntityRegistry.DeleteAsync"/>).
    /// </summary>
    public async Task<EntityRemovalResult> DeleteAsync(string entityId, CancellationToken ct = default)
    {
        if (await registry.GetAsync(entityId, ct) is not { } entite) return new EntityRemovalResult(false, []);
        // Encore déclarée : c'est la vraie raison du refus, avant toute liste d'usages. Le registre le dit (il lève).
        if (!entite.Missing) await registry.DeleteAsync(entityId, ct);
        if (await UsagesAsync(entityId, ct) is { Count: > 0 } usages) return new EntityRemovalResult(false, usages);
        return new EntityRemovalResult(await registry.DeleteAsync(entityId, ct), []);
    }
}
