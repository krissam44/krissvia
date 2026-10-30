using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// Interrupteurs et lumières par pièce (<c>GET /api/actuators</c>), basculés par un ordre vérifié. Pas d'état
/// optimiste — l'appli de DomoMud affichait l'état voulu tout de suite — : la ligne attend la réponse, puis l'écran se
/// relit. Un radiateur de thermostat ne se commande pas d'ici ; rallumer un équipement délesté se confirme d'abord.
/// </summary>
public partial class Prises : IDisposable
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(30);

    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private ReveilAppli Reveil { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IDialogService Dialogues { get; set; } = null!;

    private VueActionneurs? _vue;
    private string? _erreur;
    private bool _serveurAncien;
    private bool _charge;
    private readonly HashSet<string> _enAttente = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _fin = new();

    protected override async Task OnInitializedAsync()
    {
        await Connexion.ChargerAsync();
        if (!Connexion.EstConfigure)
        {
            Nav.NavigateTo("/reglages");
            return;
        }

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
            var (vue, erreur, ancien) = await Api.ActionneursAsync(_fin.Token);
            if (vue is not null) _vue = vue;
            _erreur = erreur;
            _serveurAncien = ancien;
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

    private async Task BasculerAsync(Actionneur a)
    {
        if (!a.Commandable || !_enAttente.Add(a.EntityId)) return;
        try
        {
            var allumer = a.Allume != true;
            if (allumer && a.Deleste && await Dialogues.ShowMessageBoxAsync("Équipement délesté",
                    $"« {a.Nom} » a été coupé par le délesteur. Le rallumer peut faire disjoncter. Le rallumer quand même ?",
                    yesText: "Rallumer", cancelText: "Annuler") != true)
                return;

            var resultat = await Api.AllumerAsync(a.EntityId, allumer);
            if (!resultat.Ok) Snackbar.Add($"{a.Nom} : {resultat.Motif}", Severity.Error);
            else if (resultat.NonConfirme) Snackbar.Add($"{a.Nom} : {resultat.Motif}.", Severity.Warning);

            // L'état affiché est celui que le serveur relit, pas celui qu'on a demandé.
            await ChargerAsync();
        }
        finally
        {
            _enAttente.Remove(a.EntityId);
        }
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

    private void SurReveil() => InvokeAsync(ActualiserAsync);

    /// <summary>L'icône selon l'usage choisi dans l'écran Entités, comme sur le web.</summary>
    private static string Icone(Actionneur a) =>
        a.PiloteePar is not null || a.Usage == "radiateur" ? Icons.Material.Filled.Whatshot
        : a.Usage == "eclairage" ? (a.Allume == true ? Icons.Material.Filled.Lightbulb : Icons.Material.Outlined.Lightbulb)
        : a.Usage == "prise" ? (a.Allume == true ? Icons.Material.Filled.Power : Icons.Material.Outlined.Power)
        : a.Usage == "compteur" ? Icons.Material.Filled.ElectricMeter
        : a.Allume == true ? Icons.Material.Filled.ToggleOn : Icons.Material.Filled.ToggleOff;

    public void Dispose()
    {
        Reveil.Reveillee -= SurReveil;
        _fin.Cancel();
        _fin.Dispose();
    }
}
