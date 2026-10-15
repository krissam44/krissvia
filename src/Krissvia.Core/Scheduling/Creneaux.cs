namespace Krissvia.Core.Scheduling;

/// <summary>
/// Un créneau d'un jour de la semaine. <see cref="End"/> à 00:00 veut dire « fin de journée » (minuit du soir) ; une
/// fin antérieure au début (22:00 → 06:00) fait un créneau de NUIT, qui commence le soir de son jour et finit le
/// LENDEMAIN matin.
/// </summary>
/// <remarks>
/// Une seule règle pour tous les plannings (thermostats, équipements…). DomoMud en avait deux copies, et elles ont
/// divergé : les créneaux de nuit des équipements ne s'activaient jamais pendant que ceux des thermostats marchaient.
/// </remarks>
public interface ITimeSlot
{
    TimeSpan Start { get; }
    TimeSpan End { get; }
}

public static class TimeSlots
{
    private static readonly TimeSpan Jour = TimeSpan.FromHours(24);

    private static TimeSpan FinEffective(ITimeSlot c) => c.End == TimeSpan.Zero ? Jour : c.End;

    /// <summary>Finit le lendemain matin.</summary>
    public static bool CrossesMidnight(this ITimeSlot c) => FinEffective(c) < c.Start;

    /// <summary>Durée réelle, nuit comprise.</summary>
    public static TimeSpan Duration(this ITimeSlot c) => c.CrossesMidnight() ? Jour - c.Start + c.End : FinEffective(c) - c.Start;

    /// <summary>Actif à cette heure de SON jour (la partie du soir pour un créneau de nuit).</summary>
    public static bool IsActiveOnItsDay(this ITimeSlot c, TimeSpan heure) =>
        heure >= c.Start && heure < (c.CrossesMidnight() ? Jour : FinEffective(c));

    /// <summary>Actif à cette heure du LENDEMAIN (le matin d'un créneau de nuit).</summary>
    public static bool IsActiveNextMorning(this ITimeSlot c, TimeSpan heure) => c.CrossesMidnight() && heure < c.End;
}

/// <summary>Deux créneaux qui se recouvrent : celui qui l'emporte (commencé le premier) et l'autre.</summary>
public sealed record SlotOverlap<TSlot>(DayOfWeek WinnerDay, TSlot Winner, DayOfWeek LoserDay, TSlot Loser) where TSlot : ITimeSlot;

/// <summary>Les règles d'une semaine de créneaux, communes à tous les plannings.</summary>
public static class WeeklySlots
{
    private static readonly TimeSpan Semaine = TimeSpan.FromDays(7);

    /// <summary>
    /// Le créneau actif à l'heure LOCALE donnée, ou null. Deux créneaux qui se chevauchent : celui qui a commencé le
    /// premier gagne — donc la fin d'une nuit de la veille avant les créneaux du jour, puis le plus matinal.
    /// </summary>
    public static TSlot? Active<TSlot>(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TSlot>> jours, DateTime heureLocale)
        where TSlot : class, ITimeSlot
    {
        var heure = heureLocale.TimeOfDay;
        if (jours.TryGetValue(Jours.Veille(heureLocale.DayOfWeek), out var hier))
            foreach (var creneau in hier.OrderBy(c => c.Start))
                if (creneau.IsActiveNextMorning(heure)) return creneau;

        if (jours.TryGetValue(heureLocale.DayOfWeek, out var creneaux))
            foreach (var creneau in creneaux.OrderBy(c => c.Start))
                if (creneau.IsActiveOnItsDay(heure)) return creneau;

        return null;
    }

    /// <summary>
    /// Les chevauchements que perd un créneau de ce jour, y compris contre la nuit de la veille. Pas une erreur (le
    /// premier commencé gagne), mais presque toujours une faute de saisie : les écrans les signalent.
    /// </summary>
    public static IReadOnlyList<SlotOverlap<TSlot>> Overlaps<TSlot>(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TSlot>> jours, DayOfWeek jour)
        where TSlot : ITimeSlot
    {
        // Chaque créneau posé sur la semaine, en temps écoulé depuis lundi 00:00 ; la nuit de dimanche déborde sur
        // le lundi suivant, d'où la comparaison aussi à une semaine d'écart.
        var poses = Jours.Semaine
            .SelectMany(j => jours.TryGetValue(j, out var cs) ? cs.Select(c => (Jour: j, Creneau: c)) : [])
            .Select(x => (x.Jour, x.Creneau, Debut: TimeSpan.FromDays(Jours.Index(x.Jour)) + x.Creneau.Start))
            .ToList();

        var resultat = new List<SlotOverlap<TSlot>>();
        for (var i = 0; i < poses.Count; i++)
        {
            for (var j = i + 1; j < poses.Count; j++)
            {
                var a = poses[i];
                var b = poses[j];
                foreach (var decalage in new[] { TimeSpan.Zero, Semaine, -Semaine })
                {
                    var debutB = b.Debut + decalage;
                    if (!(a.Debut < debutB + b.Creneau.Duration() && debutB < a.Debut + a.Creneau.Duration())) continue;

                    // Même départ : l'ordre d'Active (tri stable par heure de début) départage, soit l'ordre de saisie.
                    var (gagnant, perdant) = a.Debut <= debutB ? (a, b) : (b, a);
                    if (perdant.Jour == jour) resultat.Add(new SlotOverlap<TSlot>(gagnant.Jour, gagnant.Creneau, perdant.Jour, perdant.Creneau));
                    break;
                }
            }
        }

        return resultat;
    }

    /// <summary>Lève une exception lisible pour un créneau hors de la journée ou de durée nulle.</summary>
    public static void Validate<TSlot>(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TSlot>> jours) where TSlot : ITimeSlot
    {
        foreach (var (jour, creneaux) in jours)
        {
            foreach (var c in creneaux)
            {
                if (c.Start < TimeSpan.Zero || c.Start >= TimeSpan.FromHours(24) || c.End < TimeSpan.Zero || c.End >= TimeSpan.FromHours(24))
                    throw new ArgumentException($"planning, {Jours.Nom(jour)} : heures entre 00:00 et 23:59.");
                if (c.Start == c.End && c.End != TimeSpan.Zero)
                    throw new ArgumentException($"planning, {Jours.Nom(jour)} : un créneau commence et finit à {c.Start:hh\\:mm}, il ne durerait rien.");
            }
        }
    }
}

/// <summary>Noms des jours en français, pour les messages : <see cref="DayOfWeek"/> ne parle qu'anglais.</summary>
public static class Jours
{
    /// <summary>Du lundi au dimanche : l'ordre d'affichage.</summary>
    public static readonly DayOfWeek[] Semaine =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>Rang dans la semaine, lundi = 0.</summary>
    public static int Index(DayOfWeek jour) => ((int)jour + 6) % 7;

    public static DayOfWeek Veille(DayOfWeek jour) => (DayOfWeek)(((int)jour + 6) % 7);

    public static DayOfWeek Lendemain(DayOfWeek jour) => (DayOfWeek)(((int)jour + 1) % 7);

    public static string Nom(DayOfWeek jour) => jour switch
    {
        DayOfWeek.Monday => "lundi",
        DayOfWeek.Tuesday => "mardi",
        DayOfWeek.Wednesday => "mercredi",
        DayOfWeek.Thursday => "jeudi",
        DayOfWeek.Friday => "vendredi",
        DayOfWeek.Saturday => "samedi",
        _ => "dimanche",
    };
}
