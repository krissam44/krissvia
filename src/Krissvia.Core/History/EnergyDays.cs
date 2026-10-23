namespace Krissvia.Core.History;

/// <summary>Un jour d'un compteur : sa consommation (kWh) et sa puissance maximale ; nulles faute de mesure.</summary>
public sealed record EnergyDay(DateOnly Day, double? Kwh, double? PeakPower);

/// <summary>
/// La consommation par jour d'un compteur (HISTORIQUE.md §6), tirée de l'historique de son index (kWh, toujours
/// croissant) : rien de plus à enregistrer, l'index et la puissance le sont déjà à la minute, puis heure par heure.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Un jour vaut la <b>somme des hausses d'index d'heure en heure</b> (dernière valeur de chaque heure), et non
/// « maximum − minimum » du jour comme dans DomoMud : un compteur remis à zéro gonflait ce jour-là de tout son index.
/// Ici une baisse est lue comme un redémarrage à zéro, et seule la nouvelle valeur compte.</item>
/// <item>Les jours sont <b>locaux</b> : en UTC, une journée française commencerait à 1 h ou 2 h selon la saison, et la
/// consommation de la nuit tomberait dans la veille.</item>
/// <item>La première heure connue n'a pas de précédente : sa propre montée (dernière − minimum) compte. Le reste vient
/// de la veille, qu'il faut donc lire aussi (<see cref="FromHours"/> la reçoit et ne la rend pas).</item>
/// </list>
/// </remarks>
public static class EnergyDays
{
    /// <param name="index">Les heures de l'index, depuis la veille de <paramref name="first"/> (pour connaître la valeur de départ).</param>
    /// <param name="power">Les heures de la puissance (W ou VA), pour le maximum du jour ; vide si le compteur n'en a pas.</param>
    public static IReadOnlyList<EnergyDay> FromHours(IReadOnlyList<HistoryBucket> index, IReadOnlyList<HistoryBucket> power,
        TimeZoneInfo zone, DateOnly first, DateOnly last)
    {
        var conso = new Dictionary<DateOnly, double>();
        double? precedent = null;
        foreach (var h in index.Where(h => h.Last is not null).OrderBy(h => h.Hour))
        {
            var valeur = h.Last!.Value;
            var hausse = precedent is { } p ? valeur - p : valeur - (h.Min ?? valeur);
            if (hausse < 0) hausse = valeur;   // remis à zéro : il est reparti de 0
            var jour = Jour(h.Hour, zone);
            conso[jour] = conso.GetValueOrDefault(jour) + hausse;
            precedent = valeur;
        }

        var pointes = power.Where(h => h.Max is not null)
            .GroupBy(h => Jour(h.Hour, zone))
            .ToDictionary(g => g.Key, g => g.Max(h => h.Max!.Value));

        var jours = new List<EnergyDay>();
        for (var d = first; d <= last; d = d.AddDays(1))
            jours.Add(new EnergyDay(d, conso.TryGetValue(d, out var k) ? k : null, pointes.TryGetValue(d, out var m) ? m : null));
        return jours;
    }

    /// <summary>Minuit local d'un jour, en instant : le début de la journée pour le compteur.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>Le jour local auquel appartient une heure (UTC).</summary>
    public static DateOnly Jour(DateTimeOffset heure, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(heure, zone).DateTime);
}
