namespace Krissvia.Core.Registry;

/// <summary>Une référence vers une entité, posée par un module (thermostat, délestage, dashboard…).</summary>
/// <param name="OwnerKind">Type du module : « thermostat », « delestage »…</param>
/// <param name="OwnerId">Identifiant de l'objet qui référence.</param>
/// <param name="Role">Rôle de l'entité : « temperature_interieure », « sortie »…</param>
/// <param name="EntityId">Entité visée.</param>
/// <param name="OwnerName">Nom de l'objet pour un humain (« Thermostat Salon », « Plugin openHASP ») ; à défaut, type et identifiant.</param>
public sealed record EntityReference(string OwnerKind, string OwnerId, string Role, string EntityId, string? OwnerName = null);

/// <summary>Implémentée par chaque module qui référence des entités.</summary>
public interface IEntityReferenceSource
{
    Task<IReadOnlyList<EntityReference>> GetReferencesAsync(CancellationToken ct = default);
}

public enum ReferenceProblem
{
    /// <summary>L'identifiant n'existe pas dans le registre.</summary>
    Unknown,

    /// <summary>Le plugin ne déclare plus l'entité.</summary>
    Missing,

    /// <summary>L'entité existe mais l'utilisateur l'a désactivée.</summary>
    Disabled,
}

public sealed record IntegrityIssue(EntityReference Reference, ReferenceProblem Problem);

/// <summary>
/// Détecte les références qui ne mènent plus à une entité utilisable. Remplace les listes
/// de « radiateurs inconnus » propres à chaque module : le contrôle est le même partout.
/// </summary>
public sealed class EntityIntegrityChecker(IEntityRegistry registry, IEnumerable<IEntityReferenceSource> sources)
{
    public async Task<IReadOnlyList<IntegrityIssue>> CheckAsync(CancellationToken ct = default)
    {
        var entites = (await registry.ListAsync(ct)).ToDictionary(e => e.EntityId);
        var problemes = new List<IntegrityIssue>();

        foreach (var source in sources)
        {
            foreach (var reference in await source.GetReferencesAsync(ct))
                if (Classify(entites.GetValueOrDefault(reference.EntityId)) is { } probleme)
                    problemes.Add(new IntegrityIssue(reference, probleme));
        }

        return problemes;
    }

    /// <summary>Ce qui ne va pas avec l'entité visée ; null si elle est utilisable.</summary>
    public static ReferenceProblem? Classify(RegisteredEntity? entite) => entite switch
    {
        null => ReferenceProblem.Unknown,
        { Missing: true } => ReferenceProblem.Missing,
        { Enabled: false } => ReferenceProblem.Disabled,
        _ => null,
    };
}
