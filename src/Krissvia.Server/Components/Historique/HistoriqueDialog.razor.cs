using System.Globalization;
using Krissvia.Contracts;
using Krissvia.Core.History;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Historique;

/// <summary>
/// L'historique d'une entité, selon ce qu'elle enregistre : un thermostat montre ambiante, consigne et extérieure, avec
/// sa puissance de chauffe en dessous (la courbe TPI de DomoMud) ; une mesure, sa courbe et la bande min / max ; un
/// interrupteur ou un capteur tout-ou-rien, la part du temps où il était actif.
/// </summary>
public partial class HistoriqueDialog
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const string Violet = "#ce93d8", Orange = "#ffa726", Bleu = "#42a5f5", Corail = "#ff7043", Cyan = "#26c6da";

    [CascadingParameter] private IMudDialogInstance Dialogue { get; set; } = default!;
    [Parameter, EditorRequired] public RegisteredEntity Entite { get; set; } = default!;

    [Inject] private IHistoryRepository Historique { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    private HistoryRange _periode = HistoryCurve.Ranges[1];
    private bool _chargement = true;
    private bool _aucune;
    private string _rien = "";
    private IReadOnlyList<DateTimeOffset> _temps = [];
    private List<SerieCourbe> _series = [];
    private List<SerieCourbe> _seriesBas = [];
    private string _titreBas = "";
    private string? _unite;
    private double? _yMin, _yMax;
    private List<(string Nom, string Couleur, bool Pointilles)> _legende = [];
    private List<(string Valeur, string Libelle, string Couleur)> _chiffres = [];

    /// <summary>Ouvre la fenêtre (Entités, tableau de bord, écran Thermostats) : large, la courbe a besoin de place.</summary>
    public static Task OuvrirAsync(IDialogService dialogues, RegisteredEntity entite) =>
        dialogues.ShowAsync<HistoriqueDialog>(null, new DialogParameters<HistoriqueDialog> { { d => d.Entite, entite } },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true });

    protected override Task OnInitializedAsync() => ChargerAsync();

    private Task ChoisirAsync(HistoryRange periode)
    {
        _periode = periode;
        return ChargerAsync();
    }

    private async Task ChargerAsync()
    {
        _chargement = true;
        _aucune = false;
        _series = [];
        _seriesBas = [];
        _legende = [];
        _chiffres = [];
        _yMin = _yMax = null;
        StateHasChanged();

        var maintenant = Horloge.GetUtcNow();
        _temps = HistoryCurve.Grid(_periode, maintenant);
        var id = Entite.EntityId;
        var series = await Historique.SeriesAsync(id);

        if (series.Count == 0)
        {
            Rien("Rien d'enregistré pour cette entité : un texte (un tarif, l'état d'une machine) ne se trace pas.");
        }
        else if (series.Contains(HistoryValues.Serie(id, "temperature")))
        {
            await ThermostatAsync(series, maintenant);
        }
        else if (series.Contains(id))
        {
            if (Entite.Kind is EntityKind.Switch or EntityKind.Light or EntityKind.BinarySensor)
                await ToutOuRienAsync(id, maintenant);
            else
                await MesureAsync(id, Entite.Unit, maintenant);
        }
        else
        {
            // Un objet qui n'enregistre qu'un champ (la puissance d'un délesteur, en VA).
            await MesureAsync(series[0], Entite.DeviceClass == DeviceClasses.LoadShedding ? "VA" : Entite.Unit, maintenant);
        }

        _chargement = false;
    }

    private Task<IReadOnlyList<CurvePoint>> LireAsync(string serie, DateTimeOffset maintenant) =>
        HistoryCurve.ReadAsync(Historique, serie, _periode, maintenant);

    private async Task ThermostatAsync(IReadOnlyList<string> series, DateTimeOffset maintenant)
    {
        var id = Entite.EntityId;
        var ambiante = await LireAsync(HistoryValues.Serie(id, "temperature"), maintenant);
        var consigne = await LireAsync(HistoryValues.Serie(id, "target"), maintenant);
        var exterieure = series.Contains(HistoryValues.Serie(id, "outdoor_temperature"))
            ? await LireAsync(HistoryValues.Serie(id, "outdoor_temperature"), maintenant) : null;
        var puissance = series.Contains(HistoryValues.Serie(id, "power"))
            ? await LireAsync(HistoryValues.Serie(id, "power"), maintenant) : null;
        // Sans puissance TPI enregistrée, la part du temps où il chauffait dit presque la même chose.
        var chauffe = puissance is null || Vide(puissance)
            ? series.Contains(HistoryValues.Serie(id, "heating")) ? await LireAsync(HistoryValues.Serie(id, "heating"), maintenant) : null
            : null;
        if (Vide(ambiante) && Vide(consigne))
        {
            Rien("Aucune mesure sur cette période.");
            return;
        }

        _unite = "°C";
        _series.Add(new SerieCourbe("Ambiante", Moyennes(ambiante), Violet, StyleSerie.Ligne, Mins(ambiante), Maxs(ambiante)));
        _series.Add(new SerieCourbe("Consigne", Moyennes(consigne), Orange, StyleSerie.Pointilles));
        if (exterieure is not null && !Vide(exterieure)) _series.Add(new SerieCourbe("Extérieure", Moyennes(exterieure), Bleu, StyleSerie.Fine));
        _legende = [.. _series.Select(s => (s.Nom, s.Couleur, s.Style == StyleSerie.Pointilles))];

        // La puissance TPI est une part (0 à 1) : en pourcentage, sous la courbe, comme la bande de chauffe de DomoMud.
        if (puissance is not null && !Vide(puissance))
        {
            _seriesBas.Add(new SerieCourbe("Puissance", [.. puissance.Select(p => p.Mean * 100)], Corail, StyleSerie.Barres));
            _titreBas = "Puissance de chauffe (TPI), en % de chaque case";
        }
        else if (chauffe is not null && !Vide(chauffe))
        {
            puissance = chauffe;
            _seriesBas.Add(new SerieCourbe("En chauffe", [.. chauffe.Select(p => p.Mean * 100)], Corail, StyleSerie.Barres));
            _titreBas = "Part du temps en chauffe, en % de chaque case";
        }

        Chiffre(Derniere(ambiante), "Ambiante actuelle", Violet, "°C");
        Chiffre(HistoryCurve.Mean(ambiante), "Ambiante moyenne", Violet, "°C");
        Chiffre(HistoryCurve.Mean(consigne), "Consigne moyenne", Orange, "°C");
        if (exterieure is not null) Chiffre(HistoryCurve.Mean(exterieure), "Extérieure moyenne", Bleu, "°C");
        if (puissance is not null) Chiffre(HistoryCurve.Mean(puissance) * 100, "Puissance moyenne", Corail, "%", 0);
    }

    private async Task MesureAsync(string serie, string? unite, DateTimeOffset maintenant)
    {
        var points = await LireAsync(serie, maintenant);
        if (Vide(points))
        {
            Rien("Aucune mesure sur cette période.");
            return;
        }

        _unite = unite;
        _series.Add(new SerieCourbe(Entite.DisplayName, Moyennes(points), Cyan, StyleSerie.Ligne, Mins(points), Maxs(points)));
        Chiffre(Derniere(points), "Dernière", Cyan, unite);
        Chiffre(points.Select(p => p.Min).Min(), "Minimum", Bleu, unite);
        Chiffre(HistoryCurve.Mean(points), "Moyenne", Violet, unite);
        Chiffre(points.Select(p => p.Max).Max(), "Maximum", Corail, unite);
    }

    /// <summary>Allumé / éteint, ouvert / fermé : la moyenne d'une case est la part du temps où c'était vrai.</summary>
    private async Task ToutOuRienAsync(string serie, DateTimeOffset maintenant)
    {
        var points = await LireAsync(serie, maintenant);
        if (Vide(points))
        {
            Rien("Aucune mesure sur cette période.");
            return;
        }

        var etat = Entite.Kind == EntityKind.BinarySensor ? Entite.DeviceClass switch
        {
            DeviceClasses.Opening => "ouvert",
            DeviceClasses.Occupancy => "en détection",
            DeviceClasses.Problem => "en problème",
            _ => "actif",
        } : "allumé";
        _unite = "%";
        _yMin = 0;
        _yMax = 100;
        _series.Add(new SerieCourbe($"Part du temps {etat}", [.. points.Select(p => p.Mean * 100)], Orange, StyleSerie.Barres));
        _legende = [($"Part du temps {etat}, en % de chaque case", Orange, false)];

        var part = HistoryCurve.Mean(points);
        Chiffre(part * 100, $"du temps {etat}", Orange, "%", 0);
        if (part is not null)
        {
            // Case par case : une case inconnue ne compte pas, au lieu d'étendre la moyenne à toute la période.
            var duree = TimeSpan.FromTicks((long)points.Sum(c => (c.Mean ?? 0) * _periode.Step.Ticks));
            _chiffres.Add((duree.TotalHours >= 1 ? $"{duree.TotalHours:0.#} h" : $"{duree.TotalMinutes:0} min", $"{etat} sur {_periode.Label}", Corail));
        }
    }

    private void Rien(string pourquoi)
    {
        _aucune = true;
        _rien = pourquoi;
    }

    private void Chiffre(double? valeur, string libelle, string couleur, string? unite, int decimales = 1) =>
        _chiffres.Add((valeur is { } v ? $"{v.ToString(decimales == 0 ? "0" : "0.#", Fr)}{(unite is { Length: > 0 } u ? $" {u}" : "")}" : "—", libelle, couleur));

    private static bool Vide(IReadOnlyList<CurvePoint> points) => points.All(p => p.Mean is null);
    private static double?[] Moyennes(IReadOnlyList<CurvePoint> p) => [.. p.Select(x => x.Mean)];
    private static double?[] Mins(IReadOnlyList<CurvePoint> p) => [.. p.Select(x => x.Min)];
    private static double?[] Maxs(IReadOnlyList<CurvePoint> p) => [.. p.Select(x => x.Max)];
    private static double? Derniere(IReadOnlyList<CurvePoint> p) => p.LastOrDefault(x => x.Mean is not null)?.Mean;

    private static string Pas(TimeSpan pas) => pas.TotalHours >= 1 ? $"{pas.TotalHours:0} h" : $"{pas.TotalMinutes:0} min";
}
