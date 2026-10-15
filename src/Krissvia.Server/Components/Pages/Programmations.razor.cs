using System.Globalization;
using Krissvia.Climate.DeviceSchedules;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Pages;

/// <summary>Programmations : une carte chacune, son état voulu et son prochain changement, relue toutes les 5 s.</summary>
public partial class Programmations : IDisposable
{
    [Inject] private DeviceScheduleService Service { get; set; } = default!;
    [Inject] private DeviceScheduleEngine Moteur { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    private IReadOnlyList<RegisteredEntity> _entites = [];
    private Timer? _minuterie;

    protected override async Task OnInitializedAsync()
    {
        _entites = await Registre.ListAsync();
        _minuterie = new Timer(_ => InvokeAsync(StateHasChanged), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private string NomEntite(string id) => _entites.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id;

    /// <summary>« jusqu'à 08:00 », « allumage demain à 06:00 », « allumage samedi à 22:00 ».</summary>
    private string ProchainChangement(DeviceScheduleSnapshot etat)
    {
        if (etat.NextChange is not { } t) return "en permanence";
        var aujourdhui = Horloge.GetLocalNow().DateTime.Date;
        var jour = t.Date == aujourdhui ? ""
            : t.Date == aujourdhui.AddDays(1) ? "demain "
            : t.ToString("dddd ", CultureInfo.GetCultureInfo("fr-FR"));
        return etat.Desired ? $"jusqu'à {jour}{t:HH:mm}" : $"allumage {jour}à {t:HH:mm}";
    }

    private async Task ActiverAsync(DeviceSchedule p, bool actif)
    {
        try
        {
            await Service.SetEnabledAsync(p.Id, actif);
        }
        catch (DeviceScheduleValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    public void Dispose() => _minuterie?.Dispose();
}
