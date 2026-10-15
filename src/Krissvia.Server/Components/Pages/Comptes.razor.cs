using Krissvia.Core.Accounts;
using Microsoft.AspNetCore.Components.Authorization;

namespace Krissvia.Server.Components.Pages;

/// <summary>Comptes de l'interface : liste, création, suppression (ni le sien, ni le dernier), mot de passe.</summary>
public partial class Comptes
{
    [Inject] private AccountService Service { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState> Etat { get; set; } = default!;

    private string _moi = "";
    private string? _nouvelIdentifiant;
    private string? _nouveauMotDePasse;
    private string? _actuel;
    private string? _nouveau;
    private string? _confirmation;

    protected override async Task OnInitializedAsync() =>
        _moi = (await Etat).User.Identity?.Name ?? "";

    private async Task CreerAsync()
    {
        if (await Service.CreateAsync(_nouvelIdentifiant?.Trim() ?? "", _nouveauMotDePasse ?? "") is { } refus)
        {
            Snackbar.Add(Phrase(refus), Severity.Warning);
            return;
        }

        Snackbar.Add($"Compte « {_nouvelIdentifiant} » créé.", Severity.Success);
        _nouvelIdentifiant = _nouveauMotDePasse = null;
    }

    private async Task SupprimerAsync(string compte)
    {
        if (await Service.DeleteAsync(compte, _moi) is { } refus) Snackbar.Add(Phrase(refus), Severity.Warning);
        else Snackbar.Add($"Compte « {compte} » supprimé.", Severity.Info);
    }

    private async Task ChangerAsync()
    {
        if (_nouveau != _confirmation)
        {
            Snackbar.Add("Les deux nouveaux mots de passe diffèrent.", Severity.Warning);
            return;
        }

        if (await Service.ChangePasswordAsync(_moi, _actuel ?? "", _nouveau ?? "") is { } refus)
        {
            Snackbar.Add(Phrase(refus), Severity.Warning);
            return;
        }

        Snackbar.Add("Mot de passe changé.", Severity.Success);
        _actuel = _nouveau = _confirmation = null;
    }

    // Les refus d'AccountService sont des fragments (« mot de passe actuel faux. ») : on en fait une phrase.
    private static string Phrase(string refus) => char.ToUpperInvariant(refus[0]) + refus[1..];
}
