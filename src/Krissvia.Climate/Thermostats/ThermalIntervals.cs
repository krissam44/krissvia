using Krissvia.Core.History;

namespace Krissvia.Climate.Thermostats;

/// <summary>
/// Découpe l'historique d'un thermostat en intervalles pour l'identification (<see cref="ThermalModel"/>), en fonction pure.
/// </summary>
/// <remarks>
/// Chaque série est en escalier (une valeur vaut jusqu'au point suivant) : la part de chauffe et les moyennes sont
/// pondérées par le temps (<see cref="HourlyRollup.Calculer"/>, qui vaut pour n'importe quelle fenêtre). Un intervalle
/// où l'une des trois séries est inconnue plus de 10 % du temps (sonde muette, trou) est écarté : mieux vaut moins
/// d'intervalles que des intervalles inventés.
/// </remarks>
public static class ThermalIntervals
{
    public static readonly TimeSpan Pas = TimeSpan.FromMinutes(30);
    public const double CouvertureMin = 0.9;

    public static IReadOnlyList<ThermalInterval> Build(
        IReadOnlyList<HistoryPoint> interieur, IReadOnlyList<HistoryPoint> exterieur, IReadOnlyList<HistoryPoint> chauffe,
        DateTimeOffset depuis, DateTimeOffset jusqua, TimeSpan? pas = null)
    {
        var p = pas ?? Pas;
        var resultat = new List<ThermalInterval>();
        for (var a = depuis; a + p <= jusqua; a += p)
        {
            var b = a + p;
            if (Moyenne(interieur, a, b) is not { } ti || Moyenne(exterieur, a, b) is not { } te || Moyenne(chauffe, a, b) is not { } u) continue;
            if (ValeurA(interieur, a) is not { } debut || ValeurA(interieur, b) is not { } fin) continue;
            resultat.Add(new ThermalInterval(p.TotalHours, debut, fin, u, ti - te));
        }

        return resultat;
    }

    private static double? Moyenne(IReadOnlyList<HistoryPoint> serie, DateTimeOffset a, DateTimeOffset b)
    {
        var dedans = new List<(DateTimeOffset, double?)>();
        foreach (var point in serie)
            if (point.At >= a && point.At < b) dedans.Add((point.At, point.Value));
        var seau = HourlyRollup.Calculer(a, Avant(serie, a), dedans, b);
        return seau is { Coverage: >= CouvertureMin, Average: { } v } ? v : null;
    }

    /// <summary>La valeur en vigueur juste avant l'instant (dernier point strictement antérieur).</summary>
    private static double? Avant(IReadOnlyList<HistoryPoint> serie, DateTimeOffset instant)
    {
        double? v = null;
        foreach (var point in serie)
        {
            if (point.At >= instant) break;
            v = point.Value;
        }

        return v;
    }

    /// <summary>La valeur à l'instant : celle du dernier point qui ne le dépasse pas.</summary>
    private static double? ValeurA(IReadOnlyList<HistoryPoint> serie, DateTimeOffset instant)
    {
        double? v = null;
        foreach (var point in serie)
        {
            if (point.At > instant) break;
            v = point.Value;
        }

        return v;
    }
}
