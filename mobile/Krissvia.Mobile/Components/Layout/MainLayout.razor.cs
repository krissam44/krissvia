using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace Krissvia.Mobile.Components.Layout;

/// <summary>
/// Barre du haut (lieu affiché, thème, lieux) et navigation basse. Avec plusieurs lieux, le titre est le nom du lieu et
/// ouvre le menu pour passer à l'autre.
/// </summary>
public partial class MainLayout
{
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;

    private bool _sombre = true;

    private sealed record Onglet(string Route, string Libelle, string Icone);

    private static readonly Onglet[] Onglets =
    [
        new("/", "Thermostats", Icons.Material.Filled.ThermostatAuto),
        new("/radiateurs", "Radiateurs", Icons.Material.Filled.LocalFireDepartment),
        new("/sondes", "Sondes", Icons.Material.Filled.DeviceThermostat),
        new("/prises", "Prises", Icons.Material.Filled.Power),
        new("/alarme", "Alarme", Icons.Material.Filled.Security),
        new("/energie", "Énergie", Icons.Material.Filled.ElectricBolt),
    ];

    protected override async Task OnInitializedAsync()
    {
        Nav.LocationChanged += Rafraichir;
        Connexion.LieuChange += SurLieuChange;
        await Connexion.ChargerAsync();
    }

    private void SurLieuChange() => InvokeAsync(StateHasChanged);

    private Task ChoisirAsync(string id) => Connexion.ChoisirAsync(id);

    private void Rafraichir(object? sender, LocationChangedEventArgs e) => InvokeAsync(StateHasChanged);

    private bool EstActif(string route)
    {
        var courant = "/" + Nav.ToBaseRelativePath(Nav.Uri).Split('?')[0].TrimStart('/');
        return route == "/" ? courant == "/" : courant.StartsWith(route, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Nav.LocationChanged -= Rafraichir;
        Connexion.LieuChange -= SurLieuChange;
    }
}
