using Krissvia.Contracts;

namespace Krissvia.Core.Registry;

/// <summary>Appareil connu du registre. Son <see cref="DeviceId"/> ne change jamais.</summary>
public sealed record RegisteredDevice
{
    public required string DeviceId { get; init; }
    public required string IntegrationId { get; init; }

    /// <summary>Identifiant fourni par le plugin (adresse IEEE…).</summary>
    public required string UniqueId { get; init; }

    /// <summary>
    /// Champ de l'utilisateur. Il suit le nom proposé par le plugin tant qu'il lui est égal (<see cref="ProposedName"/>) ;
    /// renommé par l'utilisateur, il n'est plus jamais touché.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>Le dernier nom proposé par le plugin ; null pour un appareil enregistré avant le schéma v7.</summary>
    public string? ProposedName { get; init; }

    public string? Manufacturer { get; init; }
    public string? Model { get; init; }

    /// <summary>Champ de l'utilisateur.</summary>
    public string? Room { get; init; }

    /// <summary>Données opaques du plugin (JSON).</summary>
    public string? ExtraJson { get; init; }
}

/// <summary>
/// Entité connue du registre. <see cref="EntityId"/> est attribué une fois pour toutes :
/// c'est lui que référencent thermostats, délestage, automatismes et dashboards.
/// </summary>
public sealed record RegisteredEntity
{
    public required string EntityId { get; init; }
    public required string DeviceId { get; init; }
    public required string IntegrationId { get; init; }
    public required string UniqueId { get; init; }

    // ── Données techniques : mises à jour par le plugin à chaque déclaration ──
    public required EntityKind Kind { get; init; }
    public string? DeviceClass { get; init; }
    public string? Unit { get; init; }
    public StateClass? StateClass { get; init; }
    public IReadOnlyList<string> Commands { get; init; } = [];
    public string? ExtraJson { get; init; }

    /// <summary>
    /// Le dernier nom proposé par le plugin (« appareil + entité »). <see cref="DisplayName"/> le suit tant qu'il lui est
    /// égal : renommer l'appareil dans Zigbee2MQTT ou dans les entités manuelles renomme la carte, sauf si l'utilisateur a
    /// choisi un autre nom dans l'écran Entités. Null pour une entité enregistrée avant le schéma v7 : son nom ne suit
    /// pas, car rien ne dit s'il a été choisi par l'utilisateur.
    /// </summary>
    public string? ProposedName { get; init; }

    // ── Champs de l'utilisateur : jamais écrasés par un plugin (le nom, seulement tant qu'il est celui proposé) ──
    public required string DisplayName { get; init; }
    public string? Usage { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Puissance consommée allumé, en W, déclarée par l'utilisateur (null : non renseignée). Sert à la somme des
    /// équipements du délesteur et à préremplir les équipements délestables.
    /// </summary>
    public double? NominalPower { get; init; }

    /// <summary>
    /// Widget choisi pour cette entité (identifiant du catalogue) ; null : le plus précis, choisi automatiquement.
    /// Les cartes « widget de l'entité » du tableau de bord le suivent (WIDGETS.md §6).
    /// </summary>
    public string? Widget { get; init; }

    /// <summary>Le plugin ne déclare plus cette entité. Elle reste dans le registre : ses références ne disparaissent pas.</summary>
    public bool Missing { get; init; }
}

public sealed record RegistrationResult(IReadOnlyList<string> Created, IReadOnlyList<string> NowMissing);
