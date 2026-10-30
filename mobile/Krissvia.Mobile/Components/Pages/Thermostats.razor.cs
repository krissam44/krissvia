using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// Les thermostats, tenus à jour par le flux en direct. Aucun état optimiste : un ordre accepté ne change rien à
/// l'écran, c'est l'état publié qui arrive par le flux une seconde plus tard. Un ordre refusé le dit, avec le motif du
/// serveur ; un ordre réussi ne dit rien (choix validé sur l'appli DomoMud : le toast gênait l'enchaînement).
/// </summary>
public partial class Thermostats
{
    [Inject] private ThermostatsEnDirect Live { get; set; } = null!;
    [Inject] private FluxEnDirect Flux { get; set; } = null!;
    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;

    private IReadOnlyList<Thermostat> _cartes = [];
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
        _cartes = Live.Thermostats;
        Flux.Demarrer();
    }

    private void Rafraichir() => InvokeAsync(() =>
    {
        _cartes = Live.Thermostats;
        StateHasChanged();
    });

    private void Actualiser() => Flux.Reveiller();

    private async Task OrdonnerAsync(Thermostat thermostat, Func<KrissviaApi, Task<ResultatOrdre>> ordre)
    {
        if (!_occupes.Add(thermostat.EntityId)) return;
        try
        {
            var resultat = await ordre(Api);
            if (!resultat.Ok) Snackbar.Add($"{thermostat.Nom} : {resultat.Motif}", Severity.Error);
        }
        finally
        {
            _occupes.Remove(thermostat.EntityId);
        }
    }

    public void Dispose()
    {
        // Le flux, lui, continue : il est partagé, et revenir sur l'écran doit montrer des valeurs déjà à jour.
        Live.Changed -= Rafraichir;
        Flux.EtatChange -= Rafraichir;
    }
}
