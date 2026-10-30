using System.Globalization;
using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// Les compteurs (<c>GET /api/energy</c>) : puissance et tension du moment, courbe de puissance du jour, consommation
/// depuis minuit et des sept derniers jours, coût si le prix du kWh est renseigné sur le web. Tout est calculé par le
/// serveur, d'après son historique. Relu toutes les 30 s et au retour au premier plan.
/// </summary>
public partial class Energie : IDisposable
{
    private const int Jours = 7;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(30);

    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private ReveilAppli Reveil { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;

    private VueEnergie? _vue;
    private string? _erreur;
    private bool _serveurAncien;
    private bool _charge;
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
            var (vue, erreur, ancien) = await Api.EnergieAsync(Jours, _fin.Token);
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

    // La courbe du jour : marge des graduations, largeur et hauteur du tracé (unités du viewBox, mis à l'échelle de l'écran).
    private const double G = 36, L = 300, H = 110;

    private static CourbeJour Tracer(CompteurEnergie c) => CourbeJour.Tracer(c.Courbe, L, H);

    /// <summary>Un libellé SVG : Razor réserve la balise &lt;text&gt;, on l'écrit donc en balisage brut (texte échappé).</summary>
    private static MarkupString Texte(double x, double y, string ancre, string texte) =>
        new($"<text x=\"{N(x)}\" y=\"{N(y)}\" text-anchor=\"{ancre}\">{System.Net.WebUtility.HtmlEncode(texte)}</text>");

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Kwh(double kwh) => kwh.ToString(kwh >= 100 ? "#,0" : "0.##", Fr);

    public void Dispose()
    {
        Reveil.Reveillee -= SurReveil;
        _fin.Cancel();
        _fin.Dispose();
    }
}
