using Krissvia.Contracts;

namespace Krissvia.Core.Registry;

/// <summary>Appareil connu du registre. Son <see cref="DeviceId"/> ne change jamais.</summary>
public sealed record RegisteredDevice
{
    public required string DeviceId { get; init; }
    public required string IntegrationId { get; init; }

    /// <summary>Identifiant fourni par le plugin (adresse IEEE…).</summary>
    public required string UniqueId { get; init; }

    /// <summary>Champ de l'utilisateur : le plugin ne le modifie plus après la création.</summary>
    public required string DisplayName { get; init; }

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

    // ── Champs de l'utilisateur : jamais écrasés par un plugin ──
    public required string DisplayName { get; init; }
    public string? Usage { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Puissance consommée allumé, en W, déclarée par l'utilisateur (null : non renseignée). Sert à la somme des
    /// équipements du délesteur et à préremplir les équipements délestables.
    /// </summary>
    public double? NominalPower { get; init; }

    /// <summary>Le plugin ne déclare plus cette entité. Elle reste dans le registre : ses références ne disparaissent pas.</summary>
    public bool Missing { get; init; }
}

public sealed record RegistrationResult(IReadOnlyList<string> Created, IReadOnlyList<string> NowMissing);
