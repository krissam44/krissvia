using System.Text.Json.Serialization;
using Krissvia.Core.Scheduling;

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
/// Créneau d'un planning de thermostat : un mode de <see cref="Start"/> à <see cref="End"/>. Les règles (fin à minuit,
/// nuit qui finit le lendemain, chevauchements) sont celles de tous les plannings : <see cref="WeeklySlots"/>.
/// </summary>
/// <remarks>
/// DomoMud comprenait un créneau de nuit comme le soir ET le matin du même jour (C11). Changé le 15/10/2026 à la
/// demande de l'utilisateur ; l'import des plannings DomoMud devra convertir.
/// </remarks>
public sealed record ScheduleSlot(TimeSpan Start, TimeSpan End, ThermostatMode Mode) : ITimeSlot
{
    /// <summary>Finit le lendemain matin. Déduit, donc jamais écrit en base ni dans l'API.</summary>
    [JsonIgnore]
    public bool CrossesMidnight => this.CrossesMidnight();
}

/// <summary>Planning hebdomadaire d'un thermostat : créneaux par jour, mode par défaut hors créneau.</summary>
public sealed record WeeklySchedule
{
    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>> Days { get; init; } =
        new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>();

    public ThermostatMode DefaultMode { get; init; } = ThermostatMode.Eco;

    /// <summary>Mode à l'heure LOCALE donnée (C12 : en chevauchement, le premier commencé gagne).</summary>
    public ThermostatMode ModeAt(DateTime heureLocale) => WeeklySlots.Active(Days, heureLocale)?.Mode ?? DefaultMode;

    /// <summary>Les chevauchements que perd un créneau de ce jour, nuit de la veille comprise.</summary>
    public IReadOnlyList<SlotOverlap<ScheduleSlot>> Overlaps(DayOfWeek jour) => WeeklySlots.Overlaps(Days, jour);

    /// <summary>Lève une exception lisible pour un créneau hors de la journée ou de durée nulle.</summary>
    public void Validate() => WeeklySlots.Validate(Days);
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

    /// <summary>
    /// Entité météo d'un lieu (sensor <c>weather</c> du plugin Météo), dont la prévision horaire sert à anticiper :
    /// le feed-forward prend la moyenne prévue sur 2 h au lieu de la mesure. Facultative.
    /// </summary>
    public string? ForecastEntityId { get; init; }

    /// <summary>
    /// Anticipation météo active. Vrai par défaut ; l'éteindre garde l'entité choisie, pour comparer avec et sans
    /// (l'interrupteur de DomoMud).
    /// </summary>
    public bool ForecastAnticipation { get; init; } = true;

    /// <summary>
    /// Préchauffage anticipé : avec le planning actif, chauffer avant un créneau plus chaud pour que sa consigne
    /// soit atteinte à l'heure du créneau (pente de chauffe apprise, corrigée par la prévision météo). Vrai par
    /// défaut, comme dans DomoMud.
    /// </summary>
    public bool PreheatEnabled { get; init; } = true;

    /// <summary>
    /// Suit l'absence de la maison (détection de présence, absence programmée, forçage) : passe alors dans
    /// <see cref="AwayMode"/>, et remonte avant un retour programmé. Au choix de chaque thermostat, comme dans
    /// DomoMud : une salle de bain y gagne, un séjour à forte inertie peut préférer garder son planning. Faux par défaut.
    /// </summary>
    public bool FollowAbsence { get; init; }

    /// <summary>Mode pendant l'absence. Hors-gel par défaut ; Off n'empêche pas la remontée avant le retour.</summary>
    public ThermostatMode AwayMode { get; init; } = ThermostatMode.FrostProtection;

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
