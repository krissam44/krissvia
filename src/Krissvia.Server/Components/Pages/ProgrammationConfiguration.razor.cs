using Krissvia.Climate.DeviceSchedules;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Server.Components.Planning;

namespace Krissvia.Server.Components.Pages;

/// <summary>Création et modification d'une programmation : équipements pilotés et planning allumé / éteint.</summary>
public partial class ProgrammationConfiguration
{
    private static readonly bool[] Valeurs = [true, false];

    [Parameter] public string? Id { get; set; }

    [Inject] private DeviceScheduleService Service { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private IReadOnlyList<RegisteredEntity> _entites = [];
    private string _id = "";
    private string _nom = "";
    private bool _idSaisi;
    private bool _active = true;
    private IReadOnlyCollection<string> _interrupteurs = [];
    private PlanningSaisi<bool> _planning = new();
    private bool _introuvable;
    private string? _erreur;

    private bool Creation => Id is null;
    private string Titre => Creation ? "Nouvelle programmation" : $"Programmation · {(_nom.Length > 0 ? _nom : Id)}";

    protected override async Task OnParametersSetAsync()
    {
        _entites = await Registre.ListAsync();
        if (Creation) return;

        if (Service.Get(Id!) is not { } p)
        {
            _introuvable = true;
            return;
        }

        _id = p.Id;
        _nom = p.Name;
        _active = p.Enabled;
        _interrupteurs = p.SwitchEntityIds.ToHashSet();
        _planning = PlanningSaisi<bool>.Depuis(p.Days, p.DefaultOn, c => c.On);
    }

    /// <summary>Les switches proposés : pas ceux d'un thermostat, qui ont déjà leur pilote ; ceux déjà choisis restent.</summary>
    private IEnumerable<RegisteredEntity> Proposes()
    {
        var radiateurs = Thermostats.List().SelectMany(t => t.SwitchEntityIds).ToHashSet(StringComparer.Ordinal);
        return _entites
            .Where(e => e.Kind == EntityKind.Switch && ((e.Enabled && !e.Missing && !radiateurs.Contains(e.EntityId)) || _interrupteurs.Contains(e.EntityId)))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private string NomEntite(string id) => _entites.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id;

    private static string Libelle(bool allume) => allume ? "Allumé" : "Éteint";

    private static string Couleur(bool allume) => allume ? "#43a047" : "#b0b4bd";

    private void NomChange(string nom)
    {
        _nom = nom;
        if (Creation && (!_idSaisi || _id.Length == 0)) _id = Phrases.Identifiant(nom);
    }

    private void IdChange(string id)
    {
        _id = id;
        _idSaisi = true;
    }

    private async Task EnregistrerAsync()
    {
        if (Creation && Service.Get(_id) is not null)
        {
            _erreur = $"Une programmation « {_id} » existe déjà.";
            return;
        }

        var programmation = new DeviceSchedule
        {
            Id = _id.Trim(),
            Name = _nom.Trim(),
            Enabled = _active,
            SwitchEntityIds = _interrupteurs.ToList(),
            Days = _planning.Vers((debut, fin, allume) => new DeviceScheduleSlot(debut, fin, allume)),
            DefaultOn = _planning.ParDefaut,
        };

        try
        {
            await Service.SaveAsync(programmation);
            Snackbar.Add($"Programmation « {(programmation.Name.Length > 0 ? programmation.Name : programmation.Id)} » enregistrée.", Severity.Success);
            Navigation.NavigateTo("/programmations");
        }
        catch (DeviceScheduleValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private async Task SupprimerAsync()
    {
        var confirme = await Dialogues.ShowMessageBoxAsync("Supprimer la programmation",
            $"« {(_nom.Length > 0 ? _nom : Id)} » ne pilotera plus rien. Les équipements restent dans l'état où ils sont.",
            yesText: "Supprimer", cancelText: "Annuler");
        if (confirme != true) return;

        await Service.DeleteAsync(Id!);
        Snackbar.Add("Programmation supprimée.", Severity.Info);
        Navigation.NavigateTo("/programmations");
    }
}
