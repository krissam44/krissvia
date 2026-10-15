using System.Text.Json;
using System.Text.Json.Serialization;

namespace Krissvia.Contracts;

/// <summary>
/// Message échangé entre un plugin et le cœur. Le champ JSON « type » désigne le message.
/// Le contrat est indépendant du transport (WebSocket, gRPC, MQTT…).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Configure), "configure")]
[JsonDerivedType(typeof(Declare), "declare")]
[JsonDerivedType(typeof(StateBatch), "state")]
[JsonDerivedType(typeof(Availability), "availability")]
[JsonDerivedType(typeof(Discovered), "discovered")]
[JsonDerivedType(typeof(CommandRequest), "command")]
[JsonDerivedType(typeof(CommandResult), "command_result")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Shutdown), "shutdown")]
public abstract record Message;

public enum CommandPriority
{
    Normal,

    /// <summary>
    /// Ordre de sécurité (délestage, fenêtre ouverte, sonde morte) : le plugin l'exécute en premier
    /// et répond toujours par un <see cref="CommandResult"/>.
    /// </summary>
    Safety,
}

public enum CommandOutcome
{
    Accepted,
    Failed,

    /// <summary>L'appareil n'a pas pu être joint : le cœur ne doit pas supposer que l'ordre est passé.</summary>
    Unreachable,
}

/// <summary>plugin → cœur : première trame d'une session.</summary>
public sealed record Hello : Message
{
    public required string PluginId { get; init; }
    public required string PluginVersion { get; init; }

    /// <summary>Version du contrat parlée par le plugin (voir <see cref="ContractInfo.Version"/>).</summary>
    public required string ContractVersion { get; init; }

    public string? Token { get; init; }
}

/// <summary>cœur → plugin : configuration, validée côté cœur par le JSON Schema du plugin.</summary>
public sealed record Configure : Message
{
    public required JsonElement Config { get; init; }
}

/// <summary>plugin → cœur : appareils et entités connus. Peut être renvoyé à chaque changement.</summary>
public sealed record Declare : Message
{
    public required IReadOnlyList<DeviceDescriptor> Devices { get; init; }
}

/// <summary>plugin → cœur : nouvelles valeurs, par lot.</summary>
public sealed record StateBatch : Message
{
    public required IReadOnlyList<EntityState> States { get; init; }
}

/// <summary>plugin → cœur : joignabilité d'un appareil, ou du plugin lui-même si l'identifiant est absent.</summary>
public sealed record Availability : Message
{
    public string? DeviceUniqueId { get; init; }
    public required bool Online { get; init; }
}

/// <summary>plugin → cœur : appareils détectés mais pas encore configurés (auto-import).</summary>
public sealed record Discovered : Message
{
    public required IReadOnlyList<DeviceDescriptor> Candidates { get; init; }
}

/// <summary>cœur → plugin : ordre sur une entité.</summary>
public sealed record CommandRequest : Message
{
    /// <summary>Identifiant de corrélation, repris dans <see cref="CommandResult"/>.</summary>
    public required string Id { get; init; }

    public required string EntityUniqueId { get; init; }
    public required string Command { get; init; }
    public JsonElement? Args { get; init; }
    public CommandPriority Priority { get; init; } = CommandPriority.Normal;

    /// <summary>Durée de validité : passé ce délai, le plugin ignore l'ordre au lieu de l'exécuter tard.</summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>plugin → cœur : issue d'une commande.</summary>
public sealed record CommandResult : Message
{
    public required string Id { get; init; }
    public required CommandOutcome Outcome { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Dans les deux sens : battement de cœur. Détails de délai définis dans PluginHost.</summary>
public sealed record Ping : Message;

/// <summary>Dans les deux sens : fermeture propre de la session.</summary>
public sealed record Shutdown : Message
{
    public string? Reason { get; init; }
}
