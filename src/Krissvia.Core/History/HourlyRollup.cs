namespace Krissvia.Core.History;

/// <summary>
/// Une heure d'une série : moyenne <b>pondérée par le temps</b>, minimum, maximum, dernière valeur, et part de l'heure
/// couverte par une valeur connue.
/// </summary>
public sealed record HistoryBucket(DateTimeOffset Hour, double? Min, double? Max, double? Average, double? Last, double Coverage);

/// <summary>
/// Calcul d'une heure à partir des points bruts, en fonction pure. Une série est en escalier : une valeur vaut jusqu'au
/// point suivant. La moyenne pondérée par le temps est ce qui donne du sens à une heure — un radiateur allumé 15 min
/// sur 60 vaut 0,25, quel que soit le nombre de bascules ; une moyenne des points dirait 0,5 pour un allumage et une
/// extinction.
/// </summary>
public static class HourlyRollup
{
    public static readonly TimeSpan Heure = TimeSpan.FromHours(1);

    /// <param name="debut">Début de l'heure.</param>
    /// <param name="avant">La valeur en vigueur au début de l'heure (dernier point avant), null si inconnue ou trou.</param>
    /// <param name="points">Les points de l'heure, triés.</param>
    /// <param name="fin">Fin effective : la fin de l'heure, ou maintenant pour l'heure en cours.</param>
    public static HistoryBucket? Calculer(DateTimeOffset debut, double? avant, IReadOnlyList<(DateTimeOffset At, double? Value)> points, DateTimeOffset fin)
    {
        if (fin <= debut) return null;

        double somme = 0, couvert = 0;
        double? min = null, max = null, derniere = null;
        var courant = avant;
        var depuis = debut;

        void Cloturer(DateTimeOffset jusqua)
        {
            if (courant is not { } v || jusqua <= depuis) return;
            var duree = (jusqua - depuis).TotalSeconds;
            somme += v * duree;
            couvert += duree;
            min = min is null ? v : Math.Min(min.Value, v);
            max = max is null ? v : Math.Max(max.Value, v);
        }

        foreach (var (at, valeur) in points)
        {
            if (at < debut || at >= fin) continue;
            Cloturer(at);
            courant = valeur;
            depuis = at;
            if (valeur is { } v)
            {
                // Une valeur vue à l'instant même de la fin compte dans le min et le max, pas dans la moyenne.
                min = min is null ? v : Math.Min(min.Value, v);
                max = max is null ? v : Math.Max(max.Value, v);
            }
        }

        Cloturer(fin);
        derniere = courant;
        if (couvert == 0 && min is null) return null;
        return new HistoryBucket(debut, min, max, couvert > 0 ? somme / couvert : null, derniere,
            Math.Round(couvert / (fin - debut).TotalSeconds, 4));
    }

    /// <summary>Durée de conservation des points bruts ; les heures consolidées, elles, restent.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>
    /// Jusqu'où purger les points bruts : <see cref="Retention"/>, mais jamais au-delà de la dernière heure consolidée —
    /// purger d'abord effacerait des points avant de les avoir totalisés (DomoMud, conso_jour). Null : rien à purger.
    /// </summary>
    public static DateTimeOffset? LimitePurge(DateTimeOffset maintenant, DateTimeOffset? derniereConsolidee) =>
        derniereConsolidee is not { } c ? null : maintenant - Retention < c ? maintenant - Retention : c;

    public static DateTimeOffset Debut(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % Heure.Ticks, TimeSpan.Zero);
}
