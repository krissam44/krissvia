namespace Krissvia.Server.Components.Pages;

/// <summary>Fiche d'un service : son état, son processus, ses relances et les dernières lignes de son journal.</summary>
public partial class ServiceDetail
{
    [Parameter] public string Id { get; set; } = "";

    [Inject] private PluginSupervisor Supervisor { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private ServiceSnapshot? _service;
    private Timer? _minuterie;

    protected override void OnParametersSet()
    {
        _service = Supervisor.GetSnapshot(Id);
        _minuterie ??= new Timer(_ => InvokeAsync(() =>
        {
            _service = Supervisor.GetSnapshot(Id);
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private async Task BasculerAsync(bool actif)
    {
        if (await Supervisor.SetEnabledAsync(Id, actif) is { } refus) Snackbar.Add(refus, Severity.Warning);
        _service = Supervisor.GetSnapshot(Id);
    }

    private async Task RelancerAsync()
    {
        if (await Supervisor.RestartAsync(Id) is { } refus) Snackbar.Add(refus, Severity.Warning);
        else Snackbar.Add("Redémarrage demandé.", Severity.Info);
        _service = Supervisor.GetSnapshot(Id);
    }

    public void Dispose() => _minuterie?.Dispose();
}
