using Krissvia.Core.States;

namespace Krissvia.Climate.LoadShedding;

/// <summary>Nature d'une mesure de puissance. Elle décide de la confiance qu'on lui accorde.</summary>
public enum PowerQuality
{
    /// <summary>VA : ce que mesure un compteur Linky, et ce que compare l'abonnement.</summary>
    Apparent,

    /// <summary>W : ce que mesure une pince. Sous-estime les VA dès qu'il y a de la réactive.</summary>
    Active,

    /// <summary>Somme qui a dû recourir à des puissances nominales déclarées.</summary>
    Estimated,
}

/// <summary>Une mesure de puissance avec sa qualité.</summary>
public readonly record struct PowerSample(double Value, DateTimeOffset MeasuredAt, PowerQuality Quality);

/// <summary>Source de puissance du délesteur. Renvoie null quand aucune mesure exploitable n'existe.</summary>
public interface IPowerSource
{
    PowerSample? Read();
}

/// <summary>Source branchée sur une entité de puissance (pince, Linky, compteur).</summary>
public sealed class EntityPowerSource(IEntityStateReader reader, string entityId, PowerQuality quality) : IPowerSource
{
    public PowerSample? Read()
    {
        if (reader.Get(entityId) is not { } etat || !etat.TryGetNumber(out var valeur)) return null;

        return new PowerSample(valeur, etat.MeasuredAt, quality);
    }
}

/// <summary>Un équipement entrant dans la somme.</summary>
/// <param name="SwitchEntityId">État allumé ou éteint.</param>
/// <param name="PowerEntityId">Mesure réelle, quand l'équipement en publie une.</param>
/// <param name="NominalPower">Puissance déclarée, utilisée quand l'équipement ne mesure rien (fil pilote).</param>
public sealed record MeteredEquipment(string SwitchEntityId, string? PowerEntityId, double NominalPower);

/// <summary>
/// Source « somme des équipements » (D12) : mesure réelle si l'équipement en publie une non nulle, sinon puissance
/// nominale s'il est allumé, sinon zéro.
/// </summary>
/// <remarks>
/// Écart assumé avec DomoMud : si aucun équipement n'a d'état connu, la source est indisponible (null) au lieu de
/// valoir 0 W, par cohérence avec D6 (une absence de donnée n'est pas une mesure nulle). L'instant de mesure est
/// celui de l'état le plus ancien utilisé : la somme n'est pas plus fraîche que sa composante la plus périmée.
/// </remarks>
public sealed class EquipmentSumPowerSource(IEntityStateReader reader, IReadOnlyList<MeteredEquipment> equipment) : IPowerSource
{
    public PowerSample? Read()
    {
        var total = 0.0;
        DateTimeOffset? plusAncien = null;
        var estimee = false;
        var connu = false;

        foreach (var e in equipment)
        {
            // Un état inconnu n'est pas « éteint » : l'équipement est ignoré, comme dans DomoMud.
            if (reader.Get(e.SwitchEntityId) is not { } commutateur || !commutateur.TryGetBool(out var allume)) continue;

            connu = true;
            plusAncien = LePlusAncien(plusAncien, commutateur.MeasuredAt);

            if (e.PowerEntityId is { } idMesure
                && reader.Get(idMesure) is { } mesure
                && mesure.TryGetNumber(out var watts)
                && watts > 0)
            {
                total += watts;
                plusAncien = LePlusAncien(plusAncien, mesure.MeasuredAt);
            }
            else if (allume)
            {
                total += e.NominalPower;
                estimee = true;
            }
        }

        if (!connu || plusAncien is not { } instant) return null;

        return new PowerSample(total, instant, estimee ? PowerQuality.Estimated : PowerQuality.Active);
    }

    private static DateTimeOffset LePlusAncien(DateTimeOffset? courant, DateTimeOffset candidat) =>
        courant is { } c && c < candidat ? c : candidat;
}

/// <summary>Conversion d'une mesure de puissance en entrée du délesteur.</summary>
public static class PowerReadings
{
    /// <summary>
    /// Ramène une mesure à l'unité des seuils (VA). Une mesure active (W) est majorée du facteur fourni, jamais
    /// minorée : il vaut mieux délester un peu tôt que laisser le disjoncteur déclencher. Apparente ou estimée :
    /// valeur inchangée.
    /// </summary>
    /// <param name="activeToApparentFactor">≥ 1. Par exemple 1,10 pour une marge de 10 % sur une pince en W.</param>
    public static PowerReading? ToReading(PowerSample? sample, double activeToApparentFactor = 1.0)
    {
        if (activeToApparentFactor < 1.0)
            throw new ArgumentOutOfRangeException(nameof(activeToApparentFactor), activeToApparentFactor,
                "Le facteur de majoration doit être supérieur ou égal à 1.");

        if (sample is not { } s) return null;

        var valeur = s.Quality == PowerQuality.Active ? s.Value * activeToApparentFactor : s.Value;
        return new PowerReading(valeur, s.MeasuredAt);
    }
}
