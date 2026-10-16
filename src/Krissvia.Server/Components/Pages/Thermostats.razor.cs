using System.Globalization;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Thermostats : une carte par thermostat, sa température, sa consigne et son statut, les boutons de mode et le
/// planning. Relue toutes les 2 s, comme la page Services : le moteur tourne toutes les 10 s, sans prévenir l'écran.
/// </summary>
/// <remarks>
/// Les boutons passent par <see cref="ThermostatService"/>, comme l'API : l'écran ne peut produire aucun état
/// qu'un appel d'API ne produirait pas.
/// </remarks>
public partial class Thermostats : IDisposable
{
    [Inject] private ThermostatService Service { get; set; } = default!;
    [Inject] private ThermostatEngine Moteur { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private List<(ThermostatSettings Reglages, ThermostatSnapshot? Etat)> _thermostats = [];
    private Timer? _minuterie;

    protected override void OnInitialized()
    {
        Relire();
        _minuterie = new Timer(_ => InvokeAsync(() =>
        {
            Relire();
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Relire() =>
        _thermostats = Service.List().Select(t => (t, Moteur.GetSnapshot(t.Id))).ToList();

    /// <summary>
    /// La consigne ajustée d'abord : le moteur ne la reprend qu'à son prochain passage (10 s), et deux clics rapides
    /// sur « + » partiraient sinon de la même valeur. Sans ajustement, celle que régule le moteur (planning compris),
    /// puis celle du mode avant son premier calcul.
    /// </summary>
    private static double Consigne(ThermostatSettings t, ThermostatSnapshot? etat) =>
        t.TargetOverride ?? etat?.Target ?? t.TargetFor(t.ManualMode);

    /// <summary>Arrêté à la main : le service refuse une consigne, autant ne pas proposer les boutons.</summary>
    private static bool ConsigneFigee(ThermostatSettings t) => !t.ScheduleActive && t.ManualMode == ThermostatMode.Off;

    private static string Temperature(double? valeur) =>
        valeur is { } v ? v.ToString("0.0", CultureInfo.GetCultureInfo("fr-FR")) + " °C" : "—";

    private Task ModeAsync(ThermostatSettings t, ThermostatMode mode) =>
        ExecuterAsync(() => Service.SetModeAsync(t.Id, mode));

    // Le pas est appliqué par le service, sous son verrou, à la consigne réellement suivie : le même chemin que
    // l'ordre adjust_target d'un widget.
    private Task AjusterAsync(ThermostatSettings t, double pas) => ExecuterAsync(() => Service.AdjustTargetAsync(t.Id, pas));

    private Task RetablirAsync(ThermostatSettings t) => ExecuterAsync(() => Service.SetTargetAsync(t.Id, null));

    private Task PlanningAsync(ThermostatSettings t, bool actif) =>
        ExecuterAsync(() => Service.SetScheduleActiveAsync(t.Id, actif));

    private async Task ExecuterAsync(Func<Task<ThermostatSettings?>> action)
    {
        try
        {
            if (await action() is null) Snackbar.Add("Ce thermostat n'existe plus.", Severity.Warning);
        }
        catch (ThermostatValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }

        Relire();
    }

    public void Dispose() => _minuterie?.Dispose();
}
