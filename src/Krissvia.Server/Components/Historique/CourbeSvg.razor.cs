using System.Globalization;

namespace Krissvia.Server.Components.Historique;

/// <summary>Comment tracer une série : trait plein, fin, pointillés (une consigne) ou barres (une part de temps).</summary>
public enum StyleSerie { Ligne, Fine, Pointilles, Barres }

/// <summary>Une série de la courbe, une valeur par case de la grille (nulle : la courbe s'interrompt).</summary>
/// <param name="Min">Avec <paramref name="Max"/> : une bande min / max autour du trait (heures consolidées).</param>
public sealed record SerieCourbe(string Nom, IReadOnlyList<double?> Valeurs, string Couleur, StyleSerie Style = StyleSerie.Ligne,
    IReadOnlyList<double?>? Min = null, IReadOnlyList<double?>? Max = null);

/// <summary>Le dessin : échelle, graduations « rondes », repères de temps à l'heure locale.</summary>
public partial class CourbeSvg
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Le début de chaque case (heure UTC, affichée à l'heure locale).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<DateTimeOffset> Temps { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<SerieCourbe> Series { get; set; } = [];
    [Parameter] public string? Unite { get; set; }
    [Parameter] public TimeZoneInfo Fuseau { get; set; } = TimeZoneInfo.Local;

    /// <summary>Bornes imposées (0 à 100 pour un pourcentage) ; sinon tirées des valeurs, avec une marge.</summary>
    [Parameter] public double? YMin { get; set; }
    [Parameter] public double? YMax { get; set; }
    [Parameter] public int Hauteur { get; set; } = 260;

    // Le repère : 1000 de large, la hauteur demandée ; marges pour les graduations (gauche) et les heures (bas).
    private const int L = 1000;
    private int H => Hauteur;
    private const int G = 46, D = 8, Haut = 8, Bas = 24;

    private double _bas, _haut;

    protected override void OnParametersSet()
    {
        var valeurs = Series.SelectMany(s => s.Valeurs.Concat(s.Min ?? []).Concat(s.Max ?? [])).OfType<double>().ToList();
        double bas = YMin ?? (valeurs.Count > 0 ? valeurs.Min() : 0);
        double haut = YMax ?? (valeurs.Count > 0 ? valeurs.Max() : 1);
        if (YMin is null && Series.Any(s => s.Style == StyleSerie.Barres)) bas = Math.Min(bas, 0);
        if (haut - bas < 1e-9) { bas -= 1; haut += 1; }

        // Une graduation ronde (1, 2 ou 5 × 10ⁿ) et des bornes qui tombent dessus : « 18 · 20 · 22 », pas « 18,37 ».
        var pas = PasRond((haut - bas) / 4);
        _bas = YMin ?? Math.Floor(bas / pas) * pas;
        _haut = YMax ?? Math.Ceiling(haut / pas) * pas;
        if (_haut <= _bas) _haut = _bas + pas;
    }

    private static double PasRond(double brut)
    {
        var p = Math.Pow(10, Math.Floor(Math.Log10(brut)));
        return brut / p switch { <= 1 => 1, <= 2 => 2, <= 5 => 5, _ => 10 } * p;
    }

    private double X(int i) => G + (L - G - D) * i / (double)Math.Max(1, Temps.Count);

    /// <summary>Le milieu d'une case : un trait passe au centre de ce qu'il résume.</summary>
    private double Xc(int i) => X(i) + (L - G - D) / (double)Math.Max(1, Temps.Count) / 2;

    private double Y(double v) => Haut + (H - Haut - Bas) * (1 - (v - _bas) / (_haut - _bas));

    /// <summary>SVG veut un point décimal, jamais la virgule française.</summary>
    private static string S(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private IEnumerable<(double Y, string Libelle)> Graduations()
    {
        var pas = (_haut - _bas) / 4;
        for (var k = 0; k <= 4; k++)
        {
            var v = _bas + k * pas;
            yield return (Y(v), v.ToString(Math.Abs(pas) >= 1 ? "0" : "0.#", Fr) + (k == 4 && Unite is { Length: > 0 } u ? $" {u}" : ""));
        }
    }

    /// <summary>Quatre ou cinq repères : l'heure sur un jour au plus, la date au-delà.</summary>
    private IEnumerable<(double X, string Libelle)> Reperes()
    {
        if (Temps.Count < 2) yield break;
        var etendue = Temps[^1] - Temps[0];
        var format = etendue <= TimeSpan.FromDays(1.1) ? "HH:mm" : "dd/MM";
        var tous = Math.Max(1, Temps.Count / 5);
        for (var i = tous; i < Temps.Count; i += tous)
            yield return (X(i), TimeZoneInfo.ConvertTime(Temps[i], Fuseau).ToString(format, Fr));
    }

    /// <summary>Les traits, coupés à chaque case vide : un capteur muet ne se relie pas à son retour.</summary>
    private IEnumerable<string> Morceaux(IReadOnlyList<double?> valeurs)
    {
        var morceau = new List<string>();
        for (var i = 0; i < valeurs.Count; i++)
        {
            if (valeurs[i] is { } v)
            {
                morceau.Add($"{S(Xc(i))},{S(Y(v))}");
                continue;
            }

            if (morceau.Count > 0) yield return string.Join(" ", morceau);
            morceau.Clear();
        }

        if (morceau.Count > 0) yield return string.Join(" ", morceau);
    }

    private IEnumerable<string> Bandes(SerieCourbe serie)
    {
        var haut = new List<string>();
        var bas = new List<string>();
        for (var i = 0; i <= serie.Valeurs.Count; i++)
        {
            if (i < serie.Valeurs.Count && serie.Min![i] is { } mn && serie.Max![i] is { } mx)
            {
                haut.Add($"{S(Xc(i))},{S(Y(mx))}");
                bas.Insert(0, $"{S(Xc(i))},{S(Y(mn))}");
                continue;
            }

            if (haut.Count > 1) yield return string.Join(" ", haut.Concat(bas));
            haut.Clear();
            bas.Clear();
        }
    }

    private static MarkupString Texte(double x, double y, string ancre, string texte) =>
        new($"<text x=\"{S(x)}\" y=\"{S(y)}\" text-anchor=\"{ancre}\" class=\"kv-courbe-texte\">{System.Net.WebUtility.HtmlEncode(texte)}</text>");

    private string Bulle(int i)
    {
        var heure = TimeZoneInfo.ConvertTime(Temps[i], Fuseau).ToString("ddd dd/MM HH:mm", Fr);
        var lignes = Series.Select(s => $"{s.Nom} : {(s.Valeurs[i] is { } v ? v.ToString("0.#", Fr) + (Unite is { Length: > 0 } u && s.Style != StyleSerie.Barres ? $" {u}" : "") : "—")}");
        return $"{heure}\n{string.Join("\n", lignes)}";
    }
}
