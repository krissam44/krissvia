namespace Krissvia.Core.Configuration;

/// <summary>Un document de configuration : son identifiant et son contenu JSON.</summary>
public sealed record ConfigurationDocument(string Id, string Json);

/// <summary>
/// Configuration des modules (thermostats, délestage, alarme…), un document JSON par élément, rangé par
/// collection. Le JSON plutôt qu'une colonne par réglage : un réglage ajouté plus tard ne demande aucune
/// migration, et la persistance n'a pas à connaître les modules (règle d'architecture).
/// </summary>
public interface IConfigurationStore
{
    Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default);

    /// <summary>Crée ou remplace.</summary>
    Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default);

    /// <summary>Renvoie faux si le document n'existait pas.</summary>
    Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default);
}
