using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// Qui chauffe en ce moment, et trois actions rapides (Confort, Éco, Arrêt) par thermostat. Mêmes données que l'écran
/// Thermostats (le flux partagé) : passer d'un écran à l'autre ne relit rien. Pas d'état optimiste, échec seul signalé.
/// </summary>
public partial class Radiateurs : IDisposable
{
    [Inject] private ThermostatsEnDirect Live { get; set; } = null!;
    [Inject] private FluxEnDirect Flux { get; set; } = null!;
    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;

    private IReadOnlyList<Thermostat> _liste = [];
    private readonly HashSet<string> _occupes = new(StringComparer.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        await Connexion.ChargerAsync();
        if (!Connexion.EstConfigure)
        {
            Nav.NavigateTo("/reglages");
            return;
        }

        Live.Changed += Rafraichir;
        Flux.EtatChange += Rafraichir;
        _liste = Client.Radiateurs.Ordonner(Live.Thermostats);
        Flux.Demarrer();
    }

    private void Rafraichir() => InvokeAsync(() =>
    {
        _liste = Client.Radiateurs.Ordonner(Live.Thermostats);
        StateHasChanged();
    });

    private async Task ChoisirAsync(Thermostat thermostat, string mode)
    {
        if (!_occupes.Add(thermostat.EntityId)) return;
        try
        {
            var resultat = await Api.ChoisirModeAsync(thermostat.EntityId, mode);
            if (!resultat.Ok) Snackbar.Add($"{thermostat.Nom} : {resultat.Motif}", Severity.Error);
        }
        finally
        {
            _occupes.Remove(thermostat.EntityId);
        }
    }

    public void Dispose()
    {
        Live.Changed -= Rafraichir;
        Flux.EtatChange -= Rafraichir;
    }
}
