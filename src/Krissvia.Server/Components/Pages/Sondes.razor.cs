using Krissvia.Core.Live;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server.Components.Pages;

/// <summary>Sondes de température et moyennes par pièce (d'après la pièce de chaque appareil), en direct.</summary>
public partial class Sondes : IDisposable
{
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private LiveFeed Flux { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    private IReadOnlyList<RegisteredEntity> _entites = [];
    private IReadOnlyList<RegisteredDevice> _appareils = [];
    private SensorOverviewResult _vue = new([], []);
    private SuiviEnDirect? _suivi;

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
        }, TimeSpan.FromMinutes(1));
    }

    private async Task RelireRegistreAsync()
    {
        _entites = await Registre.ListAsync();
        _appareils = await Registre.ListDevicesAsync();
    }

    private void Calculer() => _vue = SensorOverview.Build(_entites, _appareils, Etats, Horloge.GetUtcNow());

    public void Dispose() => _suivi?.Dispose();
}
