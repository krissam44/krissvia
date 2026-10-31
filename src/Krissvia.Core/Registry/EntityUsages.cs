using Krissvia.Contracts;

namespace Krissvia.Core.Registry;

/// <summary>
/// Usages qu'un utilisateur donne à une entité (champ <see cref="RegisteredEntity.Usage"/>, jamais écrasé par un
/// plugin). Le plugin dit ce qu'est l'appareil (un relais, une prise) ; l'utilisateur dit à quoi il sert.
/// </summary>
public static class EntityUsages
{
    /// <summary>Un interrupteur qui alimente un radiateur (un relais Shelly, une prise…).</summary>
    public const string Radiator = "radiateur";

    /// <summary>Un interrupteur qui alimente un éclairage ; une entité <c>light</c> l'est d'elle-même.</summary>
    public const string Lighting = "eclairage";

    /// <summary>Une prise commandée (ce qu'on y branche change ; elle reste une prise).</summary>
    public const string Plug = "prise";

    /// <summary>Le relais d'un compteur d'énergie (Shelly EM, Pro 3EM…).</summary>
    public const string Meter = "compteur";

    /// <summary>Un disjoncteur commandé (réarmable à distance) : le couper prive tout un circuit.</summary>
    public const string Breaker = "disjoncteur";

    public static bool IsRadiator(RegisteredEntity e) => e.Kind == EntityKind.Switch && e.Usage == Radiator;

    /// <summary>L'usage d'une entité : celui choisi par l'utilisateur, sinon celui de sa famille (une lumière éclaire).</summary>
    public static string? Of(RegisteredEntity e) => e.Usage ?? (e.Kind == EntityKind.Light ? Lighting : null);
}
