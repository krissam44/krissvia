using System.Globalization;

namespace Krissvia.Server.Components.Planning;

/// <summary>
/// Éditeur de planning hebdomadaire, quelle que soit la valeur portée par les créneaux (mode d'un thermostat,
/// allumé/éteint d'un équipement…).
/// </summary>
/// <remarks>
/// L'aperçu interroge <see cref="WeeklySlots.Active"/> quart d'heure par quart d'heure au lieu de dessiner les
/// créneaux saisis : il montre ce que fera le moteur, créneaux de nuit et chevauchements compris, pas ce qu'on croit
/// avoir saisi.
/// </remarks>
public partial class EditeurPlanning<TValeur>
{
    private const int Pas = 15;   // minutes : la résolution de l'aperçu et des listes de minutes
    private static readonly int[] Minutes = [0, 15, 30, 45];

    [Parameter, EditorRequired] public PlanningSaisi<TValeur> Planning { get; set; } = default!;
    [Parameter, EditorRequired] public IReadOnlyList<TValeur> Valeurs { get; set; } = [];
    [Parameter, EditorRequired] public Func<TValeur, string> Libelle { get; set; } = v => v?.ToString() ?? "";

    /// <summary>Couleur en <c>#rrggbb</c> : les cartes s'en servent en transparence.</summary>
    [Parameter, EditorRequired] public Func<TValeur, string> Couleur { get; set; } = _ => "#9e9e9e";

    /// <summary>Valeur d'un créneau ajouté.</summary>
    [Parameter, EditorRequired] public TValeur NouvelleValeur { get; set; } = default!;

    [Parameter] public string LibelleParDefaut { get; set; } = "Hors créneau";

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private void Ajouter(DayOfWeek jour)
    {
        // À la suite du dernier créneau, pour ne pas en créer un qui chevauche d'emblée.
        var creneaux = Planning.Jours[jour];
        var debut = creneaux.Count == 0 ? TimeSpan.FromHours(6) : creneaux.Max(c => c.End == TimeSpan.Zero ? TimeSpan.FromHours(22) : c.End);
        if (creneaux.Count > 0 && creneaux.Any(c => c.CrossesMidnight())) debut = TimeSpan.FromHours(8);
        var fin = debut + TimeSpan.FromHours(2) >= TimeSpan.FromHours(24) ? TimeSpan.Zero : debut + TimeSpan.FromHours(2);
        creneaux.Add(CreneauSaisi<TValeur>.Depuis(debut, fin, NouvelleValeur));
    }

    private void Copier(DayOfWeek source, DayOfWeek[] cibles)
    {
        foreach (var cible in cibles.Where(c => c != source))
            Planning.Jours[cible] = Planning.Jours[source].Select(c => c.Copie()).ToList();
        Snackbar.Add($"Créneaux du {Jours.Nom(source)} copiés. Pensez à enregistrer.", Severity.Info);
    }

    /// <summary>Les plages de la journée, en minutes, regroupées par valeur : la question est posée au planning lui-même.</summary>
    private IEnumerable<(int Debut, int Fin, TValeur Valeur)> Apercu(DayOfWeek jour)
    {
        var lecture = Planning.Lecture();
        TValeur ValeurA(DateTime t) => WeeklySlots.Active(lecture, t) is { } c ? c.Valeur : Planning.ParDefaut;

        // N'importe quelle date de ce jour de la semaine convient : lundi 5 janvier 2026 + décalage.
        var date = new DateTime(2026, 1, 5).AddDays(Jours.Index(jour));
        var egal = EqualityComparer<TValeur>.Default;
        var debut = 0;
        var valeur = ValeurA(date);
        for (var minute = Pas; minute <= 1440; minute += Pas)
        {
            if (minute < 1440 && egal.Equals(ValeurA(date.AddMinutes(minute)), valeur)) continue;
            yield return (debut, minute, valeur);
            debut = minute;
            if (minute < 1440) valeur = ValeurA(date.AddMinutes(minute));
        }
    }

    private IEnumerable<string> Alertes(DayOfWeek jour)
    {
        foreach (var c in Planning.Jours[jour].Where(c => c.Start == c.End && c.End != TimeSpan.Zero))
            yield return $"Le créneau de {Heure(c.Start)} commence et finit à la même heure : il ne durerait rien.";

        foreach (var o in WeeklySlots.Overlaps(Planning.Lecture(), jour))
        {
            var gagnant = o.WinnerDay == jour
                ? $"celui de {Heure(o.Winner.Start)}"
                : $"la nuit du {Jours.Nom(o.WinnerDay)} ({Heure(o.Winner.Start)}–{Heure(o.Winner.End)})";
            var debut = o.WinnerDay == jour
                ? $"Les créneaux {Heure(o.Winner.Start)}–{Heure(o.Winner.End)} et {Heure(o.Loser.Start)}–{Heure(o.Loser.End)} se chevauchent"
                : $"Le créneau {Heure(o.Loser.Start)}–{Heure(o.Loser.End)} chevauche la nuit du {Jours.Nom(o.WinnerDay)}";
            yield return $"{debut} : là où ils se recouvrent, {gagnant} l'emporte ({Libelle(o.Winner.Valeur)}).";
        }
    }

    private static string Heure(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static string Heure(int minutes) => minutes >= 1440 ? "24:00" : Heure(TimeSpan.FromMinutes(minutes));

    /// <summary>Position CSS d'une minute de la journée, en culture invariante (« 33,333% » n'est pas du CSS).</summary>
    private static string Pourcent(int minutes) => (minutes / 14.4).ToString("0.###", CultureInfo.InvariantCulture) + "%";

    private static string Abrege(DayOfWeek jour) => Phrases.Majuscule(Jours.Nom(jour)[..3]);
}
