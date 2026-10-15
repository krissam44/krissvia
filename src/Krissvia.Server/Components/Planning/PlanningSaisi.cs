namespace Krissvia.Server.Components.Planning;

/// <summary>
/// Un créneau en cours de saisie. Heures et minutes sont choisies dans des listes : il n'y a rien à analyser, donc
/// aucune heure « incomplète » possible (le champ horaire de MudBlazor rendait « 07:30:00 » après une saisie).
/// </summary>
public sealed class CreneauSaisi<TValeur> : ITimeSlot
{
    public int HeureDebut { get; set; }
    public int MinuteDebut { get; set; }
    public int HeureFin { get; set; }
    public int MinuteFin { get; set; }
    public TValeur Valeur { get; set; } = default!;

    public TimeSpan Start => new(HeureDebut, MinuteDebut, 0);
    public TimeSpan End => new(HeureFin, MinuteFin, 0);

    public static CreneauSaisi<TValeur> Depuis(TimeSpan debut, TimeSpan fin, TValeur valeur) => new()
    {
        HeureDebut = debut.Hours, MinuteDebut = debut.Minutes, HeureFin = fin.Hours, MinuteFin = fin.Minutes, Valeur = valeur,
    };

    public CreneauSaisi<TValeur> Copie() => Depuis(Start, End, Valeur);
}

/// <summary>Un planning en cours de saisie : créneaux par jour et valeur hors créneau.</summary>
public sealed class PlanningSaisi<TValeur>
{
    public TValeur ParDefaut { get; set; } = default!;
    public Dictionary<DayOfWeek, List<CreneauSaisi<TValeur>>> Jours { get; } =
        Krissvia.Core.Scheduling.Jours.Semaine.ToDictionary(j => j, _ => new List<CreneauSaisi<TValeur>>());

    public static PlanningSaisi<TValeur> Depuis<TSlot>(
        IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TSlot>> jours, TValeur parDefaut, Func<TSlot, TValeur> valeur) where TSlot : ITimeSlot
    {
        var p = new PlanningSaisi<TValeur> { ParDefaut = parDefaut };
        foreach (var (jour, creneaux) in jours)
            p.Jours[jour].AddRange(creneaux.OrderBy(c => c.Start).Select(c => CreneauSaisi<TValeur>.Depuis(c.Start, c.End, valeur(c))));
        return p;
    }

    /// <summary>Les créneaux saisis, convertis vers le type du planning à enregistrer ; les jours vides sont omis.</summary>
    public Dictionary<DayOfWeek, IReadOnlyList<TSlot>> Vers<TSlot>(Func<TimeSpan, TimeSpan, TValeur, TSlot> creer) =>
        Jours.Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TSlot>)kv.Value.OrderBy(c => c.Start).Select(c => creer(c.Start, c.End, c.Valeur)).ToList());

    /// <summary>La vue que les règles communes savent lire.</summary>
    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<CreneauSaisi<TValeur>>> Lecture() =>
        Jours.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CreneauSaisi<TValeur>>)kv.Value);
}
