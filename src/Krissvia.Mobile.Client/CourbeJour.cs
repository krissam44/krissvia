using System.Globalization;
using System.Text;

namespace Krissvia.Mobile.Client;

/// <summary>Une graduation de l'axe des puissances : sa hauteur dans le tracé et son libellé.</summary>
public sealed record Graduation(double Y, string Libelle);

/// <summary>
/// La courbe de puissance du jour, prête à dessiner en SVG : la moyenne en ligne, l'étendue min-max en bande, et
/// l'axe des puissances gradué par pas ronds. Le jour entier est en abscisse (minuit à minuit) : on voit où en est la
/// journée. Une case sans mesure coupe la ligne au lieu de la relier à zéro, comme sur le web.
/// </summary>
/// <param name="Ligne">Chemin SVG de la moyenne (vide sans aucune mesure).</param>
/// <param name="Bande">Chemin SVG de l'étendue min-max, fermé par segment de mesures consécutives.</param>
public sealed record CourbeJour(string Ligne, string Bande, IReadOnlyList<Graduation> Graduations)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public bool Vide => Ligne.Length == 0;

    /// <summary>
    /// Les points du serveur (le premier tombe à minuit) dans un rectangle <paramref name="largeur"/> × <paramref name="hauteur"/>,
    /// origine en haut à gauche comme en SVG.
    /// </summary>
    public static CourbeJour Tracer(IReadOnlyList<PointPuissance> points, double largeur, double hauteur)
    {
        var mesures = points.Where(p => p.Moyenne is not null).ToList();
        if (points.Count == 0 || mesures.Count == 0) return new CourbeJour("", "", []);

        var plusHaut = mesures.Max(p => Math.Max(p.Max ?? p.Moyenne!.Value, p.Moyenne!.Value));
        var pas = PasRond(Math.Max(plusHaut, 100) / 3);
        var sommet = Math.Ceiling(Math.Max(plusHaut, 100) / pas) * pas;

        var minuit = points[0].A;
        double X(DateTimeOffset a) => Math.Clamp((a - minuit).TotalHours / 24, 0, 1) * largeur;
        double Y(double v) => hauteur - Math.Clamp(v / sommet, 0, 1) * hauteur;

        var ligne = new StringBuilder();
        var bande = new StringBuilder();
        var segment = new List<PointPuissance>();
        void FermerBande()
        {
            if (segment.Count > 0)
            {
                bande.Append(Inv, $"M{X(segment[0].A):0.#},{Y(segment[0].Max ?? segment[0].Moyenne!.Value):0.#}");
                foreach (var p in segment.Skip(1)) bande.Append(Inv, $"L{X(p.A):0.#},{Y(p.Max ?? p.Moyenne!.Value):0.#}");
                foreach (var p in Enumerable.Reverse(segment)) bande.Append(Inv, $"L{X(p.A):0.#},{Y(p.Min ?? p.Moyenne!.Value):0.#}");
                bande.Append('Z');
            }
            segment.Clear();
        }

        foreach (var p in points)
        {
            if (p.Moyenne is not { } m)
            {
                FermerBande();
                continue;
            }
            ligne.Append(segment.Count == 0 ? 'M' : 'L').Append(Inv, $"{X(p.A):0.#},{Y(m):0.#}");
            segment.Add(p);
        }
        FermerBande();

        var graduations = Enumerable.Range(0, (int)Math.Round(sommet / pas) + 1)
            .Select(i => new Graduation(Y(i * pas), (i * pas).ToString("#,0", Fr)))
            .ToList();
        return new CourbeJour(ligne.ToString(), bande.ToString(), graduations);
    }

    /// <summary>Le pas rond (1, 2 ou 5 × 10ⁿ) juste au-dessus de <paramref name="brut"/>.</summary>
    public static double PasRond(double brut)
    {
        var puissance = Math.Pow(10, Math.Floor(Math.Log10(brut)));
        return new[] { 1, 2, 5, 10 }.Select(f => f * puissance).First(p => p >= brut);
    }
}
