using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// Les sondes, regroupées par pièce avec la moyenne du serveur (<c>GET /api/sensors</c>). Relu toutes les 30 s et au
/// retour au premier plan : une sonde publie toutes les quelques minutes, et la règle « muette depuis 60 min » dépend
/// de l'heure — le flux en direct n'apporterait que de la complexité ici.
/// </summary>
public partial class Sondes : IDisposable
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(30);

    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private ReveilAppli Reveil { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;

    private VueSondes? _vue;
    private string? _erreur;
    private bool _serveurAncien;
    private bool _charge;
    private readonly CancellationTokenSource _fin = new();

    private sealed record Groupe(string Titre, Piece? Piece, IReadOnlyList<Sonde> Sondes);

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
            var (vue, erreur, ancien) = await Api.SondesAsync(_fin.Token);
            // Une erreur garde la dernière vue : le bandeau dit qu'elle n'est plus à jour.
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

    private async Task BoucleAsync(CancellationToken ct)
    {
        try
        {
            using var minuterie = new PeriodicTimer(Intervalle);
            while (await minuterie.WaitForNextTickAsync(ct))
                await InvokeAsync(async () =>
                {
                    await ChargerAsync();
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException)
        {
            // Écran quitté.
        }
    }

    private void SurReveil() => InvokeAsync(async () =>
    {
        await ChargerAsync();
        StateHasChanged();
    });

    /// <summary>Une carte par pièce du serveur, dans son ordre ; les sondes sans pièce à la fin.</summary>
    private IEnumerable<Groupe> Groupes()
    {
        if (_vue is null) yield break;

        foreach (var piece in _vue.Pieces)
        {
            // Le serveur regroupe les pièces sans tenir compte de la casse (« Salon » et « salon » sont la même).
            var sondes = _vue.Sondes.Where(s => string.Equals(s.Piece?.Trim(), piece.Nom, StringComparison.CurrentCultureIgnoreCase)).ToList();
            yield return new Groupe(piece.Nom, piece, sondes);
        }

        var sansPiece = _vue.Sondes.Where(s => s.Piece is null || !_vue.Pieces.Any(p => string.Equals(s.Piece.Trim(), p.Nom, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (sansPiece.Count > 0) yield return new Groupe("Sans pièce", null, sansPiece);
    }

    public void Dispose()
    {
        Reveil.Reveillee -= SurReveil;
        _fin.Cancel();
        _fin.Dispose();
    }
}
