namespace Krissvia.Core.History;

/// <summary>Un point d'une série : sa valeur à un instant ; null, un trou (entité injoignable).</summary>
public sealed record HistoryPoint(string Series, DateTimeOffset At, double? Value);

/// <summary>
/// Ce qui mérite d'être écrit. Un point par changement, mais <b>au plus un par minute et par série</b> : un Linky publie
/// toutes les 2 s, et 43 000 points par jour pour une courbe qu'on regarde à la minute rempliraient la carte SD du Pi.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Dans la minute qui suit un point, un changement attend ; s'il y en a plusieurs, le dernier gagne. Il part avec
/// son propre instant : la courbe dit quand la valeur a changé, pas quand on l'a écrite.</item>
/// <item>Une valeur inchangée ne s'écrit pas, sauf toutes les <see cref="Battement"/> : une courbe plate se distingue
/// alors d'un capteur qui ne publie plus (un trou), et une requête sur une heure récente trouve un point.</item>
/// <item>Un aller-retour dans la minute (A, B, puis A) ne laisse rien : la courbe à la minute n'avait pas à le voir.</item>
/// </list>
/// </remarks>
public sealed class HistorySampler
{
    public static readonly TimeSpan IntervalleMinimal = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Battement = TimeSpan.FromMinutes(30);

    private sealed class Etat
    {
        public bool Ecrit;
        public double? Valeur;
        public DateTimeOffset A;
        public bool EnAttente;
        public double? Attente;
        public DateTimeOffset AttenteA;
    }

    private readonly Dictionary<string, Etat> _series = new(StringComparer.Ordinal);
    private readonly Lock _verrou = new();

    public void Offer(string serie, DateTimeOffset at, double? valeur)
    {
        lock (_verrou)
        {
            if (!_series.TryGetValue(serie, out var e)) _series[serie] = e = new Etat();
            if (e.Ecrit && !e.EnAttente && Egal(valeur, e.Valeur)) return;
            e.EnAttente = true;
            e.Attente = valeur;
            e.AttenteA = at;
        }
    }

    /// <summary>Les séries déjà vues pour cette entité : un objet devenu injoignable passe toutes ses séries en trou.</summary>
    public IReadOnlyList<string> SeriesOf(string entityId)
    {
        var prefixe = entityId + ":";
        lock (_verrou)
            return [.. _series.Keys.Where(k => k == entityId || k.StartsWith(prefixe, StringComparison.Ordinal))];
    }

    /// <summary>Les points à écrire maintenant ; ils sont tenus pour écrits (l'appelant réessaie s'il échoue).</summary>
    public IReadOnlyList<HistoryPoint> TakeDue(DateTimeOffset maintenant)
    {
        var points = new List<HistoryPoint>();
        lock (_verrou)
        {
            foreach (var (serie, e) in _series)
            {
                if (e.EnAttente && (!e.Ecrit || maintenant - e.A >= IntervalleMinimal))
                {
                    e.EnAttente = false;
                    if (e.Ecrit && Egal(e.Attente, e.Valeur)) continue;
                    // Jamais avant le point précédent, même si l'horloge d'un plugin recule.
                    var at = e.Ecrit && e.AttenteA <= e.A ? e.A.AddSeconds(1) : e.AttenteA;
                    points.Add(new HistoryPoint(serie, at, e.Attente));
                    (e.Ecrit, e.Valeur, e.A) = (true, e.Attente, at);
                }
                else if (!e.EnAttente && e.Ecrit && e.Valeur is not null && maintenant - e.A >= Battement)
                {
                    points.Add(new HistoryPoint(serie, maintenant, e.Valeur));
                    e.A = maintenant;
                }
            }
        }

        return points;
    }

    /// <summary>Remet des points dont l'écriture a échoué : ils repartiront au prochain passage.</summary>
    public void Requeue(IReadOnlyList<HistoryPoint> points)
    {
        lock (_verrou)
        {
            foreach (var p in points)
            {
                if (!_series.TryGetValue(p.Series, out var e) || e.EnAttente) continue;
                (e.EnAttente, e.Attente, e.AttenteA, e.Ecrit) = (true, p.Value, p.At, false);
            }
        }
    }

    private static bool Egal(double? a, double? b) =>
        a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 1e-9;
}
