using System.Text.Json;
using Krissvia.Climate.Presence;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.Events;
using Krissvia.Core.States;

namespace Krissvia.Climate.Automations;

/// <summary>Ce qui met un automatisme en route (les trois déclencheurs de DomoMud).</summary>
public enum TriggerType
{
    /// <summary>Appui sur un bouton ou une télécommande : une entité <c>event</c> publie son action.</summary>
    Button,

    /// <summary>Heure fixe, sur les jours choisis (aucun : tous les jours).</summary>
    Time,

    /// <summary>Front d'une entité tout-ou-rien : radar qui détecte, porte qui s'ouvre, lampe qui s'allume.</summary>
    StateChange,
}

/// <summary>Le front écouté par un déclencheur <see cref="TriggerType.StateChange"/>.</summary>
public enum StateTransition
{
    /// <summary>Passe à actif : détection, ouverture, allumage.</summary>
    Activated,

    /// <summary>Repasse à repos.</summary>
    Deactivated,

    Any,
}

public sealed record AutomationTrigger
{
    public TriggerType Type { get; init; } = TriggerType.Button;

    /// <summary>Bouton : l'entité <c>event</c> ; changement d'état : l'entité tout-ou-rien.</summary>
    public string? EntityId { get; init; }

    /// <summary>Bouton : l'action attendue (« single », « double »…) ; vide : n'importe laquelle.</summary>
    public string? Action { get; init; }

    /// <summary>Horaire : l'heure locale (heures et minutes).</summary>
    public TimeSpan Time { get; init; }

    /// <summary>Horaire : les jours ; vide = tous, pour qu'un automatisme créé sans y penser parte quand même.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public StateTransition Transition { get; init; } = StateTransition.Activated;
}

/// <summary>Les conditions (toutes doivent être vraies) : celles de DomoMud, et des seuils sur les entités.</summary>
public enum ConditionType
{
    HomeOccupied,
    HomeEmpty,

    /// <summary>Plage horaire ; fin avant début = plage de nuit (22:00 → 06:00), comme les plannings.</summary>
    TimeRange,

    /// <summary>Le mode effectif d'un thermostat (absence comprise).</summary>
    ThermostatMode,

    /// <summary>Une entité tout-ou-rien est active : lampe allumée, porte ouverte, présence.</summary>
    EntityActive,

    /// <summary>Une mesure est au-dessus du seuil (strictement).</summary>
    Above,

    /// <summary>Une mesure est en dessous du seuil (strictement).</summary>
    Below,
}

public sealed record AutomationCondition
{
    public ConditionType Type { get; init; } = ConditionType.HomeOccupied;

    /// <summary>Inverse le résultat — mais une donnée inconnue reste « non satisfaite », inversée ou non.</summary>
    public bool Negated { get; init; }

    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string? ThermostatId { get; init; }
    public ThermostatMode Mode { get; init; } = ThermostatMode.Comfort;
    public string? EntityId { get; init; }
    public double Threshold { get; init; }
}

public enum ActionType
{
    TurnOn,
    TurnOff,
    Toggle,

    /// <summary>Choisit le mode d'un thermostat, comme le bouton de sa carte (le planning est suspendu).</summary>
    ThermostatMode,

    /// <summary>Envoie une notification ntfy, à chaque déclenchement (pas une alerte dédoublonnée).</summary>
    Notify,
}

public sealed record AutomationAction
{
    public ActionType Type { get; init; } = ActionType.TurnOn;
    public string? EntityId { get; init; }
    public string? ThermostatId { get; init; }
    public ThermostatMode Mode { get; init; } = ThermostatMode.Comfort;
    public string? Title { get; init; }
    public string? Message { get; init; }

    /// <summary>Attente avant cette action (« éteindre 10 min après ») ; 0 : tout de suite.</summary>
    public int DelaySeconds { get; init; }
}

/// <summary>Un automatisme : un déclencheur, des conditions (ET), des actions exécutées dans l'ordre.</summary>
public sealed record Automation
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public AutomationTrigger Trigger { get; init; } = new();
    public IReadOnlyList<AutomationCondition> Conditions { get; init; } = [];
    public IReadOnlyList<AutomationAction> Actions { get; init; } = [];

    /// <summary>Plus long délai accepté avant une action : une journée.</summary>
    public const int MaxDelaySeconds = 86_400;

    /// <summary>Lève une exception lisible pour un automatisme incomplet. Les références sont vérifiées par le service.</summary>
    public void Validate()
    {
        if (Actions.Count == 0) throw new ArgumentException("aucune action.");
        switch (Trigger.Type)
        {
            case TriggerType.Button or TriggerType.StateChange when string.IsNullOrWhiteSpace(Trigger.EntityId):
                throw new ArgumentException("déclencheur sans entité.");
            case TriggerType.Time when Trigger.Time < TimeSpan.Zero || Trigger.Time >= TimeSpan.FromDays(1):
                throw new ArgumentException("heure du déclencheur hors de la journée.");
        }

        foreach (var c in Conditions)
        {
            if (c.Type == ConditionType.TimeRange && (c.Start < TimeSpan.Zero || c.Start >= TimeSpan.FromDays(1) || c.End < TimeSpan.Zero || c.End > TimeSpan.FromDays(1) || c.Start == c.End))
                throw new ArgumentException("plage horaire invalide (début et fin dans la journée, différents).");
            if (c.Type == ConditionType.ThermostatMode && string.IsNullOrWhiteSpace(c.ThermostatId))
                throw new ArgumentException("condition sur un thermostat sans thermostat.");
            if (c.Type is ConditionType.EntityActive or ConditionType.Above or ConditionType.Below && string.IsNullOrWhiteSpace(c.EntityId))
                throw new ArgumentException("condition sans entité.");
        }

        foreach (var a in Actions)
        {
            if (a.DelaySeconds is < 0 or > MaxDelaySeconds) throw new ArgumentException("délai d'une action : de 0 à 24 h.");
            if (a.Type is ActionType.TurnOn or ActionType.TurnOff or ActionType.Toggle && string.IsNullOrWhiteSpace(a.EntityId))
                throw new ArgumentException("action sans équipement.");
            if (a.Type == ActionType.ThermostatMode && string.IsNullOrWhiteSpace(a.ThermostatId))
                throw new ArgumentException("action sans thermostat.");
            if (a.Type == ActionType.Notify && string.IsNullOrWhiteSpace(a.Title) && string.IsNullOrWhiteSpace(a.Message))
                throw new ArgumentException("notification sans titre ni message.");
        }
    }
}

/// <summary>Ce dont les conditions ont besoin, à un instant donné.</summary>
public sealed record ConditionContext(
    DateTime LocalTime,
    HomeAbsenceState Absence,
    Func<string, ThermostatMode?> ModeOf,
    IEntityStateReader States);

/// <summary>
/// Les règles des automatismes, en fonctions pures : quand un déclencheur part, quand une condition est vraie. C'est
/// là que se nichent les erreurs coûteuses (une lumière rallumée à chaque redémarrage, un chauffe-eau lancé trois heures
/// en retard) : tout se teste sans moteur ni horloge.
/// </summary>
public static class AutomationRules
{
    /// <summary>
    /// Âge maximal d'un appui pour le prendre en compte. Un plugin qui se reconnecte rejoue ses derniers états : un appui
    /// d'il y a une heure n'est pas un appui maintenant.
    /// </summary>
    public static readonly TimeSpan MaxButtonAge = TimeSpan.FromMinutes(2);

    /// <summary>Le changement d'état <paramref name="e"/> déclenche-t-il <paramref name="t"/> ?</summary>
    public static bool Fires(AutomationTrigger t, EntityStateChanged e, DateTimeOffset maintenant)
    {
        if (t.EntityId != e.EntityId) return false;

        switch (t.Type)
        {
            case TriggerType.Button:
                // Un changement de disponibilité seul (même mesure, même instant) n'est pas un appui.
                if (e.Previous is { } p && p.MeasuredAt == e.Current.MeasuredAt && !e.ValueChanged) return false;
                if (!e.Current.Available || maintenant - e.Current.MeasuredAt > MaxButtonAge) return false;
                if (e.Current.Value.ValueKind != JsonValueKind.String || e.Current.Value.GetString() is not { Length: > 0 } action) return false;
                return string.IsNullOrWhiteSpace(t.Action) || string.Equals(t.Action.Trim(), action, StringComparison.OrdinalIgnoreCase);

            case TriggerType.StateChange:
                // État précédent inconnu : ce n'est pas un front. Sinon chaque redémarrage rallumerait les lumières.
                if (Active(e.Previous) is not { } avant || Active(e.Current) is not { } apres || avant == apres) return false;
                return t.Transition switch
                {
                    StateTransition.Activated => apres,
                    StateTransition.Deactivated => !apres,
                    _ => true,
                };

            default:
                return false;
        }
    }

    /// <summary>
    /// Un déclencheur horaire tombe-t-il sur cette minute locale ? Pas de rattrapage : un serveur arrêté à l'heure dite
    /// saute l'occurrence (allumer un chauffe-eau trois heures en retard est pire que le manquer).
    /// </summary>
    public static bool TimeMatches(AutomationTrigger t, DateTime heureLocale) =>
        t.Type == TriggerType.Time
        && heureLocale.Hour == t.Time.Hours && heureLocale.Minute == t.Time.Minutes
        && (t.Days.Count == 0 || t.Days.Contains(heureLocale.DayOfWeek));

    /// <summary>Actif (allumé, ouvert, détecté), éteint, ou inconnu (absent, indisponible, pas un tout-ou-rien).</summary>
    public static bool? Active(EntityStateSnapshot? s)
    {
        if (s is not { Available: true }) return null;
        if (s.TryGetBool(out var b)) return b;
        // Une lumière publie un objet {on, brightness}.
        return s.Value.ValueKind == JsonValueKind.Object && s.Value.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? on.ValueKind == JsonValueKind.True
            : null;
    }

    /// <summary>Toutes satisfaites (une liste vide l'est) ; sinon la première qui ne l'est pas.</summary>
    public static AutomationCondition? FirstUnmet(IEnumerable<AutomationCondition> conditions, ConditionContext ctx) =>
        conditions.FirstOrDefault(c => !Satisfied(c, ctx));

    public static bool Satisfied(AutomationCondition c, ConditionContext ctx) =>
        Evaluate(c, ctx) is { } brut && brut != c.Negated;

    /// <summary>Null : impossible à dire (mesure absente, thermostat inconnu). Jamais satisfaite, même inversée.</summary>
    private static bool? Evaluate(AutomationCondition c, ConditionContext ctx) => c.Type switch
    {
        ConditionType.HomeOccupied => !ctx.Absence.Away,
        ConditionType.HomeEmpty => ctx.Absence.Away,
        ConditionType.TimeRange => InRange(c.Start, c.End, ctx.LocalTime.TimeOfDay),
        ConditionType.ThermostatMode => c.ThermostatId is { } id && ctx.ModeOf(id) is { } mode ? mode == c.Mode : null,
        ConditionType.EntityActive => c.EntityId is { } e ? Active(ctx.States.Get(e)) : null,
        ConditionType.Above => Number(c, ctx) is { } v ? v > c.Threshold : null,
        ConditionType.Below => Number(c, ctx) is { } v ? v < c.Threshold : null,
        _ => null,
    };

    private static double? Number(AutomationCondition c, ConditionContext ctx) =>
        c.EntityId is { } e && ctx.States.Get(e) is { } s && s.TryGetNumber(out var v) ? v : null;

    /// <summary>La condition en quelques mots, pour le journal (« maison vide », « pas : plage horaire »).</summary>
    public static string Describe(AutomationCondition c)
    {
        var texte = c.Type switch
        {
            ConditionType.HomeOccupied => "maison occupée",
            ConditionType.HomeEmpty => "maison vide",
            ConditionType.TimeRange => $"plage {c.Start:hh\\:mm}-{c.End:hh\\:mm}",
            ConditionType.ThermostatMode => $"mode du thermostat {c.ThermostatId}",
            ConditionType.EntityActive => $"{c.EntityId} actif",
            ConditionType.Above => $"{c.EntityId} au-dessus de {c.Threshold}",
            _ => $"{c.EntityId} en dessous de {c.Threshold}",
        };
        return c.Negated ? $"pas : {texte}" : texte;
    }

    /// <summary>Début inclus, fin exclue ; fin avant début : à cheval sur minuit.</summary>
    public static bool InRange(TimeSpan debut, TimeSpan fin, TimeSpan heure) =>
        fin < debut ? heure >= debut || heure < fin : heure >= debut && heure < fin;
}
