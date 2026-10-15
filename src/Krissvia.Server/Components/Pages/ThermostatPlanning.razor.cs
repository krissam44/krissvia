using System.Globalization;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Éditeur du planning d'un thermostat : créneaux par jour, mode hors créneau, copie d'un jour vers d'autres, et un
/// aperçu de la semaine.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>L'aperçu interroge <see cref="WeeklySchedule.ModeAt"/> quart d'heure par quart d'heure au lieu de dessiner
/// les créneaux saisis : il montre ce que le moteur fera, chevauchements et créneaux de nuit compris, pas ce qu'on
/// croit avoir saisi.</item>
/// <item>L'enregistrement passe par <see cref="ThermostatService.SetScheduleAsync"/>, qui ne touche que le planning :
/// un mode choisi sur la carte pendant que cette page était ouverte n'est pas écrasé.</item>
/// </list>
/// </remarks>
public partial class ThermostatPlanning
{
    private const int Pas = 15;   // minutes, la résolution de l'aperçu

    [Parameter] public string Id { get; set; } = "";

    [Inject] private ThermostatService Service { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private ThermostatSettings? _thermostat;
    private ThermostatMode _modeParDefaut;
    private Dictionary<DayOfWeek, List<CreneauSaisi>> _jours = [];
    private string? _erreur;

    private string Nom => _thermostat?.Name is { Length: > 0 } n ? n : Id;

    protected override void OnParametersSet() => Recharger();

    private void Recharger()
    {
        _erreur = null;
        _thermostat = Service.Get(Id);
        var planning = _thermostat?.Schedule ?? new WeeklySchedule();
        _modeParDefaut = planning.DefaultMode;
        _jours = Jours.Semaine.ToDictionary(j => j, j => planning.Days.TryGetValue(j, out var creneaux)
            ? creneaux.OrderBy(c => c.Start).Select(CreneauSaisi.Depuis).ToList()
            : []);
    }

    private void Ajouter(DayOfWeek jour)
    {
        // Un créneau qui suit le dernier, pour ne pas en créer un qui chevauche d'emblée.
        var dernier = _jours[jour].Select(c => c.Lire()).Where(c => c is not null).Select(c => c!.End).DefaultIfEmpty(TimeSpan.FromHours(6)).Max();
        var debut = dernier == TimeSpan.Zero ? TimeSpan.FromHours(6) : dernier;
        var fin = debut + TimeSpan.FromHours(2) >= TimeSpan.FromHours(24) ? TimeSpan.Zero : debut + TimeSpan.FromHours(2);
        _jours[jour].Add(new CreneauSaisi { Debut = Heure(debut), Fin = Heure(fin), Mode = ThermostatMode.Comfort });
    }

    private void Copier(DayOfWeek source, DayOfWeek[] cibles)
    {
        foreach (var cible in cibles.Where(c => c != source))
            _jours[cible] = _jours[source].Select(c => c.Copie()).ToList();
        Snackbar.Add($"Créneaux du {Jours.Nom(source)} copiés. Pensez à enregistrer.", Severity.Info);
    }

    /// <summary>Le planning tel que saisi, ou null s'il reste une heure illisible.</summary>
    private WeeklySchedule? Saisi()
    {
        var jours = new Dictionary<DayOfWeek, IReadOnlyList<ScheduleSlot>>();
        foreach (var (jour, creneaux) in _jours)
        {
            var lus = creneaux.Select(c => c.Lire()).ToList();
            if (lus.Any(c => c is null)) return null;
            if (lus.Count > 0) jours[jour] = lus.Select(c => c!).OrderBy(c => c.Start).ToList();
        }

        return new WeeklySchedule { Days = jours, DefaultMode = _modeParDefaut };
    }

    /// <summary>Les plages de la journée, en minutes, regroupées par mode : la question est posée au planning lui-même.</summary>
    private IEnumerable<(int Debut, int Fin, ThermostatMode Mode)> Apercu(DayOfWeek jour)
    {
        var planning = Saisi();
        if (planning is null) yield break;

        // N'importe quelle date de ce jour de la semaine convient : le planning ne regarde que le jour et l'heure.
        var date = new DateTime(2026, 1, 5).AddDays(((int)jour + 6) % 7);   // lundi 5 janvier 2026 + décalage
        var debut = 0;
        var mode = planning.ModeAt(date);
        for (var minute = Pas; minute <= 1440; minute += Pas)
        {
            var suivant = minute < 1440 ? planning.ModeAt(date.AddMinutes(minute)) : (ThermostatMode?)null;
            if (suivant == mode) continue;
            yield return (debut, minute, mode);
            debut = minute;
            if (suivant is { } m) mode = m;
        }
    }

    private IEnumerable<string> Alertes(DayOfWeek jour)
    {
        if (_jours[jour].Any(c => c.Lire() is null))
        {
            yield return "Une heure est incomplète.";
            yield break;
        }

        var planning = Saisi();
        if (planning is null) yield break;

        foreach (var c in _jours[jour].Select(c => c.Lire()!).Where(c => c.Start == c.End && c.End != TimeSpan.Zero))
            yield return $"Le créneau de {Heure(c.Start)} commence et finit à la même heure : il ne durerait rien.";

        foreach (var o in planning.Overlaps(jour))
        {
            var gagnant = o.WinnerDay == jour
                ? $"celui de {Heure(o.Winner.Start)}"
                : $"la nuit du {Jours.Nom(o.WinnerDay)} ({Heure(o.Winner.Start)}–{Heure(o.Winner.End)})";
            var autre = o.WinnerDay == jour
                ? $"Les créneaux {Heure(o.Winner.Start)}–{Heure(o.Winner.End)} et {Heure(o.Loser.Start)}–{Heure(o.Loser.End)} se chevauchent"
                : $"Le créneau {Heure(o.Loser.Start)}–{Heure(o.Loser.End)} chevauche la nuit du {Jours.Nom(o.WinnerDay)}";
            yield return $"{autre} : là où ils se recouvrent, {gagnant} l'emporte ({LibellesThermostat.Mode(o.Winner.Mode)}).";
        }
    }

    private async Task EnregistrerAsync()
    {
        if (Saisi() is not { } planning)
        {
            _erreur = "Une heure est incomplète : complétez-la ou supprimez le créneau.";
            return;
        }

        try
        {
            if (await Service.SetScheduleAsync(Id, planning) is null)
            {
                _erreur = "Ce thermostat n'existe plus.";
                return;
            }

            Snackbar.Add(_thermostat!.ScheduleActive
                ? "Planning enregistré : il s'applique tout de suite."
                : "Planning enregistré. Il s'appliquera quand vous l'activerez sur la carte du thermostat.", Severity.Success);
            Recharger();
        }
        catch (ThermostatValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private static bool Nuit(CreneauSaisi c) => c.Lire() is { CrossesMidnight: true };

    private static string Heure(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static string Heure(int minutes) => minutes >= 1440 ? "24:00" : Heure(TimeSpan.FromMinutes(minutes));

    /// <summary>Position CSS d'une minute de la journée. En culture invariante : « 33,333% » n'est pas du CSS, le bloc disparaîtrait.</summary>
    private static string Pourcent(int minutes) => (minutes / 14.4).ToString("0.###", CultureInfo.InvariantCulture) + "%";

    private static string Abrege(DayOfWeek jour) => Majuscule(Jours.Nom(jour)[..3]);

    private static string Majuscule(string texte) => Phrases.Majuscule(texte);

    /// <summary>Un créneau en cours de saisie : les heures restent du texte tant que le champ n'est pas complet.</summary>
    private sealed class CreneauSaisi
    {
        public string? Debut { get; set; }
        public string? Fin { get; set; }
        public ThermostatMode Mode { get; set; }

        public static CreneauSaisi Depuis(ScheduleSlot c) => new() { Debut = Heure(c.Start), Fin = Heure(c.End), Mode = c.Mode };

        public CreneauSaisi Copie() => new() { Debut = Debut, Fin = Fin, Mode = Mode };

        public ScheduleSlot? Lire() =>
            LireHeure(Debut) is { } debut && LireHeure(Fin) is { } fin ? new ScheduleSlot(debut, fin, Mode) : null;

        // Le champ rend « 07:30 » à l'ouverture mais « 07:30:00 » après une saisie : les deux, secondes ignorées.
        private static TimeSpan? LireHeure(string? texte) =>
            TimeSpan.TryParseExact(texte, [@"hh\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var t)
                ? new TimeSpan(t.Hours, t.Minutes, 0)
                : null;
    }
}
