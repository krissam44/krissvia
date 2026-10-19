using System.Globalization;
using Krissvia.Climate.Automations;
using Krissvia.Server.Components.Thermostats;

namespace Krissvia.Server.Components.Automatismes;

/// <summary>Un automatisme dit en français, pour la liste et le journal : « Quand… si… alors… ».</summary>
internal static class LibellesAutomatisme
{
    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Lundi d'abord, comme les plannings.</summary>
    public static readonly IReadOnlyList<DayOfWeek> Jours =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string Jour(DayOfWeek j) => Francais.DateTimeFormat.GetAbbreviatedDayName(j).TrimEnd('.');

    public static string TypeDeclencheur(TriggerType t) => t switch
    {
        TriggerType.Button => "Appui sur un bouton",
        TriggerType.Time => "Horaire",
        _ => "Changement d'état",
    };

    public static string Transition(StateTransition t) => t switch
    {
        StateTransition.Activated => "devient actif (détection, ouverture, allumage)",
        StateTransition.Deactivated => "repasse au repos",
        _ => "change dans un sens ou l'autre",
    };

    public static string TypeCondition(ConditionType t) => t switch
    {
        ConditionType.HomeOccupied => "Maison occupée",
        ConditionType.HomeEmpty => "Maison vide",
        ConditionType.TimeRange => "Plage horaire",
        ConditionType.ThermostatMode => "Mode d'un thermostat",
        ConditionType.EntityActive => "Équipement actif",
        ConditionType.Above => "Mesure au-dessus de",
        _ => "Mesure en dessous de",
    };

    public static string TypeAction(ActionType t) => t switch
    {
        ActionType.TurnOn => "Allumer",
        ActionType.TurnOff => "Éteindre",
        ActionType.Toggle => "Basculer",
        ActionType.ThermostatMode => "Mode d'un thermostat",
        _ => "Notifier (ntfy)",
    };

    public static string Issue(RunOutcome o) => o switch
    {
        RunOutcome.Done => "exécuté",
        RunOutcome.ConditionsNotMet => "conditions non remplies",
        RunOutcome.Throttled => "arrêté (boucle ?)",
        _ => "en échec",
    };

    public static Color Couleur(RunOutcome o) => o switch
    {
        RunOutcome.Done => Color.Success,
        RunOutcome.ConditionsNotMet => Color.Default,
        _ => Color.Error,
    };

    public static string Declencheur(AutomationTrigger t, Func<string?, string> nom) => t.Type switch
    {
        TriggerType.Button => string.IsNullOrWhiteSpace(t.Action) ? $"appui sur {nom(t.EntityId)}" : $"appui « {t.Action} » sur {nom(t.EntityId)}",
        TriggerType.Time => $"à {t.Time:hh\\:mm}, {(t.Days.Count is 0 or 7 ? "tous les jours" : string.Join(" ", Jours.Where(t.Days.Contains).Select(Jour)))}",
        _ => $"{nom(t.EntityId)} {Transition(t.Transition)}",
    };

    public static string Condition(AutomationCondition c, Func<string?, string> nom, Func<string?, string> thermostat)
    {
        var texte = c.Type switch
        {
            ConditionType.HomeOccupied => "maison occupée",
            ConditionType.HomeEmpty => "maison vide",
            ConditionType.TimeRange => $"entre {c.Start:hh\\:mm} et {c.End:hh\\:mm}",
            ConditionType.ThermostatMode => $"{thermostat(c.ThermostatId)} en {LibellesThermostat.Mode(c.Mode)}",
            ConditionType.EntityActive => $"{nom(c.EntityId)} actif",
            ConditionType.Above => $"{nom(c.EntityId)} > {c.Threshold.ToString("0.##", Francais)}",
            _ => $"{nom(c.EntityId)} < {c.Threshold.ToString("0.##", Francais)}",
        };
        return c.Negated ? $"pas : {texte}" : texte;
    }

    public static string Action(AutomationAction a, Func<string?, string> nom, Func<string?, string> thermostat)
    {
        var texte = a.Type switch
        {
            ActionType.TurnOn => $"allumer {nom(a.EntityId)}",
            ActionType.TurnOff => $"éteindre {nom(a.EntityId)}",
            ActionType.Toggle => $"basculer {nom(a.EntityId)}",
            ActionType.ThermostatMode => $"{thermostat(a.ThermostatId)} → {LibellesThermostat.Mode(a.Mode)}",
            _ => $"notifier « {(a.Title is { Length: > 0 } t ? t : a.Message)} »",
        };
        return a.DelaySeconds > 0 ? $"après {Duree(a.DelaySeconds)}, {texte}" : texte;
    }

    public static string Duree(int secondes) => secondes switch
    {
        < 60 => $"{secondes} s",
        < 3600 when secondes % 60 == 0 => $"{secondes / 60} min",
        < 3600 => $"{secondes / 60} min {secondes % 60} s",
        _ when secondes % 3600 == 0 => $"{secondes / 3600} h",
        _ => $"{secondes / 3600} h {secondes % 3600 / 60} min",
    };
}
