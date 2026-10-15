using Krissvia.Contracts;
using Krissvia.Core.Registry;
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
/// <list type="bullet">
/// <item>Écart assumé avec DomoMud : si aucun équipement n'a d'état connu, la source est indisponible (null) au lieu
/// de valoir 0 W, par cohérence avec D6 (une absence de donnée n'est pas une mesure nulle).</item>
/// <item>La somme est datée de l'instant de la lecture, pas de son état le plus ancien. Un switch ne publie qu'à
/// son changement, une prise mesurée qu'à une variation notable : un radiateur allumé depuis trois heures aurait
/// rendu la somme « périmée » en permanence, et donc inutilisable. La protection vient de la disponibilité : un
/// plugin mort ou un appareil injoignable rend ses états indisponibles, et ils sont ignorés.</item>
/// </list>
/// </remarks>
/// <param name="equipment">Relue à chaque lecture : une puissance déclarée ou un équipement ajouté compte aussitôt.</param>
public sealed class EquipmentSumPowerSource(IEntityStateReader reader, Func<IReadOnlyList<MeteredEquipment>> equipment, TimeProvider clock) : IPowerSource
{
    public PowerSample? Read()
    {
        var total = 0.0;
        var estimee = false;
        var connu = false;

        foreach (var e in equipment())
        {
            // Un état inconnu ou indisponible n'est pas « éteint » : l'équipement est ignoré, comme dans DomoMud.
            if (reader.Get(e.SwitchEntityId) is not { } commutateur || !commutateur.TryGetBool(out var allume)) continue;

            connu = true;
            if (e.PowerEntityId is { } idMesure
                && reader.Get(idMesure) is { } mesure
                && mesure.TryGetNumber(out var watts)
                && watts > 0)
            {
                total += watts;
            }
            else if (allume)
            {
                total += e.NominalPower;
                estimee = true;
            }
        }

        return connu ? new PowerSample(total, clock.GetUtcNow(), estimee ? PowerQuality.Estimated : PowerQuality.Active) : null;
    }
}

/// <summary>Les équipements de la somme, tirés du registre.</summary>
public static class DeclaredEquipment
{
    /// <summary>
    /// Chaque switch actif qui a une puissance déclarée ; sa mesure réelle est celle de son appareil (un capteur de
    /// puissance en W), s'il en publie une. Comme dans DomoMud : on déclare la puissance d'un équipement une fois, la
    /// somme suit sans autre réglage.
    /// </summary>
    public static IReadOnlyList<MeteredEquipment> FromRegistry(IEnumerable<RegisteredEntity> entites)
    {
        var liste = entites.Where(e => e.Enabled && !e.Missing).ToList();
        var mesures = liste
            .Where(e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Power)
            .GroupBy(e => e.DeviceId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.EntityId, StringComparer.Ordinal).First().EntityId);

        return liste
            .Where(e => e.Kind == EntityKind.Switch && e.NominalPower is > 0)
            .Select(e => new MeteredEquipment(e.EntityId, mesures.GetValueOrDefault(e.DeviceId), e.NominalPower!.Value))
            .ToList();
    }
}

/// <summary>
/// Sources par ordre de préférence : la première qui a une mesure fraîche l'emporte, ramenée à l'unité des seuils
/// avec sa propre majoration. Aucune fraîche : pas de mesure (D6), jamais zéro.
/// </summary>
public sealed class PowerSourceChain(IReadOnlyList<(string Name, IPowerSource Source, double Factor)> sources, TimeSpan maxAge)
{
    public (PowerReading? Reading, string? Source) Read(DateTimeOffset now)
    {
        foreach (var (nom, source, facteur) in sources)
        {
            if (source.Read() is not { } mesure || now - mesure.MeasuredAt > maxAge) continue;
            return (PowerReadings.ToReading(mesure, facteur), nom);
        }

        return (null, null);
    }
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
