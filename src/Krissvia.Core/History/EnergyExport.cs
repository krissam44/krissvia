using System.Globalization;
using Krissvia.Core.Export;

namespace Krissvia.Core.History;

/// <summary>Les jours d'un compteur, à exporter.</summary>
public sealed record EnergyExportMeter(string Name, IReadOnlyList<EnergyDay> Days);

/// <summary>
/// La consommation de tous les compteurs en classeur Excel, pour faire ses propres statistiques : une feuille
/// « Jours », une feuille « Semaines ». Format « long » — une ligne par compteur et par jour (ou semaine), avec le
/// compteur en première colonne : un tableau croisé dynamique s'en fait directement, ce que des colonnes par compteur
/// empêcheraient.
/// </summary>
/// <remarks>
/// Un jour sans mesure garde sa ligne, consommation vide et remarque « sans mesure » : un trou n'est pas « 0 kWh ».
/// Le jour en cours est marqué « en cours », la semaine en cours aussi : à écarter d'une moyenne.
/// </remarks>
public static class EnergyExport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<XlsxSheet> Build(IReadOnlyList<EnergyExportMeter> meters, double? pricePerKwh, DateOnly today) =>
        [Jours(meters, pricePerKwh, today), Semaines(meters, pricePerKwh, today)];

    private static XlsxSheet Jours(IReadOnlyList<EnergyExportMeter> compteurs, double? prix, DateOnly aujourdhui)
    {
        var lignes = new List<IReadOnlyList<object?>>();
        foreach (var c in compteurs)
            foreach (var j in c.Days.OrderBy(d => d.Day))
            {
                var semaine = ISOWeek.GetWeekOfYear(j.Day.ToDateTime(TimeOnly.MinValue));
                lignes.Add([
                    c.Name, j.Day, j.Day.ToString("dddd", Fr), semaine, j.Day.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    Arrondi(j.Kwh, 3), j.Kwh * prix is { } cout ? Math.Round(cout, 2) : null, Arrondi(j.PeakPower, 0),
                    j.Day == aujourdhui ? "en cours" : j.Kwh is null ? "sans mesure" : null,
                ]);
            }

        return new XlsxSheet("Jours",
        [
            new("Compteur", 24), new("Jour", 12, XlsxFormat.Date), new("Jour de la semaine", 18), new("Semaine", 10, XlsxFormat.Integer),
            new("Mois", 10), new("Consommation (kWh)", 20, XlsxFormat.ThreeDecimals), new("Coût (€)", 11, XlsxFormat.TwoDecimals),
            new("Puissance max (W ou VA)", 24, XlsxFormat.Integer), new("Remarque", 14),
        ], lignes);
    }

    private static XlsxSheet Semaines(IReadOnlyList<EnergyExportMeter> compteurs, double? prix, DateOnly aujourdhui)
    {
        var lignes = new List<IReadOnlyList<object?>>();
        var lundiCourant = EnergyWeeks.MondayOf(aujourdhui);
        foreach (var c in compteurs)
            foreach (var s in EnergyWeeks.FromDays(c.Days))
                lignes.Add([
                    c.Name, ISOWeek.GetYear(s.Monday.ToDateTime(TimeOnly.MinValue)), s.Number, s.Monday, s.Sunday, s.MeasuredDays,
                    Arrondi(s.Kwh, 3), Arrondi(s.AveragePerDay, 3), s.Kwh * prix is { } cout ? Math.Round(cout, 2) : null,
                    s.Monday == lundiCourant ? "en cours" : s.MeasuredDays < 7 ? $"{s.MeasuredDays} j mesuré(s)" : null,
                ]);

        return new XlsxSheet("Semaines",
        [
            new("Compteur", 24), new("Année", 8, XlsxFormat.Integer), new("Semaine", 10, XlsxFormat.Integer), new("Du (lundi)", 12, XlsxFormat.Date),
            new("Au (dimanche)", 14, XlsxFormat.Date), new("Jours mesurés", 14, XlsxFormat.Integer),
            new("Consommation (kWh)", 20, XlsxFormat.ThreeDecimals), new("Moyenne / jour (kWh)", 20, XlsxFormat.ThreeDecimals),
            new("Coût (€)", 11, XlsxFormat.TwoDecimals), new("Remarque", 16),
        ], lignes);
    }

    private static double? Arrondi(double? v, int decimales) => v is { } x ? Math.Round(x, decimales) : null;
}
