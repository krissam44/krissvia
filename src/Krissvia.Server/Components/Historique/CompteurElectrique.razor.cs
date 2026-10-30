using System.Globalization;
using Krissvia.Core.History;
using Krissvia.Core.States;

namespace Krissvia.Server.Components.Historique;

/// <summary>
/// Les chiffres et les courbes d'un compteur. Les chiffres du moment (puissance, tension) suivent l'état en direct ;
/// la consommation depuis minuit est « index maintenant − index à minuit », lue sur les points bruts ; les jours
/// passés viennent des heures consolidées (<see cref="EnergyDays"/>). Relu toutes les minutes, rythme de l'historique.
/// </summary>
public partial class CompteurElectrique
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const string Jaune = "#ffb300", Vert = "#66bb6a", Bleu = "#42a5f5", Violet = "#ce93d8";
    private static readonly TimeSpan Pas = TimeSpan.FromMinutes(5);
    private const int Jours = 30;

    [Parameter, EditorRequired] public Compteur Compteur { get; set; } = default!;

    /// <summary>Le prix du kWh (€), ou null : pas de coût affiché.</summary>
    [Parameter] public double? Prix { get; set; }

    [Inject] private IHistoryRepository Historique { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    /// <summary>Une ligne du tableau, déjà mise en forme.</summary>
    private sealed record Ligne(string Date, string Kwh, IReadOnlyList<string> Parts, string Cout, string Pointe);

    private int _onglet;
    private Timer? _minuterie;
    private int _tours;
    private double? _depuisMinuit;
    private bool _jourVide = true;
    private IReadOnlyList<DateTimeOffset> _tempsJour = [];
    private List<SerieCourbe> _serieJour = [];
    private IReadOnlyList<EnergyDay> _jours = [];
    private IReadOnlyList<DateTimeOffset> _tempsJours = [];
    private List<SerieCourbe> _serieJours = [];
    private List<Ligne> _lignes = [];
    private double? _total;

    protected override async Task OnInitializedAsync()
    {
        await ChargerAsync();
        // Les chiffres du moment toutes les 5 s ; l'historique toutes les minutes (il ne s'écrit pas plus vite).
        _minuterie = new Timer(_ => InvokeAsync(async () =>
        {
            if (++_tours % 12 == 0) await ChargerAsync();
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    protected override void OnParametersSet() => MettreEnForme();

    private async Task ChargerAsync()
    {
        var zone = Horloge.LocalTimeZone;
        var maintenant = Horloge.GetUtcNow();
        var aujourdhui = EnergyDays.Jour(maintenant, zone);
        var minuit = EnergyDays.Midnight(aujourdhui, zone);

        // Depuis minuit et les 30 derniers jours : le même calcul que GET /api/energy (LectureCompteur).
        var conso = await LectureCompteur.LireAsync(Compteur, Jours, Historique, Etats, Horloge);
        _depuisMinuit = conso.DepuisMinuit;

        // La puissance du jour, de minuit à maintenant, par cases de 5 min.
        _tempsJour = [.. Enumerable.Range(0, (int)Math.Ceiling((maintenant - minuit) / Pas)).Select(i => minuit + i * Pas)];
        if (Compteur.Puissance is { } puissance)
        {
            var cases = HistoryCurve.FromPoints(await Historique.ReadAsync(puissance.EntityId, minuit, maintenant), _tempsJour, Pas, maintenant);
            _jourVide = cases.All(c => c.Mean is null);
            _serieJour = [new SerieCourbe(puissance.Unit == "VA" ? "Puissance apparente" : "Puissance", [.. cases.Select(c => c.Mean)], Jaune,
                StyleSerie.Ligne, [.. cases.Select(c => c.Min)], [.. cases.Select(c => c.Max)])];
        }

        var jours = conso.Jours;
        _jours = jours;
        var premier = jours[0].Day;
        var depuis = EnergyDays.Midnight(premier.AddDays(-1), zone);
        _tempsJours = [.. jours.Select(j => EnergyDays.Midnight(j.Day, zone))];
        _serieJours = [new SerieCourbe("Consommation", [.. jours.Select(j => j.Kwh)], Bleu, StyleSerie.Barres)];

        var parts = new List<IReadOnlyList<EnergyDay>>();
        foreach (var partiel in Compteur.Partiels)
            parts.Add(EnergyDays.FromHours(await Historique.ReadHourlyAsync(partiel.EntityId, depuis, maintenant), [], zone, premier, aujourdhui));
        _partsBrutes = parts;
        _total = jours.Any(j => j.Kwh is not null) ? jours.Sum(j => j.Kwh ?? 0) : null;
        MettreEnForme();
    }

    private IReadOnlyList<IReadOnlyList<EnergyDay>> _partsBrutes = [];

    /// <summary>Le tableau, du plus récent au plus ancien ; refait quand le prix change.</summary>
    private void MettreEnForme()
    {
        _lignes = [.. _jours.Select((j, i) => new Ligne(
                j.Day.ToString("ddd dd/MM", Fr),
                j.Kwh is { } k ? Kwh(k) : "—",
                [.. _partsBrutes.Select(p => p[i].Kwh is { } x ? Kwh(x) : "—")],
                j.Kwh is { } c && Prix is { } prix ? Euros(c * prix) : "—",
                j.PeakPower is { } m ? $"{m.ToString("#,0", Fr)} {Compteur.Puissance?.Unit}" : "—"))
            .Reverse()];
    }

    private IEnumerable<(string Valeur, string Libelle, string Couleur)> Tuiles()
    {
        if (Compteur.Puissance is { } p)
            yield return (Nombre(p.EntityId) is { } w ? $"{w.ToString("#,0", Fr)}" : "—", $"{p.Unit} — puissance{(p.Unit == "VA" ? " apparente" : "")}", Jaune);
        if (Compteur.Tension is { } t)
            yield return (Nombre(t.EntityId) is { } v ? $"{v.ToString("0.#", Fr)} V" : "—", "Tension", Vert);
        yield return (_depuisMinuit is { } k ? Kwh(k) : "—", "Depuis minuit", Bleu);
        if (Prix is { } prix)
            yield return (_depuisMinuit is { } c ? Euros(c * prix) : "—", "Coût du jour", Violet);
    }

    /// <summary>« Compteur Linky index 1 » devient « index 1 » : le nom du compteur est déjà au-dessus.</summary>
    private string NomPartiel(Krissvia.Core.Registry.RegisteredEntity e) =>
        e.DisplayName.StartsWith(Compteur.Nom + " ", StringComparison.Ordinal) ? e.DisplayName[(Compteur.Nom.Length + 1)..] : e.DisplayName;

    private double? Nombre(string entityId) => LectureCompteur.Nombre(Etats, entityId);

    private static string Kwh(double kwh) => $"{kwh.ToString(kwh >= 100 ? "#,0" : "0.##", Fr)} kWh";

    private static string Euros(double euros) => euros.ToString("0.00 €", Fr);

    public void Dispose() => _minuterie?.Dispose();
}
