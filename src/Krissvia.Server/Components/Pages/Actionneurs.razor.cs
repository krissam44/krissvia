using Krissvia.Climate.DeviceSchedules;
using Krissvia.Climate.LoadShedding;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Live;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Interrupteurs et lumières par pièce. Chaque ordre est vérifié : la tuile ne passe à « ON » que quand l'appareil le
/// confirme (DomoMud affichait l'état voulu tout de suite, par optimisme).
/// </summary>
public partial class Actionneurs : IDisposable
{
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private ICommandBus Commandes { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private DeviceScheduleService Programmations { get; set; } = default!;
    [Inject] private LoadShedder Delesteur { get; set; } = default!;
    [Inject] private LiveFeed Flux { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private IReadOnlyList<RegisteredEntity> _entites = [];
    private IReadOnlyList<RegisteredDevice> _appareils = [];
    private IReadOnlyList<ActuatorRoom> _pieces = [];
    private readonly HashSet<string> _enAttente = new(StringComparer.Ordinal);
    private bool _toutEnCours;
    private SuiviEnDirect? _suivi;

    private int Total => _pieces.Sum(p => p.Actuators.Count);
    private int Allumes => _pieces.Sum(p => p.Actuators.Count(a => a.On == true));

    protected override async Task OnInitializedAsync()
    {
        await RelireRegistreAsync();
        Calculer();
        _suivi = new SuiviEnDirect(Flux, async registre =>
        {
            if (registre) await RelireRegistreAsync();
            await InvokeAsync(() =>
            {
                Calculer();
                StateHasChanged();
            });
        }, TimeSpan.FromSeconds(30));
    }

    private async Task RelireRegistreAsync()
    {
        _entites = await Registre.ListAsync();
        _appareils = await Registre.ListDevicesAsync();
    }

    private void Calculer()
    {
        var pilotes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in Thermostats.List())
            foreach (var radiateur in t.SwitchEntityIds) pilotes.TryAdd(radiateur, t.Name is { Length: > 0 } n ? n : t.Id);

        var programmes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Programmations.List().Where(p => p.Enabled))
            foreach (var prise in p.SwitchEntityIds) programmes.TryAdd(prise, p.Name);

        _pieces = ActuatorOverview.Build(_entites, _appareils, Etats, pilotes, programmes, Delesteur.IsSwitchShed);
    }

    private async Task BasculerAsync(Actuator a)
    {
        if (!a.Controllable) return;
        var allumer = a.On != true;

        // Rallumer un équipement délesté peut faire disjoncter : on demande d'abord.
        if (allumer && a.Shed && await Dialogues.ShowMessageBoxAsync("Équipement délesté",
                $"« {a.Entity.DisplayName} » a été coupé par le délesteur. Le rallumer peut faire disjoncter. Le rallumer quand même ?",
                yesText: "Rallumer", cancelText: "Annuler") != true)
            return;

        _enAttente.Add(a.Entity.EntityId);
        var resultat = await EnvoyerAsync(a, allumer);
        _enAttente.Remove(a.Entity.EntityId);

        if (resultat.Outcome != CommandOutcome.Accepted)
            Snackbar.Add($"« {a.Entity.DisplayName} » : {resultat.Detail ?? "ordre refusé"}.", Severity.Error);
        else if (resultat.Verification == CommandVerification.NotConfirmed)
            Snackbar.Add($"« {a.Entity.DisplayName} » n'a pas confirmé l'ordre.", Severity.Warning);
        Calculer();
    }

    private async Task ToutAsync(bool allumer)
    {
        var cibles = ActuatorOverview.Bulk(_pieces, allumer);
        if (cibles.Count == 0) return;

        var verbe = allumer ? "Allumer" : "Éteindre";
        if (await Dialogues.ShowMessageBoxAsync($"{verbe} tout", $"{verbe} {cibles.Count} équipement(s) : {string.Join(", ", cibles.Select(c => c.Entity.DisplayName))} ?",
                yesText: verbe, cancelText: "Annuler") != true)
            return;

        _toutEnCours = true;
        foreach (var c in cibles) _enAttente.Add(c.Entity.EntityId);
        StateHasChanged();

        var resultats = await Task.WhenAll(cibles.Select(c => EnvoyerAsync(c, allumer)));

        foreach (var c in cibles) _enAttente.Remove(c.Entity.EntityId);
        _toutEnCours = false;
        Calculer();

        // Le bilan réel, et non « N équipements allumés » quoi qu'il arrive comme dans DomoMud.
        var confirmes = resultats.Count(r => r.Confirmed);
        var refuses = resultats.Count(r => r.Outcome != CommandOutcome.Accepted);
        var douteux = resultats.Length - confirmes - refuses;
        Snackbar.Add(
            $"{confirmes} confirmé(s)" + (douteux > 0 ? $", {douteux} sans confirmation" : "") + (refuses > 0 ? $", {refuses} refusé(s)" : "") + ".",
            refuses > 0 ? Severity.Error : douteux > 0 ? Severity.Warning : Severity.Success);
    }

    private Task<VerifiedCommandResult> EnvoyerAsync(Actuator a, bool allumer) =>
        Commandes.SendVerifiedAsync(new EntityCommand(a.Entity.EntityId, allumer ? StandardCommands.TurnOn : StandardCommands.TurnOff)
        {
            Origin = "interface:actionneurs",
        });

    private static string Icone(Actuator a) =>
        a.DrivenBy is not null || EntityUsages.IsRadiator(a.Entity) ? Icons.Material.Filled.Whatshot
        : a.Entity.Kind == EntityKind.Light ? (a.On == true ? Icons.Material.Filled.Lightbulb : Icons.Material.Outlined.Lightbulb)
        : a.On == true ? Icons.Material.Filled.ToggleOn : Icons.Material.Filled.ToggleOff;

    private static string Infobulle(Actuator a) =>
        !a.Available ? "Injoignable : aucun état récent."
        : a.DrivenBy is { } t ? $"Piloté par le thermostat « {t} » : il se règle depuis l'écran Thermostats."
        : a.Shed ? "Coupé par le délesteur."
        : a.ScheduledBy is { } p ? $"Programmation « {p} » : un geste à la main tient jusqu'au prochain créneau."
        : a.On == true ? "Éteindre" : "Allumer";

    public void Dispose() => _suivi?.Dispose();
}
