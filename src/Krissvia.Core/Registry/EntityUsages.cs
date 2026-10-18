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

    public static bool IsRadiator(RegisteredEntity e) => e.Kind == EntityKind.Switch && e.Usage == Radiator;
}
