using System.Globalization;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Pages;

/// <summary>Automatismes : une carte chacun, dite en « Quand… si… alors… », sa dernière exécution, et le journal.</summary>
public partial class Automatismes : IDisposable
{
    [Inject] private AutomationService Service { get; set; } = default!;
    [Inject] private AutomationEngine Moteur { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;

    private IReadOnlyList<RegisteredEntity> _entites = [];
    private readonly HashSet<string> _enCours = new(StringComparer.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        _entites = await Registre.ListAsync();
        Moteur.Ran += Rafraichir;
    }

    private void Rafraichir(AutomationRun _) => InvokeAsync(StateHasChanged);

    private static string Nom(Automation a) => a.Name is { Length: > 0 } n ? n : a.Id;

    private string NomEntite(string? id) => _entites.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id ?? "?";

    private string NomThermostat(string? id) => id is not null && Thermostats.Get(id) is { } t && t.Name is { Length: > 0 } n ? n : id ?? "?";

    private string Heure(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone);
        return local.Date == Horloge.GetLocalNow().Date ? local.ToString("HH:mm:ss") : local.ToString("dd/MM HH:mm", CultureInfo.GetCultureInfo("fr-FR"));
    }

    private async Task ActiverAsync(Automation a, bool actif)
    {
        try
        {
            await Service.SetEnabledAsync(a.Id, actif);
        }
        catch (AutomationValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    private async Task ExecuterAsync(Automation a)
    {
        _enCours.Add(a.Id);
        try
        {
            var r = await Moteur.RunNowAsync(a);
            Snackbar.Add($"« {Nom(a)} » : {LibellesAutomatisme.Issue(r.Outcome)}{(r.Detail is { } d ? $" — {d}" : "")}",
                r.Outcome == RunOutcome.Done ? Severity.Success : r.Outcome == RunOutcome.ConditionsNotMet ? Severity.Info : Severity.Warning);
        }
        finally
        {
            _enCours.Remove(a.Id);
        }
    }

    /// <summary>Créer (id null) ou régler un automatisme, dans sa fenêtre ; la liste se relit en sortant.</summary>
    private async Task OuvrirAsync(string? id)
    {
        var parametres = new DialogParameters<AutomatismeDialog> { { d => d.Id, id } };
        var dialogue = await Dialogues.ShowAsync<AutomatismeDialog>(null, parametres,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true, BackdropClick = false });
        await dialogue.Result;
        _entites = await Registre.ListAsync();
        StateHasChanged();
    }

    public void Dispose() => Moteur.Ran -= Rafraichir;
}
