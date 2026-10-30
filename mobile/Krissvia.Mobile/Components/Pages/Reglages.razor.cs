using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// Les lieux : une installation Krissvia chacun (l'appartement, la maison), avec son adresse et sa clé d'API. Un appui
/// affiche un lieu ; enregistrer un lieu l'affiche aussi. Sans aucun lieu, l'écran s'ouvre directement sur le
/// formulaire : c'est le premier lancement.
/// </summary>
public partial class Reglages
{
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private HttpClient Http { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;

    /// <summary>Le lieu en cours de saisie ; null : la liste.</summary>
    private Edition? _edition;
    private string? _aSupprimer;
    private bool _visible;
    private bool _occupe;
    private bool _succes;
    private string? _resultat;

    private sealed class Edition
    {
        public string? Id { get; init; }
        public string Nom { get; set; } = string.Empty;
        public string Adresse { get; set; } = string.Empty;
        public string Cle { get; set; } = string.Empty;
    }

    /// <summary>Une connexion d'essai : « Tester » vérifie ce qui est saisi sans rien enregistrer ni changer de lieu.</summary>
    private sealed record ConnexionEssai(string BaseUrl, string ApiKey) : IConnexion;

    protected override async Task OnInitializedAsync()
    {
        await Connexion.ChargerAsync();
        if (Connexion.Lieux.Count == 0) Ajouter();
    }

    private void Ajouter()
    {
        _aSupprimer = null;
        _resultat = null;
        _edition = new Edition();
    }

    private void Annuler()
    {
        _edition = null;
        _resultat = null;
    }

    private async Task ModifierAsync(Lieu lieu)
    {
        _aSupprimer = null;
        _resultat = null;
        _edition = new Edition { Id = lieu.Id, Nom = lieu.Nom, Adresse = lieu.BaseUrl, Cle = await Connexion.CleDeAsync(lieu.Id) };
    }

    private async Task ChoisirAsync(Lieu lieu)
    {
        _aSupprimer = null;
        await Connexion.ChoisirAsync(lieu.Id);
        Nav.NavigateTo("/");
    }

    private async Task SupprimerAsync(Lieu lieu)
    {
        _aSupprimer = null;
        await Connexion.SupprimerAsync(lieu.Id);
        if (Connexion.Lieux.Count == 0) Ajouter();
    }

    /// <summary>
    /// Enregistre puis essaie ce qui est affiché. Un lieu injoignable s'enregistre quand même — la maison n'est
    /// peut-être pas encore branchée — mais on reste sur le formulaire pour lire pourquoi.
    /// </summary>
    private async Task EnregistrerAsync()
    {
        if (_edition is null) return;
        _occupe = true;
        _resultat = null;
        try
        {
            var lieu = await Connexion.EnregistrerAsync(_edition.Id, _edition.Nom, _edition.Adresse, _edition.Cle);
            var erreur = await EssayerAsync(Connexion.BaseUrl, Connexion.ApiKey);
            if (erreur is null)
            {
                _edition = null;
                Nav.NavigateTo("/");
                return;
            }
            // Le formulaire continue sur le lieu créé : un second « Enregistrer » ne doit pas en créer un autre.
            _edition = new Edition { Id = lieu.Id, Nom = lieu.Nom, Adresse = lieu.BaseUrl, Cle = _edition.Cle };
            _succes = false;
            _resultat = $"Enregistré, mais la connexion échoue : {erreur}";
        }
        finally
        {
            _occupe = false;
        }
    }

    private async Task TesterAsync()
    {
        if (_edition is null) return;
        _occupe = true;
        _resultat = null;
        try
        {
            var erreur = await EssayerAsync(AdresseServeur.Normaliser(_edition.Adresse), _edition.Cle.Trim());
            _succes = erreur is null;
            _resultat = _succes ? "Connexion réussie." : erreur;
        }
        finally
        {
            _occupe = false;
        }
    }

    private Task<string?> EssayerAsync(string adresse, string cle) =>
        new KrissviaApi(Http, new ConnexionEssai(adresse, cle)).TesterAsync();
}
