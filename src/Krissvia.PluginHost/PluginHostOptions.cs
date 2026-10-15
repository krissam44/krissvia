namespace Krissvia.PluginHost;

/// <summary>Réglages du PluginHost (section « PluginHost » de la configuration). Délais : voir docs/TRANSPORT.md.</summary>
public sealed class PluginHostOptions
{
    /// <summary>Délai laissé au plugin pour envoyer <c>hello</c> après l'ouverture de la connexion.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Période des <c>ping</c>, dans chaque sens.</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Silence au-delà duquel une session est déclarée morte (trois battements manqués).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Attente maximale d'un <c>command_result</c> quand la commande n'a pas de TTL.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Jeton attendu par plugin. Un plugin présent ici DOIT présenter son jeton.</summary>
    /// <remarks>Concurrent : le superviseur y inscrit le jeton de chaque plugin qu'il lance pendant que le hub le lit.</remarks>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Tokens { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Accepter sans jeton un plugin absent de <see cref="Tokens"/>. Faux par défaut : n'importe quelle machine
    /// du réseau pourrait sinon déclarer des entités ou recevoir les ordres d'un plugin légitime.
    /// </summary>
    public bool AllowUnregisteredPlugins { get; set; }
}
