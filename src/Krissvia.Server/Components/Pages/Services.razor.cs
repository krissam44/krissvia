namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Page Services : un plugin par ligne, son statut, l'interrupteur qui le lance ou l'arrête, et sa fiche. Relue toutes
/// les 2 s : un plugin qui se connecte ou plante change de statut sans qu'on recharge la page.
/// </summary>
public partial class Services
{
    [Inject] private PluginSupervisor Supervisor { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private IReadOnlyList<ServiceSnapshot> _services = [];
    private Timer? _minuterie;

    protected override void OnInitialized()
    {
        _services = Supervisor.Snapshots();
        _minuterie = new Timer(_ => InvokeAsync(() =>
        {
            _services = Supervisor.Snapshots();
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private async Task BasculerAsync(string id, bool actif)
    {
        if (await Supervisor.SetEnabledAsync(id, actif) is { } refus)
            Snackbar.Add(refus, Severity.Warning);

        _services = Supervisor.Snapshots();
    }

    public void Dispose() => _minuterie?.Dispose();
}
