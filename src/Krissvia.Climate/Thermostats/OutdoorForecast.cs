using System.Globalization;
using System.Text.Json;
using Krissvia.Core.States;

namespace Krissvia.Climate.Thermostats;

/// <summary>
/// La température extérieure ANTICIPÉE d'un thermostat : la moyenne prévue sur les deux prochaines heures, tirée de
/// l'entité météo d'un lieu (valeur objet, champ <c>hourly</c> : <c>[{"time": "…Z", "temperature": 5.2}]</c>).
/// Le feed-forward s'en sert à la place de la mesure : une maison réagit au froid avec des heures de retard, la
/// mesure arrive quand le refroidissement est déjà commencé (le « palier ③ » de DomoMud).
/// </summary>
/// <remarks>
/// Fonction pure : le moteur l'interroge, les tests aussi. Elle ne renvoie rien plutôt qu'une valeur douteuse — une
/// prévision indisponible, trop vieille, ou qui ne couvre pas tout l'horizon laisse le moteur sur la mesure.
/// </remarks>
public static class OutdoorForecast
{
    /// <summary>Horizon de la moyenne : celui de DomoMud (<c>HorizonAnticipationHeures</c>).</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(2);

    /// <summary>
    /// Âge au-delà duquel une prévision n'est plus crue. Le plugin relève toutes les 10 min ; trois heures laissent
    /// passer une panne d'Open-Meteo sans revenir à la mesure à la première erreur.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    private static readonly TimeSpan Pas = TimeSpan.FromMinutes(30);

    /// <summary>La moyenne prévue d'une entité météo, ou null si elle n'est pas utilisable maintenant.</summary>
    public static double? Anticipated(EntityStateSnapshot? etat, DateTimeOffset maintenant) => MeanOver(etat, maintenant, Horizon);

    /// <summary>La moyenne prévue sur un autre horizon (la durée d'une montée en température), mêmes garde-fous.</summary>
    public static double? MeanOver(EntityStateSnapshot? etat, DateTimeOffset maintenant, TimeSpan horizon) =>
        etat is { Available: true } && maintenant - etat.MeasuredAt <= MaxAge
            ? MeanAhead(etat.Value, maintenant, horizon)
            : null;

    /// <summary>
    /// Moyenne des températures prévues de <paramref name="maintenant"/> à <paramref name="maintenant"/> +
    /// <paramref name="horizon"/>, échantillonnées toutes les 30 min et interpolées entre les points horaires. Null si
    /// la série ne couvre pas tout l'intervalle.
    /// </summary>
    public static double? MeanAhead(JsonElement valeur, DateTimeOffset maintenant, TimeSpan horizon)
    {
        var points = Lire(valeur);
        if (points.Count < 2) return null;

        var somme = 0.0;
        var n = 0;
        for (var t = maintenant; t <= maintenant + horizon; t += Pas)
        {
            if (Interpoler(points, t) is not { } v) return null;
            somme += v;
            n++;
        }

        return Math.Round(somme / n, 2);
    }

    /// <summary>
    /// Le minimum prévu entre l'heure en cours et <paramref name="maintenant"/> + <paramref name="horizon"/> (les
    /// points horaires eux-mêmes : c'est là que sont les creux). Null sans aucun point à venir.
    /// </summary>
    public static double? MinimumAhead(JsonElement valeur, DateTimeOffset maintenant, TimeSpan horizon)
    {
        var points = Lire(valeur).Where(p => p.Item1 > maintenant.AddHours(-1) && p.Item1 <= maintenant + horizon).ToList();
        return points.Any(p => p.Item1 > maintenant) ? points.Min(p => p.Item2) : null;
    }

    private static double? Interpoler(List<(DateTimeOffset Quand, double Temperature)> points, DateTimeOffset t)
    {
        for (var i = 0; i < points.Count - 1; i++)
        {
            var (a, ta) = points[i];
            var (b, tb) = points[i + 1];
            if (t < a || t > b) continue;
            var part = (t - a).TotalSeconds / (b - a).TotalSeconds;
            return ta + (tb - ta) * part;
        }

        return null;
    }

    private static List<(DateTimeOffset, double)> Lire(JsonElement valeur)
    {
        var points = new List<(DateTimeOffset, double)>();
        if (valeur.ValueKind != JsonValueKind.Object || !valeur.TryGetProperty("hourly", out var serie) || serie.ValueKind != JsonValueKind.Array)
            return points;

        foreach (var p in serie.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("time", out var quand) && quand.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(quand.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                && p.TryGetProperty("temperature", out var temp) && temp.ValueKind == JsonValueKind.Number)
                points.Add((date, temp.GetDouble()));
        }

        points.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        // Deux points à la même heure rendraient l'interpolation indéfinie : on garde le premier.
        return points.Where((p, i) => i == 0 || p.Item1 != points[i - 1].Item1).ToList();
    }
}
