using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>Adresse et clé d'API : le seul réglage de l'appli. Enregistrer teste aussitôt, et rouvre le flux.</summary>
public partial class Reglages
{
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private FluxEnDirect Flux { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;

    private string _adresse = string.Empty;
    private string _cle = string.Empty;
    private bool _visible;
    private bool _occupe;
    private bool _succes;
    private string? _resultat;

    protected override async Task OnInitializedAsync()
    {
        await Connexion.ChargerAsync();
        _adresse = Connexion.BaseUrl;
        _cle = Connexion.ApiKey;
    }

    private async Task EnregistrerAsync()
    {
        if (await EssayerAsync())
        {
            // Le flux lit la connexion à chaque session : il suffit de le relancer.
            Flux.Reveiller();
            Nav.NavigateTo("/");
        }
    }

    private Task TesterAsync() => EssayerAsync();

    /// <summary>Enregistre ce qui est affiché (normalisé), puis l'essaie : on teste ce que l'appli utilisera.</summary>
    private async Task<bool> EssayerAsync()
    {
        _occupe = true;
        _resultat = null;
        try
        {
            await Connexion.EnregistrerAsync(_adresse, _cle);
            _adresse = Connexion.BaseUrl;
            var erreur = await Api.TesterAsync();
            _succes = erreur is null;
            _resultat = _succes ? "Connexion réussie." : erreur;
            return _succes;
        }
        finally
        {
            _occupe = false;
        }
    }
}
