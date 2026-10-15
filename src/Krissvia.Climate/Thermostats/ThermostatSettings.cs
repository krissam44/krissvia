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
/// Créneau d'une journée. <see cref="End"/> à 00:00 veut dire « fin de journée » (minuit du soir). Une fin
/// antérieure au début (22:00 → 06:00) fait un créneau de nuit : actif de 00:00 à la fin ET du début à
/// minuit, sur le MÊME jour (comportement de DomoMud, C11).
/// </summary>
public sealed record ScheduleSlot(TimeSpan Start, TimeSpan End, ThermostatMode Mode)
{
    private TimeSpan FinEffective => End == TimeSpan.Zero ? TimeSpan.FromHours(24) : End;

    public bool IsActiveAt(TimeSpan heure)
    {
        var fin = FinEffective;
        return fin < Start
            ? heure >= Start || heure < fin
            : heure >= Start && heure < fin;
    }
}

/// <summary>Planning hebdomadaire : créneaux par jour, mode par défaut hors créneau.</summary>
public sealed record WeeklySchedule
{
    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>> Days { get; init; } =
        new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>();

    public ThermostatMode DefaultMode { get; init; } = ThermostatMode.Eco;

    /// <summary>
    /// Mode à l'heure LOCALE donnée. Deux créneaux qui se chevauchent : le plus matinal gagne (C12, la saisie
    /// avertit de ces chevauchements).
    /// </summary>
    public ThermostatMode ModeAt(DateTime heureLocale)
    {
        if (!Days.TryGetValue(heureLocale.DayOfWeek, out var creneaux)) return DefaultMode;

        var heure = heureLocale.TimeOfDay;
        foreach (var creneau in creneaux.OrderBy(c => c.Start))
            if (creneau.IsActiveAt(heure)) return creneau.Mode;

        return DefaultMode;
    }
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
    }
}
