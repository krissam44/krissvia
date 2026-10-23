namespace Krissvia.Core.History;

/// <summary>Une période d'affichage : sa durée et le pas de sa grille (6 h par 5 min, 30 j par 6 h…).</summary>
public sealed record HistoryRange(string Key, string Label, TimeSpan Duration, TimeSpan Step)
{
    /// <summary>Au-delà de 3 jours, les points bruts ne sont plus gardés que 30 jours : on lit les heures consolidées.</summary>
    public bool UsesHourly => Duration > TimeSpan.FromDays(3);
}

/// <summary>Une case de la grille : la moyenne pondérée par le temps, le min et le max ; tout est nul sans donnée.</summary>
public sealed record CurvePoint(DateTimeOffset At, double? Mean, double? Min, double? Max);

/// <summary>
/// Les courbes de l'interface (HISTORIQUE.md §5) : une grille régulière, quelle que soit la densité des points. Un Linky
/// publie toutes les 2 s et une sonde trois fois par heure : tracés tels quels, l'un serait un pâté et l'autre une
/// ligne brisée. Pur et testé : c'est ici que se décide ce qu'affiche une case.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Une valeur vaut jusqu'au point suivant (la série est en escalier, HISTORIQUE.md §3) : la moyenne d'une case est
/// pondérée par le temps passé à chaque valeur. Un radiateur allumé 5 min sur 15 vaut 0,33 ; une moyenne des points
/// dirait 0,5 s'il a publié deux fois.</item>
/// <item>Un trou (valeur nulle, entité injoignable) ne compte pas, et une case sans aucune valeur connue reste nulle :
/// la courbe s'interrompt au lieu de tirer un trait jusqu'au retour du capteur.</item>
/// <item>Après le dernier point, la valeur court jusqu'à « maintenant », pas au-delà : la fin de la grille est à venir.</item>
/// </list>
/// </remarks>
public static class HistoryCurve
{
    public static readonly IReadOnlyList<HistoryRange> Ranges =
    [
        new("6h", "6 h", TimeSpan.FromHours(6), TimeSpan.FromMinutes(5)),
        new("24h", "24 h", TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)),
        new("7j", "7 j", TimeSpan.FromDays(7), TimeSpan.FromHours(1)),
        new("30j", "30 j", TimeSpan.FromDays(30), TimeSpan.FromHours(6)),
    ];

    /// <summary>Les débuts de case, du plus ancien au plus récent ; la dernière contient « maintenant ».</summary>
    public static IReadOnlyList<DateTimeOffset> Grid(HistoryRange range, DateTimeOffset now)
    {
        var pas = range.Step.Ticks;
        var derniere = new DateTimeOffset(now.UtcTicks / pas * pas, TimeSpan.Zero);
        var nombre = (int)(range.Duration.Ticks / pas);
        return [.. Enumerable.Range(0, nombre).Select(i => derniere.AddTicks(-(long)(nombre - 1 - i) * pas))];
    }

    /// <summary>
    /// Les cases tirées des points bruts (<see cref="IHistoryRepository.ReadAsync"/>, dont le premier peut précéder la
    /// grille : la valeur en vigueur à son début).
    /// </summary>
    public static IReadOnlyList<CurvePoint> FromPoints(IReadOnlyList<HistoryPoint> points, IReadOnlyList<DateTimeOffset> grid, TimeSpan step, DateTimeOffset now)
    {
        var tries = points.OrderBy(p => p.At).ToList();
        var resultat = new List<CurvePoint>(grid.Count);
        var i = 0;
        foreach (var debut in grid)
        {
            var fin = Min(debut + step, now);
            // Le dernier point avant la case : sa valeur court encore à son début.
            while (i + 1 < tries.Count && tries[i + 1].At <= debut) i++;

            double somme = 0, duree = 0;
            double? min = null, max = null;
            for (var j = i; j < tries.Count && tries[j].At < fin; j++)
            {
                if (tries[j].Value is not { } v) continue;
                var de = Max(tries[j].At, debut);
                var a = j + 1 < tries.Count ? Min(tries[j + 1].At, fin) : fin;
                if (a <= de) continue;
                var s = (a - de).TotalSeconds;
                somme += v * s;
                duree += s;
                min = min is null ? v : Math.Min(min.Value, v);
                max = max is null ? v : Math.Max(max.Value, v);
            }

            resultat.Add(new CurvePoint(debut, duree > 0 ? somme / duree : null, min, max));
        }

        return resultat;
    }

    /// <summary>
    /// Les cases tirées des heures consolidées (<see cref="IHistoryRepository.ReadHourlyAsync"/>) : moyenne des heures
    /// pondérée par leur couverture, min et max des heures. Une case de 6 h ne pèse que les heures où l'on savait.
    /// </summary>
    public static IReadOnlyList<CurvePoint> FromHours(IReadOnlyList<HistoryBucket> hours, IReadOnlyList<DateTimeOffset> grid, TimeSpan step)
    {
        var resultat = new List<CurvePoint>(grid.Count);
        foreach (var debut in grid)
        {
            var dedans = hours.Where(h => h.Hour >= debut && h.Hour < debut + step && h.Average is not null && h.Coverage > 0).ToList();
            var poids = dedans.Sum(h => h.Coverage);
            resultat.Add(new CurvePoint(debut,
                poids > 0 ? dedans.Sum(h => h.Average!.Value * h.Coverage) / poids : null,
                dedans.Select(h => h.Min).Where(v => v is not null).DefaultIfEmpty().Min(),
                dedans.Select(h => h.Max).Where(v => v is not null).DefaultIfEmpty().Max()));
        }

        return resultat;
    }

    /// <summary>La moyenne des cases connues (toutes de même durée) ; nulle sans aucune donnée.</summary>
    public static double? Mean(IReadOnlyList<CurvePoint> points)
    {
        var connus = points.Where(p => p.Mean is not null).ToList();
        return connus.Count == 0 ? null : connus.Average(p => p.Mean!.Value);
    }

    /// <summary>La courbe d'une série sur une période : points bruts jusqu'à 3 jours, heures consolidées au-delà.</summary>
    public static async Task<IReadOnlyList<CurvePoint>> ReadAsync(IHistoryRepository repository, string series, HistoryRange range,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var grille = Grid(range, now);
        var debut = grille[0];
        return range.UsesHourly
            ? FromHours(await repository.ReadHourlyAsync(series, debut, now, ct), grille, range.Step)
            : FromPoints(await repository.ReadAsync(series, debut, now, ct), grille, range.Step, now);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
