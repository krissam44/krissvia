using System.Globalization;
using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Mobile.Components.Presence;

/// <summary>
/// Les ouvertures et les détecteurs de mouvement, par pièce (<c>GET /api/detectors</c>) : ce qui est ouvert, où ça
/// bouge, depuis quand, et ce que surveille l'alarme. Sous la présence, sur la page Alarme : avant d'armer en partant,
/// on veut voir la fenêtre restée ouverte. Relue toutes les 10 s et au retour au premier plan.
/// </summary>
public partial class CarteDetecteurs : IDisposable
{
    private const double PileFaible = 20;
    private const string Orange = "#ffa726", Bleu = "#29b6f6", Vert = "#66bb6a", Gris = "#9e9e9e";
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(10);

    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ReveilAppli Reveil { get; set; } = null!;

    private VueDetecteurs? _vue;
    private string? _erreur;
    private bool _serveurAncien;
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
            var (vue, erreur, ancien) = await Api.DetecteursAsync(_fin.Token);
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

    private static string Bilan(VueDetecteurs v)
    {
        if (v.Detecteurs.Count == 0) return "";
        var morceaux = new List<string>();
        if (v.Ouverts > 0) morceaux.Add(v.Ouverts == 1 ? "1 ouvert" : $"{v.Ouverts} ouverts");
        if (v.EnMouvement > 0) morceaux.Add(v.EnMouvement == 1 ? "1 mouvement" : $"{v.EnMouvement} mouvements");
        var muets = v.Detecteurs.Count(d => d.Actif is null);
        if (muets > 0) morceaux.Add($"{muets} sans nouvelles");
        return morceaux.Count == 0 ? "Tout est fermé, aucun mouvement." : string.Join(" · ", morceaux);
    }

    private static string CouleurBilan(VueDetecteurs v) =>
        v.Ouverts > 0 ? Orange : v.EnMouvement > 0 ? Bleu : v.Detecteurs.Any(d => d.Actif is null) ? Gris : Vert;

    /// <summary>Orange : ouvert. Bleu : mouvement. Vert : fermé ou calme. Gris : état inconnu — jamais « fermé » par défaut.</summary>
    private static string Couleur(Detecteur d) => d.Actif switch
    {
        null => Gris,
        true => d.Mouvement ? Bleu : Orange,
        false => Vert,
    };

    private static string Icone(Detecteur d) => (d.Mouvement, d.Actif) switch
    {
        (_, null) => Icons.Material.Filled.SensorsOff,
        (true, true) => Icons.Material.Filled.DirectionsRun,
        (true, false) => Icons.Material.Filled.Sensors,
        (false, true) => Icons.Material.Filled.MeetingRoom,
        (false, false) => Icons.Material.Filled.DoorFront,
    };

    /// <summary>« 14:02 » aujourd'hui, « hier 22:15 », sinon « ven. 14/11 09:30 ».</summary>
    private static string Depuis(DateTimeOffset instant)
    {
        var local = instant.ToLocalTime();
        var jours = (DateTime.Today - local.Date).Days;
        return jours switch
        {
            0 => local.ToString("HH:mm", Fr),
            1 => $"hier {local.ToString("HH:mm", Fr)}",
            _ => local.ToString("ddd dd/MM HH:mm", Fr),
        };
    }

    public void Dispose()
    {
        Reveil.Reveillee -= SurReveil;
        _fin.Cancel();
        _fin.Dispose();
    }
}
