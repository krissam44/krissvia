using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Krissvia.Core.Export;
using Krissvia.Core.History;

namespace Krissvia.Core.Tests;

/// <summary>L'export Excel de la consommation : un .xlsx valide, des nombres et des dates qui restent des nombres.</summary>
public class EnergyExportTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Les parties du classeur, chacune lue comme XML (un fichier mal formé lève une exception).</summary>
    private static Dictionary<string, XDocument> Ouvrir(byte[] classeur)
    {
        using var zip = new ZipArchive(new MemoryStream(classeur), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var f = e.Open(); return XDocument.Load(f); });
    }

    private static List<List<string>> Cases(XDocument feuille) =>
        [.. feuille.Descendants(S + "row").Select(r => r.Elements(S + "c")
            .Select(c => (string?)c.Element(S + "v") ?? string.Concat(c.Descendants(S + "t").Select(t => t.Value))).ToList())];

    [Fact]
    public void Le_classeur_est_un_xlsx_bien_forme_avec_des_nombres_au_point_meme_en_francais()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var classeur = XlsxWorkbook.Write([new XlsxSheet("Conso & <essai>",
                [new("Compteur"), new("Jour", Format: XlsxFormat.Date), new("kWh", Format: XlsxFormat.ThreeDecimals), new("Vide")],
                [["Salon & cuisine", new DateOnly(2026, 11, 28), 8.42, null]])]);

            var parties = Ouvrir(classeur);
            Assert.Contains("[Content_Types].xml", parties.Keys);
            Assert.Equal("Conso & <essai>", (string?)parties["xl/workbook.xml"].Descendants(S + "sheet").Single().Attribute("name"));

            var feuille = parties["xl/worksheets/sheet1.xml"];
            var lignes = Cases(feuille);
            Assert.Equal(["Compteur", "Jour", "kWh", "Vide"], lignes[0]);
            Assert.Equal(["Salon & cuisine", "46354", "8.42"], lignes[1]);   // la case vide n'est pas écrite
            Assert.Equal("A1:D2", (string?)feuille.Descendants(S + "autoFilter").Single().Attribute("ref"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(701, "ZZ")]
    public void Les_colonnes_se_nomment_comme_dans_Excel(int index, string lettres) =>
        Assert.Equal(lettres, XlsxWorkbook.Colonne(index));

    [Fact]
    public void Le_numero_de_serie_d_une_date_est_celui_d_Excel()
    {
        Assert.Equal(1, XlsxWorkbook.Serial(new DateOnly(1899, 12, 31)));
        Assert.Equal(46023, XlsxWorkbook.Serial(new DateOnly(2026, 1, 1)));
    }

    private static readonly DateOnly Aujourdhui = new(2026, 11, 28);   // un samedi

    private static EnergyExportMeter Compteur(string nom) => new(nom,
    [
        new(new(2026, 11, 22), 13.74, 5090),   // dimanche, semaine 47
        new(new(2026, 11, 23), 13.51, 5185),   // lundi, semaine 48
        new(new(2026, 11, 24), null, null),
        new(Aujourdhui, 8.4249, 5034),
    ]);

    [Fact]
    public void La_feuille_des_jours_a_une_ligne_par_compteur_et_par_jour_avec_cout_et_remarques()
    {
        var feuilles = EnergyExport.Build([Compteur("Linky"), Compteur("Prise")], 0.2001, Aujourdhui);

        var jours = feuilles[0];
        Assert.Equal("Jours", jours.Name);
        Assert.Equal(8, jours.Rows.Count);
        Assert.Equal(["Linky", new DateOnly(2026, 11, 22), "dimanche", 47, "2026-11", 13.74, 2.75, 5090.0, null], jours.Rows[0]);
        Assert.Null(jours.Rows[2][5]);                       // un trou n'est pas « 0 kWh »
        Assert.Equal("sans mesure", jours.Rows[2][8]);
        Assert.Equal((8.425, "en cours"), ((double)jours.Rows[3][5]!, (string)jours.Rows[3][8]!));
        Assert.Equal("Prise", jours.Rows[4][0]);
    }

    [Fact]
    public void La_feuille_des_semaines_signale_la_semaine_en_cours_et_les_semaines_incompletes()
    {
        var semaines = EnergyExport.Build([Compteur("Linky")], 0.2001, Aujourdhui)[1].Rows;

        Assert.Equal(2, semaines.Count);
        Assert.Equal(["Linky", 2026, 47, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 22), 1, 13.74, 13.74, 2.75, "1 j mesuré(s)"], semaines[0]);
        Assert.Equal((48, 2, 21.935, "en cours"), ((int)semaines[1][2]!, (int)semaines[1][5]!, (double)semaines[1][6]!, (string)semaines[1][9]!));
    }

    [Fact]
    public void Sans_prix_le_cout_reste_vide()
    {
        var feuilles = EnergyExport.Build([Compteur("Linky")], null, Aujourdhui);

        Assert.All(feuilles[0].Rows, r => Assert.Null(r[6]));
        Assert.All(feuilles[1].Rows, r => Assert.Null(r[8]));
    }

    [Fact]
    public void Le_classeur_complet_s_ecrit_et_se_relit()
    {
        var parties = Ouvrir(XlsxWorkbook.Write(EnergyExport.Build([Compteur("Linky")], 0.2001, Aujourdhui)));

        Assert.Equal(["Jours", "Semaines"], parties["xl/workbook.xml"].Descendants(S + "sheet").Select(s => (string)s.Attribute("name")!));
        Assert.Equal(5, Cases(parties["xl/worksheets/sheet1.xml"]).Count);   // titres + 4 jours
        Assert.Equal("13.74", Cases(parties["xl/worksheets/sheet1.xml"])[1][5]);
    }
}
