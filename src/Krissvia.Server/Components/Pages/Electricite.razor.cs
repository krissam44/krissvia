using Krissvia.Contracts;
using Krissvia.Core.History;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Consommation électrique, comme l'écran de DomoMud : une carte par compteur (un appareil qui publie un index en kWh),
/// tirée de l'historique — rien de plus à enregistrer, l'index et la puissance le sont déjà à la minute.
/// </summary>
public partial class Electricite
{
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ElectricityPrice Prix { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private List<Compteur> _compteurs = [];
    private double? _prix;

    protected override async Task OnInitializedAsync()
    {
        _prix = await Prix.GetAsync();
        _compteurs = Compteur.Trouver(await Registre.ListAsync(), await Registre.ListDevicesAsync());
    }

    private async Task ChangerPrixAsync(double? prix)
    {
        try
        {
            await Prix.SetAsync(prix);
            _prix = await Prix.GetAsync();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Snackbar.Add(ex.Message.Split(" (")[0], Severity.Warning);
        }
    }
}
