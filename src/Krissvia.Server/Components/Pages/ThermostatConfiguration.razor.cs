using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Server.Components.Planning;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Création et configuration d'un thermostat. Les listes ne proposent que des entités du registre, de la bonne
/// famille : une sonde ne peut viser qu'un <c>sensor</c> de température, un radiateur qu'un <c>switch</c>. Le
/// service revérifie tout à l'enregistrement ; l'écran évite seulement de proposer ce qu'il refuserait.
/// </summary>
public partial class ThermostatConfiguration
{
    /// <summary>Valeur des listes facultatives pour « aucune » : une option de valeur null se sélectionne mal.</summary>
    private const string Aucune = "";

    [Parameter] public string? Id { get; set; }

    [Inject] private ThermostatService Service { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private Modele? _m;
    private PlanningSaisi<ThermostatMode>? _planning;
    private int _onglet;
    private ThermostatSettings? _existant;
    private bool _introuvable;
    private bool _idSaisi;
    private string? _erreur;
    private IReadOnlyList<RegisteredEntity> _entites = [];
    private List<RegisteredEntity> _sondes = [];
    private List<RegisteredEntity> _interrupteurs = [];
    private List<RegisteredEntity> _ouvertures = [];
    private List<RegisteredEntity> _previsions = [];

    private bool Creation => Id is null;
    private string Titre => Creation ? "Nouveau thermostat" : $"Configuration · {(_existant?.Name is { Length: > 0 } n ? n : Id)}";

    protected override async Task OnParametersSetAsync()
    {
        _entites = await Registre.ListAsync();
        var actives = _entites.Where(e => e.Enabled && !e.Missing).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _sondes = actives.Where(e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Temperature).ToList();
        // Les interrupteurs déclarés « Radiateur » dans l'écran Entités viennent en tête.
        _interrupteurs = [.. actives.Where(e => e.Kind == EntityKind.Switch).OrderByDescending(EntityUsages.IsRadiator)];
        _ouvertures = actives.Where(e => e.Kind == EntityKind.BinarySensor).ToList();
        _previsions = actives.Where(e => e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Weather).ToList();

        // Le lien « planning » de la carte ouvre directement l'onglet Planning.
        _onglet = Navigation.Uri.TrimEnd('/').EndsWith("/planning", StringComparison.Ordinal) ? 3 : 0;

        if (Creation)
        {
            _m = new Modele();
            _planning = new PlanningSaisi<ThermostatMode> { ParDefaut = ThermostatMode.Eco };
            return;
        }

        _existant = Service.Get(Id!);
        _introuvable = _existant is null;
        _m = _existant is null ? null : Modele.Depuis(_existant);
        _planning = _existant is null ? null : PlanningSaisi<ThermostatMode>.Depuis(_existant.Schedule.Days, _existant.Schedule.DefaultMode, c => c.Mode);
    }

    /// <summary>
    /// Les entités proposées, plus celles déjà choisies qui n'y seraient plus (disparues, désactivées) : sinon la
    /// liste afficherait un champ vide et l'enregistrement effacerait la référence sans que personne l'ait voulu.
    /// </summary>
    private IEnumerable<RegisteredEntity> Options(List<RegisteredEntity> proposees, params string?[] choisies)
    {
        foreach (var e in proposees) yield return e;
        foreach (var id in choisies.Where(id => !string.IsNullOrEmpty(id) && proposees.All(e => e.EntityId != id)))
            yield return _entites.FirstOrDefault(e => e.EntityId == id)
                         ?? new RegisteredEntity { EntityId = id!, DeviceId = "", IntegrationId = "", UniqueId = "", Kind = EntityKind.Sensor, DisplayName = $"{id} (inconnue)" };
    }

    private static string Libelle(RegisteredEntity e) =>
        e.Missing ? $"{e.DisplayName} (disparue)" : !e.Enabled ? $"{e.DisplayName} (désactivée)" : e.DisplayName;

    private string Nom(string id) => _entites.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id;

    /// <summary>À la création, l'identifiant suit le nom tant qu'on ne l'a pas tapé soi-même.</summary>
    private void NomChange(string nom)
    {
        _m!.Name = nom;
        if (Creation && (!_idSaisi || string.IsNullOrEmpty(_m.Id))) _m.Id = Phrases.Identifiant(nom);
    }

    private void IdChange(string id)
    {
        _m!.Id = id;
        _idSaisi = true;
    }

    private async Task EnregistrerAsync()
    {
        if (Creation && Service.Get(_m!.Id) is not null)
        {
            _erreur = $"Un thermostat « {_m.Id} » existe déjà.";
            return;
        }

        try
        {
            // Relu au moment d'enregistrer, pas à l'ouverture : un mode ou une consigne choisis sur la carte pendant
            // que cette page était ouverte ne doivent pas être écrasés par ceux d'il y a dix minutes.
            var actuel = Creation ? null : Service.Get(Id!) ?? _existant;
            var planning = new WeeklySchedule
            {
                Days = _planning!.Vers((debut, fin, mode) => new ScheduleSlot(debut, fin, mode)),
                DefaultMode = _planning.ParDefaut,
            };
            var enregistre = await Service.SaveAsync(_m!.Vers(actuel) with { Schedule = planning });
            Snackbar.Add($"Thermostat « {(enregistre.Name is { Length: > 0 } n ? n : enregistre.Id)} » enregistré.", Severity.Success);
            Navigation.NavigateTo("/thermostats");
        }
        catch (ThermostatValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private double Temperature(ThermostatMode mode) => mode switch
    {
        ThermostatMode.Comfort => _m!.Confort,
        ThermostatMode.Comfort1 => _m!.Confort - 1,
        ThermostatMode.Comfort2 => _m!.Confort - 2,
        ThermostatMode.Eco => _m!.Eco,
        _ => _m!.HorsGel,
    };

    private static string Degres(double t) => t.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " °C";

    private async Task SupprimerAsync()
    {
        var confirme = await Dialogues.ShowMessageBoxAsync("Supprimer le thermostat",
            $"« {_existant!.Name} » ne régulera plus. Les radiateurs restent dans l'état où ils sont.",
            yesText: "Supprimer", cancelText: "Annuler");
        if (confirme != true) return;

        await Service.DeleteAsync(_existant.Id);
        Snackbar.Add("Thermostat supprimé.", Severity.Info);
        Navigation.NavigateTo("/thermostats");
    }

    /// <summary>Le formulaire : des champs modifiables, convertis en <see cref="ThermostatSettings"/> à l'enregistrement.</summary>
    private sealed class Modele
    {
        private static readonly TpiSettings TpiParDefaut = new();
        private static readonly ThermostatSettings Defaut = new() { Id = "", TemperatureEntityId = "", SwitchEntityIds = [] };

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Sonde { get; set; } = "";
        public IReadOnlyCollection<string> Radiateurs { get; set; } = [];
        public string SondeExterieure { get; set; } = Aucune;
        public string Ouverture { get; set; } = Aucune;
        public string Prevision { get; set; } = Aucune;
        public bool Anticipation { get; set; } = Defaut.ForecastAnticipation;
        public double Confort { get; set; } = Defaut.ComfortTemperature;
        public double Eco { get; set; } = Defaut.EcoTemperature;
        public double HorsGel { get; set; } = Defaut.FrostProtectionTemperature;
        public int FailsafeMinutes { get; set; } = Defaut.FailsafeMinutes;
        public int OuvertureMinutes { get; set; } = Defaut.OpeningDelayMinutes;
        public int FermetureMinutes { get; set; } = Defaut.ClosingDelayMinutes;
        public double Kp { get; set; } = TpiParDefaut.Kp;
        public double Ki { get; set; } = TpiParDefaut.Ki;
        public double Kint { get; set; } = TpiParDefaut.Kint;
        public double IntegraleMax { get; set; } = TpiParDefaut.IntegralMax;
        public int Cycle { get; set; } = TpiParDefaut.CycleSeconds;
        public int MarcheMin { get; set; } = TpiParDefaut.MinOnSeconds;
        public int ArretMin { get; set; } = TpiParDefaut.MinOffSeconds;
        public double InertieHaut { get; set; } = TpiParDefaut.CoastingHigh;
        public double InertieBas { get; set; } = TpiParDefaut.CoastingLow;

        public static Modele Depuis(ThermostatSettings t) => new()
        {
            Id = t.Id,
            Name = t.Name,
            Sonde = t.TemperatureEntityId,
            Radiateurs = t.SwitchEntityIds.ToHashSet(),
            SondeExterieure = t.OutdoorTemperatureEntityId ?? Aucune,
            Ouverture = t.OpeningEntityId ?? Aucune,
            Prevision = t.ForecastEntityId ?? Aucune,
            Anticipation = t.ForecastAnticipation,
            Confort = t.ComfortTemperature,
            Eco = t.EcoTemperature,
            HorsGel = t.FrostProtectionTemperature,
            FailsafeMinutes = t.FailsafeMinutes,
            OuvertureMinutes = t.OpeningDelayMinutes,
            FermetureMinutes = t.ClosingDelayMinutes,
            Kp = t.Tpi.Kp,
            Ki = t.Tpi.Ki,
            Kint = t.Tpi.Kint,
            IntegraleMax = t.Tpi.IntegralMax,
            Cycle = t.Tpi.CycleSeconds,
            MarcheMin = t.Tpi.MinOnSeconds,
            ArretMin = t.Tpi.MinOffSeconds,
            InertieHaut = t.Tpi.CoastingHigh,
            InertieBas = t.Tpi.CoastingLow,
        };

        /// <summary>Part de l'existant : mode, consigne ajustée et planning ne se règlent pas ici et restent tels quels.</summary>
        public ThermostatSettings Vers(ThermostatSettings? existant) => (existant ?? Defaut) with
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            TemperatureEntityId = Sonde,
            SwitchEntityIds = Radiateurs.ToList(),
            OutdoorTemperatureEntityId = string.IsNullOrEmpty(SondeExterieure) ? null : SondeExterieure,
            OpeningEntityId = string.IsNullOrEmpty(Ouverture) ? null : Ouverture,
            ForecastEntityId = string.IsNullOrEmpty(Prevision) ? null : Prevision,
            ForecastAnticipation = Anticipation,
            ComfortTemperature = Confort,
            EcoTemperature = Eco,
            FrostProtectionTemperature = HorsGel,
            FailsafeMinutes = FailsafeMinutes,
            OpeningDelayMinutes = OuvertureMinutes,
            ClosingDelayMinutes = FermetureMinutes,
            Tpi = (existant?.Tpi ?? TpiParDefaut) with
            {
                Kp = Kp,
                Ki = Ki,
                Kint = Kint,
                IntegralMax = IntegraleMax,
                CycleSeconds = Cycle,
                MinOnSeconds = MarcheMin,
                MinOffSeconds = ArretMin,
                CoastingHigh = InertieHaut,
                CoastingLow = InertieBas,
            },
        };
    }
}
