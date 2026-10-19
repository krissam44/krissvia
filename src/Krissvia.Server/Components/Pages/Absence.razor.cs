using System.Globalization;
using Krissvia.Climate.Presence;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// L'absence du foyer : ce qui s'applique maintenant et pourquoi, le forçage, la période programmée, et ce que fait
/// chaque thermostat pendant l'absence.
/// </summary>
public partial class Absence
{
    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    [Inject] private AbsenceService Absences { get; set; } = default!;
    [Inject] private HomePresence Detection { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private DateTime? _departJour;
    private TimeSpan? _departHeure;
    private DateTime? _retourJour;
    private TimeSpan? _retourHeure;
    private bool _annulerSiPresence = true;
    private string? _erreur;
    private CancellationTokenSource? _arret;

    private HomeAbsenceState Etat => Absences.Current;
    private HomePresenceState? Presence => Detection.Current;

    private string Source => Etat.Source switch
    {
        AbsenceSource.Manual => Etat.Away ? "Forcée absente à la main." : "Forcée occupée à la main.",
        AbsenceSource.Scheduled => $"Absence programmée, retour {Heure(Etat.Return!.Value)}.",
        AbsenceSource.Detection => "Détection de présence : tous les appareils suivis sont partis.",
        _ => "Détection de présence.",
    };

    protected override void OnInitialized()
    {
        // Le formulaire reprend la période enregistrée, sinon propose demain matin → dans une semaine au soir.
        var local = Horloge.GetLocalNow().DateTime;
        if (Absences.Period is { } p)
        {
            (_departJour, _departHeure) = Decouper(p.Departure);
            (_retourJour, _retourHeure) = Decouper(p.Return);
            _annulerSiPresence = p.CancelOnPresence;
        }
        else
        {
            (_departJour, _departHeure) = (local.Date.AddDays(1), new TimeSpan(8, 0, 0));
            (_retourJour, _retourHeure) = (local.Date.AddDays(8), new TimeSpan(18, 0, 0));
        }

        Absences.Changed += Rafraichir;
        _arret = new CancellationTokenSource();
        _ = SuivreAsync(_arret.Token);
    }

    /// <summary>La présence n'a pas d'évènement : relue toutes les 10 s, au rythme de son calcul.</summary>
    private async Task SuivreAsync(CancellationToken ct)
    {
        using var minuterie = new PeriodicTimer(TimeSpan.FromSeconds(10), Horloge);
        try
        {
            while (await minuterie.WaitForNextTickAsync(ct)) await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Rafraichir() => _ = InvokeAsync(StateHasChanged);

    private string Forcage => Absences.Forced switch { true => "absent", false => "present", null => "auto" };

    private Task ForcerAsync(string choix) => Absences.ForceAsync(choix switch { "absent" => true, "present" => false, _ => null });

    private async Task ProgrammerAsync()
    {
        _erreur = null;
        if (_departJour is not { } dj || _departHeure is not { } dh || _retourJour is not { } rj || _retourHeure is not { } rh)
        {
            _erreur = "Indiquez le jour et l'heure du départ et du retour.";
            return;
        }

        try
        {
            await Absences.ScheduleAsync(Instant(dj.Date + dh), Instant(rj.Date + rh), _annulerSiPresence);
            Snackbar.Add("Absence programmée.", Severity.Success);
        }
        catch (AbsenceValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private async Task SupprimerAsync()
    {
        await Absences.ClearAsync();
        Snackbar.Add("Absence programmée supprimée.", Severity.Info);
    }

    private string Avancement(ScheduledAbsence p)
    {
        var maintenant = Horloge.GetUtcNow();
        if (p.Cancelled) return "annulée : retour anticipé.";
        if (maintenant < p.Departure) return "à venir.";
        return p.DepartureSeen ? "en cours, départ constaté." : "en cours, départ pas encore constaté.";
    }

    /// <summary>Une heure locale saisie, en instant (le fuseau de la maison, celui du serveur).</summary>
    private DateTimeOffset Instant(DateTime local) => new(local, Horloge.LocalTimeZone.GetUtcOffset(local));

    private (DateTime?, TimeSpan?) Decouper(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone).DateTime;
        return (local.Date, local.TimeOfDay);
    }

    private string Heure(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone).ToString("ddd d MMM HH:mm", Francais);

    public void Dispose()
    {
        Absences.Changed -= Rafraichir;
        _arret?.Cancel();
        _arret?.Dispose();
    }
}
