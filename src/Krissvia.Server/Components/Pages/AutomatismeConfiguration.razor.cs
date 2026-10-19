using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Création et réglage d'un automatisme : Quand (déclencheur), Si (conditions), Alors (actions dans l'ordre). Les listes
/// ne proposent que les entités de la bonne famille ; le service revérifie tout à l'enregistrement.
/// </summary>
public partial class AutomatismeConfiguration
{
    [Parameter] public string? Id { get; set; }

    [Inject] private AutomationService Service { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private Modele? _m;
    private Automation? _existant;
    private bool _introuvable;
    private bool _idSaisi;
    private string? _erreur;
    private IReadOnlyList<RegisteredEntity> _entites = [];
    private List<RegisteredEntity> _evenements = [];
    private List<RegisteredEntity> _toutOuRien = [];
    private List<RegisteredEntity> _pilotables = [];
    private List<RegisteredEntity> _mesures = [];

    private bool Creation => Id is null;

    private string Titre => Creation ? "Nouvel automatisme" : $"Automatisme · {(_existant?.Name is { Length: > 0 } n ? n : Id)}";

    /// <summary>La dernière action reçue du bouton choisi : c'est le mot à recopier (« single », « double »…).</summary>
    private string AideAction =>
        _m?.EntiteDeclencheur is { Length: > 0 } id && Etats.Get(id)?.Value is { ValueKind: System.Text.Json.JsonValueKind.String } v
            ? $"Vide : toutes. Dernière reçue : « {v.GetString()} »"
            : "Vide : toutes. Appuyez une fois pour voir son nom ici.";

    protected override async Task OnParametersSetAsync()
    {
        _entites = await Registre.ListAsync();
        var actives = _entites.Where(e => e.Enabled && !e.Missing).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _evenements = actives.Where(e => e.Kind == EntityKind.Event).ToList();
        _toutOuRien = actives.Where(e => e.Kind is EntityKind.BinarySensor or EntityKind.Switch or EntityKind.Light).ToList();
        _pilotables = actives.Where(e => e.Kind is EntityKind.Switch or EntityKind.Light).ToList();
        _mesures = actives.Where(e => e.Kind == EntityKind.Sensor && !string.IsNullOrEmpty(e.Unit)).ToList();

        if (Creation)
        {
            _m = new Modele();
            return;
        }

        _existant = Service.Get(Id!);
        _introuvable = _existant is null;
        _m = _existant is null ? null : Modele.Depuis(_existant);
    }

    /// <summary>Les entités proposées, plus celle déjà choisie si elle n'y est plus : sinon le champ paraîtrait vide.</summary>
    private IEnumerable<RegisteredEntity> Options(List<RegisteredEntity> proposees, string? choisie)
    {
        foreach (var e in proposees) yield return e;
        if (!string.IsNullOrEmpty(choisie) && proposees.All(e => e.EntityId != choisie))
            yield return _entites.FirstOrDefault(e => e.EntityId == choisie) is { } connue
                ? connue with { DisplayName = $"{connue.DisplayName} (indisponible)" }
                : new RegisteredEntity { EntityId = choisie, DeviceId = "", IntegrationId = "", UniqueId = "", Kind = EntityKind.Sensor, DisplayName = $"{choisie} (inconnue)" };
    }

    private static string NomThermostat(ThermostatSettings t) => t.Name is { Length: > 0 } n ? n : t.Id;

    private void NomChange(string nom)
    {
        _m!.Nom = nom;
        if (Creation && (!_idSaisi || string.IsNullOrEmpty(_m.Id))) _m.Id = Phrases.Identifiant(nom);
    }

    private void IdChange(string id)
    {
        _m!.Id = id;
        _idSaisi = true;
    }

    private void Deplacer(ActionSaisie a, int sens)
    {
        var i = _m!.Actions.IndexOf(a);
        var j = i + sens;
        if (j < 0 || j >= _m.Actions.Count) return;
        (_m.Actions[i], _m.Actions[j]) = (_m.Actions[j], _m.Actions[i]);
    }

    private async Task EnregistrerAsync()
    {
        if (Creation && Service.Get(_m!.Id) is not null)
        {
            _erreur = $"Un automatisme « {_m.Id} » existe déjà.";
            return;
        }

        try
        {
            var enregistre = await Service.SaveAsync(_m!.Vers());
            Snackbar.Add($"Automatisme « {(enregistre.Name is { Length: > 0 } n ? n : enregistre.Id)} » enregistré.", Severity.Success);
            Navigation.NavigateTo("/automatismes");
        }
        catch (AutomationValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private async Task SupprimerAsync()
    {
        var confirme = await Dialogues.ShowMessageBoxAsync("Supprimer l'automatisme",
            $"« {(_existant!.Name is { Length: > 0 } n ? n : _existant.Id)} » ne se déclenchera plus.", yesText: "Supprimer", cancelText: "Annuler");
        if (confirme != true) return;

        await Service.DeleteAsync(_existant.Id);
        Snackbar.Add("Automatisme supprimé.", Severity.Info);
        Navigation.NavigateTo("/automatismes");
    }

    // ── Formulaire ───────────────────────────────────────────────────────────

    private sealed class ConditionSaisie
    {
        public Guid Cle { get; } = Guid.NewGuid();
        public ConditionType Type { get; set; } = ConditionType.TimeRange;
        public bool Inverse { get; set; }
        public TimeSpan? Debut { get; set; } = new(22, 0, 0);
        public TimeSpan? Fin { get; set; } = new(6, 0, 0);
        public string Thermostat { get; set; } = "";
        public ThermostatMode Mode { get; set; } = ThermostatMode.Comfort;
        public string Entite { get; set; } = "";
        public double Seuil { get; set; } = 20;

        public static ConditionSaisie Depuis(AutomationCondition c) => new()
        {
            Type = c.Type, Inverse = c.Negated, Debut = c.Start, Fin = c.End, Thermostat = c.ThermostatId ?? "",
            Mode = c.Mode, Entite = c.EntityId ?? "", Seuil = c.Threshold,
        };

        public AutomationCondition Vers() => new()
        {
            Type = Type,
            Negated = Inverse,
            Start = Type == ConditionType.TimeRange ? Debut ?? TimeSpan.Zero : default,
            End = Type == ConditionType.TimeRange ? Fin ?? TimeSpan.Zero : default,
            ThermostatId = Type == ConditionType.ThermostatMode ? Vide(Thermostat) : null,
            Mode = Mode,
            EntityId = Type is ConditionType.EntityActive or ConditionType.Above or ConditionType.Below ? Vide(Entite) : null,
            Threshold = Type is ConditionType.Above or ConditionType.Below ? Seuil : 0,
        };
    }

    private sealed class ActionSaisie
    {
        public Guid Cle { get; } = Guid.NewGuid();
        public ActionType Type { get; set; } = ActionType.TurnOn;
        public string Entite { get; set; } = "";
        public string Thermostat { get; set; } = "";
        public ThermostatMode Mode { get; set; } = ThermostatMode.Comfort;
        public string Titre { get; set; } = "";
        public string Message { get; set; } = "";
        public double DelaiMinutes { get; set; }

        public static ActionSaisie Depuis(AutomationAction a) => new()
        {
            Type = a.Type, Entite = a.EntityId ?? "", Thermostat = a.ThermostatId ?? "", Mode = a.Mode,
            Titre = a.Title ?? "", Message = a.Message ?? "", DelaiMinutes = Math.Round(a.DelaySeconds / 60.0, 2),
        };

        public AutomationAction Vers() => new()
        {
            Type = Type,
            EntityId = Type is ActionType.TurnOn or ActionType.TurnOff or ActionType.Toggle ? Vide(Entite) : null,
            ThermostatId = Type == ActionType.ThermostatMode ? Vide(Thermostat) : null,
            Mode = Mode,
            Title = Type == ActionType.Notify ? Vide(Titre) : null,
            Message = Type == ActionType.Notify ? Vide(Message) : null,
            DelaySeconds = (int)Math.Round(DelaiMinutes * 60),
        };
    }

    private sealed class Modele
    {
        public string Id { get; set; } = "";
        public string Nom { get; set; } = "";
        public bool Actif { get; set; } = true;
        public TriggerType Declencheur { get; set; } = TriggerType.Button;
        public string EntiteDeclencheur { get; set; } = "";
        public string? Action { get; set; }
        public TimeSpan? Heure { get; set; } = new(7, 0, 0);
        public Dictionary<DayOfWeek, bool> Jours { get; } = LibellesAutomatisme.Jours.ToDictionary(j => j, _ => false);
        public StateTransition Transition { get; set; } = StateTransition.Activated;
        public List<ConditionSaisie> Conditions { get; } = [];
        public List<ActionSaisie> Actions { get; } = [new()];

        public static Modele Depuis(Automation a)
        {
            var m = new Modele
            {
                Id = a.Id, Nom = a.Name, Actif = a.Enabled, Declencheur = a.Trigger.Type, EntiteDeclencheur = a.Trigger.EntityId ?? "",
                Action = a.Trigger.Action, Heure = a.Trigger.Time, Transition = a.Trigger.Transition,
            };
            foreach (var j in a.Trigger.Days) m.Jours[j] = true;
            m.Conditions.AddRange(a.Conditions.Select(ConditionSaisie.Depuis));
            m.Actions.Clear();
            m.Actions.AddRange(a.Actions.Select(ActionSaisie.Depuis));
            return m;
        }

        public Automation Vers() => new()
        {
            Id = Id.Trim(),
            Name = Nom.Trim(),
            Enabled = Actif,
            Trigger = new AutomationTrigger
            {
                Type = Declencheur,
                EntityId = Declencheur == TriggerType.Time ? null : Vide(EntiteDeclencheur),
                Action = Declencheur == TriggerType.Button ? Vide(Action) : null,
                Time = Declencheur == TriggerType.Time ? Heure ?? TimeSpan.Zero : default,
                Days = Declencheur == TriggerType.Time ? [.. Jours.Where(kv => kv.Value).Select(kv => kv.Key)] : [],
                Transition = Transition,
            },
            Conditions = [.. Conditions.Select(c => c.Vers())],
            Actions = [.. Actions.Select(a => a.Vers())],
        };
    }

    private static string? Vide(string? texte) => string.IsNullOrWhiteSpace(texte) ? null : texte.Trim();
}
