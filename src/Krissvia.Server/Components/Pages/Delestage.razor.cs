using System.Globalization;
using Krissvia.Climate.LoadShedding;
using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Délestage : l'état du moment (relu toutes les 2 s, le rythme du délesteur) et toute la configuration, enregistrée
/// d'un bloc par <see cref="LoadSheddingService.SaveAsync"/>, qui la vérifie comme pour l'API.
/// </summary>
/// <remarks>
/// La configuration n'est chargée qu'à l'ouverture et sur « Annuler » : la relecture périodique ne touche que l'état,
/// sinon elle effacerait la saisie en cours.
/// </remarks>
public partial class Delestage : IDisposable
{
    /// <summary>Valeur des listes facultatives pour « aucune » : une option de valeur null se sélectionne mal.</summary>
    private const string Aucune = "";

    [Inject] private LoadSheddingService Service { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    private Modele _m = new();
    private LoadSheddingStatus _etat = default!;
    private string? _erreur;
    private Timer? _minuterie;
    private IReadOnlyList<RegisteredEntity> _entites = [];
    private List<RegisteredEntity> _puissances = [];
    private List<RegisteredEntity> _interrupteurs = [];

    /// <summary>L'abonnement enregistré, en VA : la pleine échelle de la jauge.</summary>
    private double Abonnement => Service.Configuration.SubscriptionKva * 1000;

    protected override async Task OnInitializedAsync()
    {
        _entites = await Registre.ListAsync();
        var actives = _entites.Where(e => e.Enabled && !e.Missing).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _puissances = actives.Where(e => e.Kind == EntityKind.Sensor && e.DeviceClass is DeviceClasses.Power or DeviceClasses.ApparentPower).ToList();
        _interrupteurs = actives.Where(e => e.Kind == EntityKind.Switch).ToList();

        Recharger();
        _etat = Service.Status;
        _minuterie = new Timer(_ => InvokeAsync(() =>
        {
            _etat = Service.Status;
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Recharger()
    {
        _erreur = null;
        _m = Modele.Depuis(Service.Configuration);
    }

    private async Task EnregistrerAsync()
    {
        try
        {
            await Service.SaveAsync(_m.Vers());
            _erreur = null;
            _etat = Service.Status;
            Snackbar.Add(_m.Enabled ? "Délestage enregistré et appliqué." : "Configuration enregistrée ; le délestage est désactivé.", Severity.Success);
        }
        catch (LoadSheddingValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    // ── Sources ──────────────────────────────────────────────────────────────

    private void AjouterMesure() =>
        _m.Sources.Add(new SourceSaisie { Nom = _m.Sources.Count == 0 ? "Linky" : "Pince" });

    private void AjouterSomme() => _m.Sources.Add(new SourceSaisie { Nom = "Somme", Somme = true });

    private void Deplacer(int rang, int sens)
    {
        var source = _m.Sources[rang];
        _m.Sources.RemoveAt(rang);
        _m.Sources.Insert(rang + sens, source);
    }

    // ── Équipements délestables ──────────────────────────────────────────────

    private void AjouterEquipement() =>
        _m.Equipements.Add(new EquipementDelestable { Priorite = _m.Equipements.Count == 0 ? 1 : Math.Min(10, _m.Equipements.Max(d => d.Priorite) + 1) });

    /// <summary>L'identifiant suit le nom tant qu'il n'a pas été enregistré : l'API et le journal s'en servent ensuite.</summary>
    private void NomEquipementChange(EquipementDelestable d, string nom)
    {
        d.Nom = nom;
        if (!d.Enregistre) d.Id = Phrases.Identifiant(nom);
    }

    /// <summary>Sans puissance saisie, celle déclarée pour les switches choisis (page Entités) est reprise.</summary>
    private void InterrupteursChange(EquipementDelestable d, IEnumerable<string> interrupteurs)
    {
        d.Interrupteurs = interrupteurs.ToHashSet();
        if (d.Nominale <= 0) d.Nominale = PuissanceDeclaree(d.Interrupteurs);
    }

    private double PuissanceDeclaree(IEnumerable<string> interrupteurs) =>
        interrupteurs.Sum(id => _entites.FirstOrDefault(e => e.EntityId == id)?.NominalPower ?? 0);

    /// <summary>Lier un thermostat reprend ses radiateurs et son nom, si rien n'est encore choisi.</summary>
    private void ThermostatChange(EquipementDelestable d, string thermostatId)
    {
        d.Thermostat = thermostatId;
        if (Thermostats.Get(thermostatId) is not { } t) return;

        if (d.Interrupteurs.Count == 0) InterrupteursChange(d, t.SwitchEntityIds);
        if (string.IsNullOrWhiteSpace(d.Nom)) NomEquipementChange(d, t.Name is { Length: > 0 } n ? n : t.Id);
    }

    // ── Affichage ────────────────────────────────────────────────────────────

    private string NomEquipement(string id) =>
        Service.Configuration.Devices.FirstOrDefault(d => d.Id == id) is { Name.Length: > 0 } d ? d.Name : id;

    private string NomEntite(string id) => _entites.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id;

    /// <summary>Les entités proposées, plus celles déjà choisies qui n'y seraient plus : sinon le champ paraîtrait vide.</summary>
    private IEnumerable<RegisteredEntity> Options(List<RegisteredEntity> proposees, params string?[] choisies)
    {
        foreach (var e in proposees) yield return e;
        foreach (var id in choisies.Where(id => !string.IsNullOrEmpty(id) && proposees.All(e => e.EntityId != id)))
            yield return _entites.FirstOrDefault(e => e.EntityId == id)
                         ?? new RegisteredEntity { EntityId = id!, DeviceId = "", IntegrationId = "", UniqueId = "", Kind = EntityKind.Sensor, DisplayName = $"{id} (inconnue)" };
    }

    private static string Libelle(RegisteredEntity e) =>
        e.Missing ? $"{e.DisplayName} (disparue)" : !e.Enabled ? $"{e.DisplayName} (désactivée)" : e.DisplayName;

    private static string Va(double valeur) => valeur.ToString("#,0", CultureInfo.GetCultureInfo("fr-FR")) + " VA";

    /// <summary>Position CSS sur la jauge, en culture invariante (« 33,3% » n'est pas du CSS).</summary>
    private string Pourcent(double valeur) =>
        (Abonnement <= 0 ? 0 : Math.Clamp(valeur / Abonnement * 100, 0, 100)).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private string CouleurJauge(double valeur) =>
        valeur >= _etat.AlertThreshold ? "#c62828" : valeur >= _etat.RestoreThreshold ? "#ef8a17" : "#43a047";

    private string Age(DateTimeOffset? mesure)
    {
        if (mesure is not { } m) return "";
        var secondes = (int)Math.Max(0, (Horloge.GetUtcNow() - m).TotalSeconds);
        return secondes < 2 ? "à l'instant" : $"il y a {secondes} s";
    }

    public void Dispose() => _minuterie?.Dispose();

    // ── Formulaire ───────────────────────────────────────────────────────────

    private sealed class SourceSaisie
    {
        public string Nom { get; set; } = "";
        public bool Somme { get; init; }
        public string Entite { get; set; } = "";
        public PowerQuality Qualite { get; set; } = PowerQuality.Apparent;
        public double Majoration { get; set; } = 1.10;
    }

    private sealed class EquipementDelestable
    {
        public string Id { get; set; } = "";
        public string Nom { get; set; } = "";
        public int Priorite { get; set; } = 5;
        public double Nominale { get; set; }
        public int DelaiRetablissement { get; set; } = 120;
        public IReadOnlyCollection<string> Interrupteurs { get; set; } = [];
        public string Thermostat { get; set; } = Aucune;

        /// <summary>Déjà enregistré : son identifiant ne suit plus le nom.</summary>
        public bool Enregistre { get; init; }
    }

    private sealed class Modele
    {
        public bool Enabled { get; set; }
        public double Kva { get; set; } = 6;
        public double Alerte { get; set; } = 80;
        public double Retablissement { get; set; } = 70;
        public int AgeMax { get; set; } = 10;
        public int Stabilisation { get; set; } = 5;
        public int Verification { get; set; } = 5;
        public List<SourceSaisie> Sources { get; init; } = [];
        public List<EquipementDelestable> Equipements { get; init; } = [];

        public static Modele Depuis(LoadSheddingConfiguration c) => new()
        {
            Enabled = c.Enabled,
            Kva = c.SubscriptionKva,
            Alerte = c.AlertPercent,
            Retablissement = c.RestorePercent,
            AgeMax = c.MaxReadingAgeSeconds,
            Stabilisation = c.SettleDelaySeconds,
            Verification = c.CutVerificationTimeoutSeconds,
            Sources = c.Sources.Select(s => new SourceSaisie
            {
                Nom = s.Name,
                Somme = s.Kind == PowerSourceKind.EquipmentSum,
                Entite = s.PowerEntityId ?? "",
                Qualite = s.PowerQuality,
                Majoration = s.ActiveToApparentFactor,
            }).ToList(),
            Equipements = c.Devices.Select(d => new EquipementDelestable
            {
                Id = d.Id,
                Nom = d.Name,
                Priorite = d.Priority,
                Nominale = d.NominalPower,
                DelaiRetablissement = d.RestoreDelaySeconds,
                Interrupteurs = d.SwitchEntityIds.ToHashSet(),
                Thermostat = d.ThermostatId ?? Aucune,
                Enregistre = true,
            }).ToList(),
        };

        public LoadSheddingConfiguration Vers() => new()
        {
            Enabled = Enabled,
            SubscriptionKva = Kva,
            AlertPercent = Alerte,
            RestorePercent = Retablissement,
            MaxReadingAgeSeconds = AgeMax,
            SettleDelaySeconds = Stabilisation,
            CutVerificationTimeoutSeconds = Verification,
            Sources = Sources.Select(s => new PowerSourceConfiguration
            {
                Name = s.Nom.Trim(),
                Kind = s.Somme ? PowerSourceKind.EquipmentSum : PowerSourceKind.Entity,
                PowerEntityId = s.Somme || string.IsNullOrEmpty(s.Entite) ? null : s.Entite,
                PowerQuality = s.Somme ? PowerQuality.Apparent : s.Qualite,
                ActiveToApparentFactor = s.Majoration,
            }).ToList(),
            Devices = Equipements.Select(d => new SheddableDeviceConfiguration
            {
                Id = d.Id.Trim(),
                Name = d.Nom.Trim(),
                Priority = d.Priorite,
                NominalPower = d.Nominale,
                RestoreDelaySeconds = d.DelaiRetablissement,
                SwitchEntityIds = d.Interrupteurs.ToList(),
                ThermostatId = string.IsNullOrEmpty(d.Thermostat) ? null : d.Thermostat,
            }).ToList(),
        };
    }
}
