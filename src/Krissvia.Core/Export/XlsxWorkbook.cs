using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Krissvia.Core.Export;

/// <summary>Le format d'une colonne numérique.</summary>
public enum XlsxFormat
{
    /// <summary>Texte, ou nombre sans format particulier.</summary>
    General,

    /// <summary>Date (jj/mm/aaaa dans un Excel français).</summary>
    Date,

    /// <summary>Deux décimales (euros).</summary>
    TwoDecimals,

    /// <summary>Trois décimales (kWh).</summary>
    ThreeDecimals,

    /// <summary>Entier (watts, nombre de jours).</summary>
    Integer,
}

/// <summary>Une colonne : son titre, sa largeur (en caractères) et son format.</summary>
public sealed record XlsxColumn(string Title, double Width = 14, XlsxFormat Format = XlsxFormat.General);

/// <summary>Une feuille : des colonnes, puis des lignes dont chaque case vaut une chaîne, un nombre, une date ou null (vide).</summary>
public sealed record XlsxSheet(string Name, IReadOnlyList<XlsxColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// Un classeur Excel (.xlsx) minimal, écrit sans bibliothèque : un .xlsx est un zip de quelques fichiers XML (Office
/// Open XML, ECMA-376). Les nombres et les dates y sont de vraies valeurs (tableaux croisés, graphiques, sommes), la
/// ligne de titres est figée et filtrable.
/// </summary>
/// <remarks>
/// Les nombres s'écrivent en culture invariante (point décimal) quelle que soit la langue du serveur : « 8,42 » dans
/// le XML rendrait le fichier illisible. Les dates sont des numéros de série Excel (jours depuis le 30/12/1899) avec un
/// format de date. Les textes sont « en ligne » (pas de table de chaînes partagées) : plus simple, et Excel comme
/// LibreOffice les lisent.
/// </remarks>
public static class XlsxWorkbook
{
    private static readonly DateOnly Origine = new(1899, 12, 30);

    public static byte[] Write(IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("au moins une feuille", nameof(sheets));
        using var flux = new MemoryStream();
        using (var zip = new ZipArchive(flux, ZipArchiveMode.Create, leaveOpen: true))
        {
            Ecrire(zip, "[Content_Types].xml", TypesDeContenu(sheets.Count));
            Ecrire(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Ecrire(zip, "xl/workbook.xml", Classeur(sheets));
            Ecrire(zip, "xl/_rels/workbook.xml.rels", LiensDuClasseur(sheets.Count));
            Ecrire(zip, "xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++) Ecrire(zip, $"xl/worksheets/sheet{i + 1}.xml", Feuille(sheets[i]));
        }

        return flux.ToArray();
    }

    /// <summary>Le numéro de série Excel d'une date (le 1er janvier 1900 vaut 1, à l'erreur historique de 1900 près).</summary>
    public static int Serial(DateOnly jour) => jour.DayNumber - Origine.DayNumber;

    private static void Ecrire(ZipArchive zip, string chemin, string contenu)
    {
        using var sortie = new StreamWriter(zip.CreateEntry(chemin, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        sortie.Write(contenu);
    }

    private static string TypesDeContenu(int feuilles)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= feuilles; i++)
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return sb.Append("</Types>").ToString();
    }

    private static string Classeur(IReadOnlyList<XlsxSheet> sheets)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (var i = 0; i < sheets.Count; i++)
            sb.Append($"""<sheet name="{NomDeFeuille(sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        return sb.Append("</sheets></workbook>").ToString();
    }

    private static string LiensDuClasseur(int feuilles)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= feuilles; i++)
            sb.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        sb.Append($"""<Relationship Id="rId{feuilles + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        return sb.Append("</Relationships>").ToString();
    }

    // Styles (cellXfs) : 0 normal, 1 titre en gras, 2 date, 3 deux décimales, 4 trois décimales, 5 entier.
    private const string Styles = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="2"><numFmt numFmtId="164" formatCode="dd/mm/yyyy"/><numFmt numFmtId="165" formatCode="0.000"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="1" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""";

    private static int Style(XlsxFormat f) => f switch
    {
        XlsxFormat.Date => 2,
        XlsxFormat.TwoDecimals => 3,
        XlsxFormat.ThreeDecimals => 4,
        XlsxFormat.Integer => 5,
        _ => 0,
    };

    private static string Feuille(XlsxSheet feuille)
    {
        var colonnes = feuille.Columns;
        var derniere = Colonne(colonnes.Count - 1);
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        // Ligne de titres figée : elle reste visible quand on fait défiler.
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<cols>");
        for (var c = 0; c < colonnes.Count; c++)
            sb.Append(FormattableString.Invariant($"""<col min="{c + 1}" max="{c + 1}" width="{colonnes[c].Width}" customWidth="1"/>"""));
        sb.Append("</cols><sheetData>");

        sb.Append("""<row r="1">""");
        for (var c = 0; c < colonnes.Count; c++)
            sb.Append($"""<c r="{Colonne(c)}1" t="inlineStr" s="1"><is><t>{Xml(colonnes[c].Title)}</t></is></c>""");
        sb.Append("</row>");

        for (var l = 0; l < feuille.Rows.Count; l++)
        {
            var numero = l + 2;
            var ligne = feuille.Rows[l];
            sb.Append($"""<row r="{numero}">""");
            for (var c = 0; c < Math.Min(ligne.Count, colonnes.Count); c++)
                sb.Append(Case($"{Colonne(c)}{numero}", ligne[c], colonnes[c].Format));
            sb.Append("</row>");
        }

        sb.Append("</sheetData>");
        // Filtre automatique sur les titres : trier ou filtrer par compteur sans rien régler.
        if (feuille.Rows.Count > 0) sb.Append($"""<autoFilter ref="A1:{derniere}{feuille.Rows.Count + 1}"/>""");
        return sb.Append("</worksheet>").ToString();
    }

    private static string Case(string reference, object? valeur, XlsxFormat format) => valeur switch
    {
        null => "",
        string texte => $"""<c r="{reference}" t="inlineStr"><is><t>{Xml(texte)}</t></is></c>""",
        DateOnly jour => $"""<c r="{reference}" s="{Style(XlsxFormat.Date)}"><v>{Serial(jour)}</v></c>""",
        double d when !double.IsFinite(d) => "",
        IConvertible n when n is double or float or decimal or int or long or short or byte =>
            $"""<c r="{reference}" s="{Style(format)}"><v>{Convert.ToDouble(n, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)}</v></c>""",
        _ => $"""<c r="{reference}" t="inlineStr"><is><t>{Xml(Convert.ToString(valeur, CultureInfo.InvariantCulture) ?? "")}</t></is></c>""",
    };

    /// <summary>A, B, … Z, AA, AB… : la lettre d'une colonne (0 = A).</summary>
    public static string Colonne(int index)
    {
        var lettres = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) lettres = (char)('A' + (n - 1) % 26) + lettres;
        return lettres;
    }

    private static string Xml(string texte) => SecurityElement.Escape(texte) ?? "";

    /// <summary>Un nom de feuille accepté par Excel : 31 caractères au plus, sans <c>\ / ? * [ ] :</c>.</summary>
    private static string NomDeFeuille(string nom)
    {
        var propre = new string([.. nom.Where(ch => "\\/?*[]:".IndexOf(ch) < 0)]).Trim();
        if (propre.Length == 0) propre = "Feuille";
        return Xml(propre.Length > 31 ? propre[..31] : propre);
    }
}
