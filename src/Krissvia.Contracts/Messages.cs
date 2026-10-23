using System.Text.Json;
using System.Text.Json.Serialization;

namespace Krissvia.Contracts;

/// <summary>
/// Message échangé entre un plugin et le cœur. Le champ JSON « type » désigne le message.
/// Le contrat est indépendant du transport (WebSocket, gRPC, MQTT…).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(Configure), "configure")]
[JsonDerivedType(typeof(ConfigureResult), "configure_result")]
[JsonDerivedType(typeof(Declare), "declare")]
[JsonDerivedType(typeof(StateBatch), "state")]
[JsonDerivedType(typeof(Availability), "availability")]
[JsonDerivedType(typeof(Discovered), "discovered")]
[JsonDerivedType(typeof(CommandRequest), "command")]
[JsonDerivedType(typeof(CommandResult), "command_result")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Shutdown), "shutdown")]
[JsonDerivedType(typeof(Subscribe), "subscribe")]
[JsonDerivedType(typeof(Subscribed), "subscribed")]
[JsonDerivedType(typeof(EntityStateUpdate), "entity_state")]
[JsonDerivedType(typeof(EntityCommandRequest), "entity_command")]
[JsonDerivedType(typeof(EntityCommandResponse), "entity_command_result")]
[JsonDerivedType(typeof(LinkState), "link_state")]
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

/// <summary>Motif d'un refus de session.</summary>
public enum RejectReason
{
    /// <summary>La version du contrat parlée par le plugin n'est pas comprise par le cœur.</summary>
    IncompatibleContract,

    /// <summary>Jeton absent ou faux.</summary>
    InvalidToken,

    /// <summary>Plugin inconnu du cœur, alors que le cœur n'accepte que les plugins déclarés.</summary>
    UnknownPlugin,

    /// <summary>La première trame n'était pas un <see cref="Hello"/> valide, ou n'est pas arrivée à temps.</summary>
    ProtocolError,
}

/// <summary>cœur → plugin : réponse à <see cref="Hello"/>, la session est ouverte.</summary>
public sealed record Welcome : Message
{
    /// <summary>Version du contrat retenue pour la session.</summary>
    public required string ContractVersion { get; init; }

    /// <summary>Période des <see cref="Ping"/> attendus du plugin.</summary>
    public required int HeartbeatSeconds { get; init; }

    /// <summary>Silence au-delà duquel chaque côté considère l'autre comme mort.</summary>
    public required int TimeoutSeconds { get; init; }
}

/// <summary>cœur → plugin : réponse à <see cref="Hello"/>, la session est refusée puis fermée.</summary>
public sealed record Rejected : Message
{
    public required RejectReason Reason { get; init; }
    public string? Detail { get; init; }
}

/// <summary>cœur → plugin : configuration, validée côté cœur par le JSON Schema du plugin.</summary>
public sealed record Configure : Message
{
    public required JsonElement Config { get; init; }
}

/// <summary>
/// plugin → cœur : la configuration reçue est-elle utilisable ? Valide au schéma ne veut pas dire utilisable
/// (adresse injoignable, broker qui refuse la connexion).
/// </summary>
public sealed record ConfigureResult : Message
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
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

/// <summary>
/// plugin → cœur : joignabilité d'une entité, d'un appareil, ou du plugin lui-même si aucun identifiant n'est
/// donné. <c>online: true</c> ne rend disponibles que les entités dont une valeur a été reçue depuis
/// l'ouverture de la session : une valeur restaurée au démarrage du cœur n'est pas une mesure.
/// </summary>
public sealed record Availability : Message
{
    public string? EntityUniqueId { get; init; }
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

/// <summary>
/// Dans les deux sens : battement de cœur, envoyé toutes les <see cref="Welcome.HeartbeatSeconds"/> secondes
/// par chaque côté. Aucune réponse n'est attendue : n'importe quelle trame reçue prouve que l'autre vit.
/// </summary>
public sealed record Ping : Message;

/// <summary>Dans les deux sens : fermeture propre de la session.</summary>
public sealed record Shutdown : Message
{
    public string? Reason { get; init; }
}

// ── Contrat 0.3 : lire et commander d'autres entités (docs/CONTRACT-V0.3.md) ──────────────────────────────────────

/// <summary>
/// plugin → cœur : les entités à suivre. La liste est COMPLÈTE et remplace la précédente ; l'abonnement ne survit
/// pas à la session.
/// </summary>
public sealed record Subscribe : Message
{
    public required IReadOnlyList<string> EntityIds { get; init; }
}

/// <summary>Pourquoi une entité est refusée à l'abonnement.</summary>
public enum SubscriptionDenial
{
    /// <summary>Pas choisie dans les réglages du plugin : il n'y a pas droit.</summary>
    NotAllowed,

    /// <summary>Absente du registre.</summary>
    Unknown,
}

public sealed record DeniedEntity(string EntityId, SubscriptionDenial Reason);

/// <summary>cœur → plugin : réponse à <see cref="Subscribe"/>. Les entités accordées reçoivent aussitôt leur état.</summary>
public sealed record Subscribed : Message
{
    public required IReadOnlyList<string> Granted { get; init; }
    public IReadOnlyList<DeniedEntity> Denied { get; init; } = [];
}

/// <summary>
/// cœur → plugin : l'état d'une entité suivie, à l'abonnement puis à chaque changement. <see cref="Value"/> et
/// <see cref="MeasuredAt"/> sont absents si l'entité n'a jamais rien publié.
/// </summary>
public sealed record EntityStateUpdate : Message
{
    public required string EntityId { get; init; }
    public required string Name { get; init; }
    public required EntityKind Kind { get; init; }
    public string? DeviceClass { get; init; }
    public string? Unit { get; init; }
    public required bool Available { get; init; }
    public JsonElement? Value { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }
}

/// <summary>plugin → cœur : un ordre sur une entité qui n'est pas forcément la sienne. Passe par le bus de commandes.</summary>
public sealed record EntityCommandRequest : Message
{
    /// <summary>Corrélation, reprise dans <see cref="EntityCommandResponse"/>.</summary>
    public required string Id { get; init; }

    public required string EntityId { get; init; }
    public required string Command { get; init; }
    public JsonElement? Args { get; init; }
}

/// <summary>cœur → plugin : issue d'un <see cref="EntityCommandRequest"/>.</summary>
public sealed record EntityCommandResponse : Message
{
    public required string Id { get; init; }
    public required CommandOutcome Outcome { get; init; }

    /// <summary>La vérification de l'effet par le bus (<c>confirmed</c>, <c>not_confirmed</c>, <c>not_applicable</c>…).</summary>
    public string? Verification { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// plugin → cœur (contrat 0.3) : l'état d'un lien du plugin vers l'extérieur, nommé (<c>mqtt</c>). Distinct de
/// <see cref="Availability"/>, qui dit si les entités sont joignables : un plugin Zigbee2MQTT peut être connecté au
/// broker alors que le pont Zigbee est hors ligne. C'est ce qui colore la pastille MQTT de la barre du haut.
/// </summary>
/// <param name="Link">Nom du lien : <c>mqtt</c>.</param>
/// <param name="Detail">Pourquoi il est coupé (« connexion refusée : identifiants »…), pour un humain.</param>
public sealed record LinkState : Message
{
    public required string Link { get; init; }
    public required bool Connected { get; init; }
    public string? Detail { get; init; }
}
