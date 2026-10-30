using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Mobile.Components.Pages;

/// <summary>
/// L'alarme : son état, le décompte d'une sortie ou d'une entrée, les défauts qui empêcheraient d'armer, et un clavier
/// pour armer ou désarmer PAR CODE (la clé d'API ne suffit pas, ALARME.md §6). L'écran se relit chaque seconde pendant
/// un décompte, toutes les 3 s sinon : une alarme qui se déclenche doit se voir sans geste.
/// </summary>
/// <remarks>
/// Le code ne s'affiche jamais, s'efface à l'envoi et après 30 s sans frappe — comme la carte du tableau de bord.
/// </remarks>
public partial class Alarme : IDisposable
{
    private static readonly string[] Touches = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "", "0", "⌫"];
    private static readonly TimeSpan OubliCode = TimeSpan.FromSeconds(30);
    private const int LongueurMax = 12;

    [Inject] private KrissviaApi Api { get; set; } = null!;
    [Inject] private ConnexionSettings Connexion { get; set; } = null!;
    [Inject] private ReveilAppli Reveil { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;

    private EtatAlarme? _etat;
    private string? _erreur;
    private string _code = string.Empty;
    private DateTime _derniereFrappe;
    private bool _envoi;
    private bool _charge;
    private ResultatCode? _resultat;
    private readonly CancellationTokenSource _fin = new();

    private bool ClavierInactif => _envoi || _etat is not { Activee: true } || _etat.BloqueSecondes > 0;

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
            var (etat, erreur) = await Api.AlarmeAsync(_fin.Token);
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
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_etat is { EnDecompte: true } ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), ct);
                await InvokeAsync(async () =>
                {
                    if (_code.Length > 0 && DateTime.UtcNow - _derniereFrappe > OubliCode) _code = string.Empty;
                    await ActualiserAsync();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Écran quitté.
        }
    }

    private void Taper(string touche)
    {
        _resultat = null;
        _derniereFrappe = DateTime.UtcNow;
        if (touche == "⌫")
        {
            if (_code.Length > 0) _code = _code[..^1];
        }
        else if (_code.Length < LongueurMax)
        {
            _code += touche;
        }
    }

    private async Task EnvoyerAsync()
    {
        if (_etat is null || _code.Length < 4 || _envoi) return;
        _envoi = true;
        var code = _code;
        _code = string.Empty;   // effacé dès l'envoi, quelle que soit la réponse
        try
        {
            _resultat = await Api.CommanderAlarmeAsync(_etat.Desarmee, code);
            await ChargerAsync();
        }
        finally
        {
            _envoi = false;
        }
    }

    private void SurReveil() => InvokeAsync(ActualiserAsync);

    private static string Couleur(EtatAlarme e) => e.Etat switch
    {
        "disarmed" => "#66bb6a",
        "exit_delay" or "entry_delay" => "#ffa726",
        "armed" => "#42a5f5",
        "triggered" => "#ef5350",
        _ => "#78909c",
    };

    private static string Icone(EtatAlarme e) => e.Etat switch
    {
        "disarmed" => Icons.Material.Filled.LockOpen,
        "exit_delay" or "entry_delay" => Icons.Material.Filled.HourglassTop,
        "armed" => Icons.Material.Filled.Lock,
        "triggered" => Icons.Material.Filled.NotificationsActive,
        _ => Icons.Material.Filled.Security,
    };

    public void Dispose()
    {
        Reveil.Reveillee -= SurReveil;
        _fin.Cancel();
        _fin.Dispose();
    }
}
