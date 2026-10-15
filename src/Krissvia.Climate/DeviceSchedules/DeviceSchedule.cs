using Krissvia.Core.Scheduling;

namespace Krissvia.Climate.DeviceSchedules;

/// <summary>Créneau d'une programmation : allumé (par défaut) ou éteint, de <see cref="Start"/> à <see cref="End"/>.</summary>
public sealed record DeviceScheduleSlot(TimeSpan Start, TimeSpan End, bool On = true) : ITimeSlot;

/// <summary>
/// Programmation hebdomadaire d'un ou plusieurs switches (une prise, un chauffe-eau…). Mêmes règles de créneaux que
/// les thermostats (<see cref="WeeklySlots"/>) : un créneau de nuit finit le lendemain matin.
/// </summary>
public sealed record DeviceSchedule
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Désactivée : elle ne pilote plus rien et ne retient plus l'état de ses switches.</summary>
    public bool Enabled { get; init; } = true;

    public required IReadOnlyList<string> SwitchEntityIds { get; init; }

    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<DeviceScheduleSlot>> Days { get; init; } =
        new Dictionary<DayOfWeek, IReadOnlyList<DeviceScheduleSlot>>();

    /// <summary>État hors créneau : éteint par défaut, les créneaux disent quand allumer.</summary>
    public bool DefaultOn { get; init; }

    /// <summary>Allumé ou éteint à l'heure LOCALE donnée.</summary>
    public bool StateAt(DateTime heureLocale) => WeeklySlots.Active(Days, heureLocale)?.On ?? DefaultOn;

    /// <summary>Prochain changement d'état après <paramref name="depuis"/>, à la minute, sur une semaine ; null s'il n'y en a pas.</summary>
    public DateTime? NextChange(DateTime depuis)
    {
        var debut = new DateTime(depuis.Year, depuis.Month, depuis.Day, depuis.Hour, depuis.Minute, 0, depuis.Kind);
        var actuel = StateAt(depuis);
        for (var t = debut.AddMinutes(1); t <= debut.AddDays(7); t = t.AddMinutes(1))
            if (StateAt(t) != actuel) return t;
        return null;
    }

    /// <summary>Lève une exception lisible pour une programmation inutilisable.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("programmation sans identifiant.");
        if (SwitchEntityIds.Count == 0) throw new ArgumentException($"{Id} : aucun équipement à piloter.");
        try
        {
            WeeklySlots.Validate(Days);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{Id} : {ex.Message}");
        }
    }
}
