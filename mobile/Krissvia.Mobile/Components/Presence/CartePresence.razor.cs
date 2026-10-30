using System.Globalization;
using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Mobile.Components.Presence;

/// <summary>
/// La présence du foyer, comme la carte « Maison occupée » de l'écran Absence du web : ce qui s'applique aux
/// thermostats et qui en décide, le forçage Automatique / Présent / Absent, et les appareils suivis. Relue toutes les
/// 10 s, au retour au premier plan, et aussitôt après un forçage.
/// </summary>
public partial class CartePresence : IDisposable
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(10);
    private static readonly (Forcage, string)[] Choix =
        [(Forcage.Automatique, "Automatique"), (Forcage.Present, "Présent"), (Forcage.Absent, "Absent")];

    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ReveilAppli Reveil { get; set; } = null!;

    private EtatPresence? _etat;
    private string? _erreur;
    private bool _envoi;
    private bool _charge;
    private readonly CancellationTokenSource _fin = new();

    protected override async Task OnInitializedAsync()
    {
        Reveil.Reveillee += SurReveil;
        await ChargerAsync();
        _ = BoucleAsync(_fin.Token);
    }

    private async Task ChargerAsync()
    {
        if (_charge) return;
        _charge = true;
        try
        {
            var (etat, erreur) = await Api.PresenceAsync(_fin.Token);
            if (etat is not null) _etat = etat;
            _erreur = erreur;
        }
        catch (OperationCanceledException)
        {
            // Écran quitté.
        }
        finally
        {
            _charge = false;
        }
    }

    private async Task ActualiserAsync()
    {
        await ChargerAsync();
        StateHasChanged();
    }

    private async Task BoucleAsync(CancellationToken ct)
    {
        try
        {
            using var minuterie = new PeriodicTimer(Intervalle);
            while (await minuterie.WaitForNextTickAsync(ct))
                await InvokeAsync(ActualiserAsync);
        }
        catch (OperationCanceledException)
        {
            // Écran quitté.
        }
    }

    /// <summary>Pas d'état optimiste : le bouton choisi est celui que le serveur renvoie à la relecture.</summary>
    private async Task ForcerAsync(Forcage forcage)
    {
        if (_envoi || _etat?.Forcage == forcage) return;
        _envoi = true;
        try
        {
            var resultat = await Api.ForcerAbsenceAsync(forcage, _fin.Token);
            await ChargerAsync();
            if (!resultat.Ok) _erreur = resultat.Motif;
        }
        catch (OperationCanceledException)
        {
            // Écran quitté.
        }
        finally
        {
            _envoi = false;
        }
    }

    private void SurReveil() => InvokeAsync(ActualiserAsync);

    private static string Date(DateTimeOffset d) => d.ToLocalTime().ToString("ddd dd/MM HH:mm", Fr);

    private static string Couleur(EtatPresence p) =>
        p.Absente ? (p.Source == "scheduled" ? "#ab47bc" : "#78909c") : p.DetectionPerdue ? "#ffa726" : "#66bb6a";

    private static string Icone(EtatPresence p) =>
        p.Absente ? (p.Source == "scheduled" ? Icons.Material.Filled.Luggage : Icons.Material.Filled.DoorFront)
        : p.DetectionPerdue ? Icons.Material.Filled.HelpOutline
        : Icons.Material.Filled.Home;

    public void Dispose()
    {
        Reveil.Reveillee -= SurReveil;
        _fin.Cancel();
        _fin.Dispose();
    }
}
