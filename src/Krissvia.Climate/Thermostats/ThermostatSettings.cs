using System.Text.Json.Serialization;

namespace Krissvia.Climate.Thermostats;

/// <summary>Modes de chauffe (décret français de 2012), du plus froid au plus chaud.</summary>
public enum ThermostatMode
{
    Off = 0,
    FrostProtection = 1,
    Eco = 2,
    Comfort2 = 3,
    Comfort1 = 4,
    Comfort = 5,
}

/// <summary>
/// Créneau d'un jour. <see cref="End"/> à 00:00 veut dire « fin de journée » (minuit du soir). Une fin antérieure
/// au début (22:00 → 06:00) fait un créneau de NUIT : il commence le soir de son jour et finit le LENDEMAIN matin.
/// </summary>
/// <remarks>
/// DomoMud comprenait l'inverse (C11) : le soir ET le matin du même jour, si bien qu'une nuit du lundi au mardi
/// demandait deux créneaux, lundi 22:00 → 00:00 et mardi 00:00 → 06:00. Changé le 15/10/2026 à la demande de
/// l'utilisateur ; l'import des plannings DomoMud devra convertir.
/// </remarks>
public sealed record ScheduleSlot(TimeSpan Start, TimeSpan End, ThermostatMode Mode)
{
    private static readonly TimeSpan Jour = TimeSpan.FromHours(24);

    private TimeSpan FinEffective => End == TimeSpan.Zero ? Jour : End;

    /// <summary>Finit le lendemain matin. Déduit, donc jamais écrit en base ni dans l'API.</summary>
    [JsonIgnore]
    public bool CrossesMidnight => FinEffective < Start;

    /// <summary>Durée réelle, nuit comprise.</summary>
    [JsonIgnore]
    public TimeSpan Duration => CrossesMidnight ? Jour - Start + End : FinEffective - Start;

    /// <summary>Actif à cette heure de SON jour (la partie du soir pour un créneau de nuit).</summary>
    internal bool IsActiveOnItsDay(TimeSpan heure) => heure >= Start && heure < (CrossesMidnight ? Jour : FinEffective);

    /// <summary>Actif à cette heure du LENDEMAIN (le matin d'un créneau de nuit).</summary>
    internal bool IsActiveNextMorning(TimeSpan heure) => CrossesMidnight && heure < End;
}

/// <summary>Deux créneaux qui se recouvrent : celui qui l'emporte (commencé le premier) et l'autre.</summary>
public sealed record ScheduleOverlap(DayOfWeek WinnerDay, ScheduleSlot Winner, DayOfWeek LoserDay, ScheduleSlot Loser);

/// <summary>Planning hebdomadaire : créneaux par jour, mode par défaut hors créneau.</summary>
public sealed record WeeklySchedule
{
    private static readonly TimeSpan Semaine = TimeSpan.FromDays(7);

    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>> Days { get; init; } =
        new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>();

    public ThermostatMode DefaultMode { get; init; } = ThermostatMode.Eco;

    /// <summary>
    /// Mode à l'heure LOCALE donnée. Deux créneaux qui se chevauchent : celui qui a commencé le premier gagne (C12,
    /// la saisie avertit de ces chevauchements) — donc la fin d'une nuit de la veille avant les créneaux du jour.
    /// </summary>
    public ThermostatMode ModeAt(DateTime heureLocale)
    {
        var heure = heureLocale.TimeOfDay;
        if (Days.TryGetValue(Veille(heureLocale.DayOfWeek), out var hier))
            foreach (var creneau in hier.OrderBy(c => c.Start))
                if (creneau.IsActiveNextMorning(heure)) return creneau.Mode;

        if (Days.TryGetValue(heureLocale.DayOfWeek, out var creneaux))
            foreach (var creneau in creneaux.OrderBy(c => c.Start))
                if (creneau.IsActiveOnItsDay(heure)) return creneau.Mode;

        return DefaultMode;
    }

    /// <summary>
    /// Les chevauchements que perd un créneau de ce jour, y compris contre la nuit de la veille. Pas une erreur (le
    /// premier commencé gagne, C12), mais presque toujours une faute de saisie : l'écran les signale.
    /// </summary>
    public IReadOnlyList<ScheduleOverlap> Overlaps(DayOfWeek jour)
    {
        // Chaque créneau posé sur la semaine, en temps écoulé depuis lundi 00:00 ; la nuit de dimanche déborde sur
        // le lundi suivant, d'où la comparaison aussi à une semaine d'écart.
        var poses = Jours.Semaine
            .SelectMany(j => Days.TryGetValue(j, out var cs) ? cs.Select(c => (Jour: j, Creneau: c)) : [])
            .Select(x => (x.Jour, x.Creneau, Debut: TimeSpan.FromDays(Jours.Index(x.Jour)) + x.Creneau.Start))
            .ToList();

        var resultat = new List<ScheduleOverlap>();
        for (var i = 0; i < poses.Count; i++)
        {
            for (var j = i + 1; j < poses.Count; j++)
            {
                var a = poses[i];
                var b = poses[j];
                foreach (var decalage in new[] { TimeSpan.Zero, Semaine, -Semaine })
                {
                    var debutB = b.Debut + decalage;
                    if (!(a.Debut < debutB + b.Creneau.Duration && debutB < a.Debut + a.Creneau.Duration)) continue;

                    // Même départ : l'ordre de ModeAt (tri stable par heure de début) départage, soit l'ordre de saisie.
                    var (gagnant, perdant) = a.Debut <= debutB ? (a, b) : (b, a);
                    if (perdant.Jour == jour) resultat.Add(new ScheduleOverlap(gagnant.Jour, gagnant.Creneau, perdant.Jour, perdant.Creneau));
                    break;
                }
            }
        }

        return resultat;
    }

    private static DayOfWeek Veille(DayOfWeek jour) => (DayOfWeek)(((int)jour + 6) % 7);

    /// <summary>Lève une exception lisible pour un créneau hors de la journée ou de durée nulle.</summary>
    public void Validate()
    {
        foreach (var (jour, creneaux) in Days)
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

/// <summary>Paramètres de la régulation TPI (valeurs par défaut de DomoMud).</summary>
public sealed record TpiSettings
{
    /// <summary>Proportionnel : puissance par °C d'écart à la consigne.</summary>
    public double Kp { get; init; } = 0.6;

    /// <summary>Feed-forward extérieur : puissance par °C entre la consigne et l'extérieur. 0 = sans.</summary>
    public double Ki { get; init; } = 0.01;

    /// <summary>Intégral, sur l'erreur cumulée en °C·h. 0 = régulateur proportionnel pur.</summary>
    public double Kint { get; init; } = 0.02;

    /// <summary>Borne de l'intégrale (anti-emballement), en °C·h.</summary>
    public double IntegralMax { get; init; } = 2.0;

    public int CycleSeconds { get; init; } = 600;

    /// <summary>En dessous, pas de chauffe (micro-cycle), sauf sous la consigne où on chauffe ce minimum.</summary>
    public int MinOnSeconds { get; init; } = 30;

    /// <summary>Un arrêt plus court que ça devient un cycle plein.</summary>
    public int MinOffSeconds { get; init; } = 30;

    /// <summary>Au-dessus de consigne + ce seuil : on coupe et on laisse l'inertie faire. 0 = désactivé.</summary>
    public double CoastingHigh { get; init; } = 1.0;

    /// <summary>On reprend la régulation une fois redescendu à consigne + ce seuil.</summary>
    public double CoastingLow { get; init; } = 0.3;
}

/// <summary>Configuration d'un thermostat. Toutes les références visent des entités du registre.</summary>
public sealed record ThermostatSettings
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Sonde intérieure (sensor, °C).</summary>
    public required string TemperatureEntityId { get; init; }

    /// <summary>Radiateurs : switches pilotés ensemble.</summary>
    public required IReadOnlyList<string> SwitchEntityIds { get; init; }

    /// <summary>Sonde extérieure, pour le feed-forward. Facultative.</summary>
    public string? OutdoorTemperatureEntityId { get; init; }

    /// <summary>Capteur d'ouverture (binary_sensor, true = ouvert). Facultatif.</summary>
    public string? OpeningEntityId { get; init; }

    public double ComfortTemperature { get; init; } = 20.0;
    public double EcoTemperature { get; init; } = 16.5;
    public double FrostProtectionTemperature { get; init; } = 7.0;

    public ThermostatMode ManualMode { get; init; } = ThermostatMode.Eco;
    public bool ScheduleActive { get; init; }
    public WeeklySchedule Schedule { get; init; } = new();

    /// <summary>Consigne ajustée à la main (+/-), prioritaire sur le mode tant qu'aucun mode n'est rechoisi.</summary>
    public double? TargetOverride { get; init; }

    public TpiSettings Tpi { get; init; } = new();

    /// <summary>Sonde muette depuis plus longtemps : chauffage coupé jusqu'à son retour. 0 = désactivé.</summary>
    public int FailsafeMinutes { get; init; } = 60;

    /// <summary>Fenêtre ouverte depuis ce délai : chauffage suspendu.</summary>
    public int OpeningDelayMinutes { get; init; } = 2;

    /// <summary>Fenêtre refermée depuis ce délai : chauffage repris.</summary>
    public int ClosingDelayMinutes { get; init; } = 1;

    public ThermostatMode ModeAt(DateTime heureLocale) => ScheduleActive ? Schedule.ModeAt(heureLocale) : ManualMode;

    public double TargetFor(ThermostatMode mode) => mode switch
    {
        ThermostatMode.Comfort => ComfortTemperature,
        ThermostatMode.Comfort1 => ComfortTemperature - 1,
        ThermostatMode.Comfort2 => ComfortTemperature - 2,
        ThermostatMode.Eco => EcoTemperature,
        _ => FrostProtectionTemperature,
    };

    /// <summary>Lève une exception lisible pour une configuration qui ne peut pas réguler.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("Thermostat sans identifiant.");
        if (string.IsNullOrWhiteSpace(TemperatureEntityId)) throw new ArgumentException($"{Id} : sonde intérieure manquante.");
        if (SwitchEntityIds.Count == 0) throw new ArgumentException($"{Id} : aucun radiateur à piloter.");
        if (Tpi.CycleSeconds <= 0) throw new ArgumentException($"{Id} : durée de cycle invalide.");
        if (Tpi.CoastingHigh > 0 && Tpi.CoastingLow >= Tpi.CoastingHigh)
            throw new ArgumentException($"{Id} : le seuil bas d'inertie doit être inférieur au seuil haut.");
        if (FailsafeMinutes < 0 || OpeningDelayMinutes < 0 || ClosingDelayMinutes < 0)
            throw new ArgumentException($"{Id} : délai négatif.");
        try
        {
            Schedule.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{Id} : {ex.Message}");
        }
    }
}
