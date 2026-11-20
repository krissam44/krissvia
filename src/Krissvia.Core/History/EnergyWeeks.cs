using System.Globalization;

namespace Krissvia.Core.History;

/// <summary>
/// Une semaine d'un compteur, du lundi au dimanche.
/// </summary>
/// <param name="Monday">Le lundi de la semaine.</param>
/// <param name="Kwh">La somme des jours mesurés ; null si aucun ne l'est (pas « 0 kWh »).</param>
/// <param name="MeasuredDays">Jours mesurés dans la semaine : moins de 7 pour la semaine en cours, un début d'historique
/// ou un trou.</param>
public sealed record EnergyWeek(DateOnly Monday, double? Kwh, int MeasuredDays)
{
    /// <summary>Le numéro de semaine ISO 8601, celui des calendriers français.</summary>
    public int Number => ISOWeek.GetWeekOfYear(Monday.ToDateTime(TimeOnly.MinValue));

    public DateOnly Sunday => Monday.AddDays(6);
}

/// <summary>La consommation par semaine, tirée des jours (<see cref="EnergyDays"/>) : aucun calcul de plus sur l'index.</summary>
public static class EnergyWeeks
{
    /// <summary>Les semaines qui contiennent les jours donnés, de la plus ancienne à la plus récente.</summary>
    public static IReadOnlyList<EnergyWeek> FromDays(IEnumerable<EnergyDay> days) =>
    [
        .. days.GroupBy(d => MondayOf(d.Day))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var mesures = g.Where(d => d.Kwh is not null).ToList();
                return new EnergyWeek(g.Key, mesures.Count == 0 ? null : mesures.Sum(d => d.Kwh!.Value), mesures.Count);
            }),
    ];

    /// <summary>Le lundi de la semaine d'un jour (ISO 8601 : la semaine commence le lundi).</summary>
    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
