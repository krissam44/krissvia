using System.Globalization;
using Krissvia.Plugin.Ipp.Protocole;

namespace Krissvia.Plugin.Ipp.Tests;

/// <summary>
/// Les trames de <c>Trames/</c> : écrites octet par octet d'après la RFC 8010 par un script indépendant du codec (pas
/// par lui : un test qui relit ce que le codec a écrit ne prouverait que sa cohérence avec lui-même), aux valeurs de
/// la Samsung M2020 de l'utilisateur telles que Gladys les affiche.
/// </summary>
internal static class Trames
{
    public static byte[] Lire(string fichier) => Hex(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Trames", fichier)));

    /// <summary>« 01 0b  # commentaire » → octets ; tout ce qui suit « # » est ignoré.</summary>
    public static byte[] Hex(string texte) =>
    [
        .. texte.Split('\n')
            .Select(l => l.Split('#')[0])
            .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
    ];
}

public class CodecTests
{
    [Fact]
    public void La_requete_est_un_Get_Printer_Attributes_conforme_a_la_RFC_8010()
    {
        var octets = IppCodec.EncodeGetPrinterAttributes("ipp://10.0.0.5:631/ipp/print", 7, ["printer-state", "marker-levels"]);

        var attendu = Trames.Hex("""
            01 01 00 0b 00 00 00 07                                     # IPP/1.1, Get-Printer-Attributes, request-id 7
            01                                                          # operation-attributes-tag
            47 00 12 61 74 74 72 69 62 75 74 65 73 2d 63 68 61 72 73 65 74 00 05 75 74 66 2d 38
            48 00 1b 61 74 74 72 69 62 75 74 65 73 2d 6e 61 74 75 72 61 6c 2d 6c 61 6e 67 75 61 67 65 00 02 65 6e
            45 00 0b 70 72 69 6e 74 65 72 2d 75 72 69                   # printer-uri
            00 1c 69 70 70 3a 2f 2f 31 30 2e 30 2e 30 2e 35 3a 36 33 31 2f 69 70 70 2f 70 72 69 6e 74
            42 00 14 72 65 71 75 65 73 74 69 6e 67 2d 75 73 65 72 2d 6e 61 6d 65 00 08 6b 72 69 73 73 76 69 61
            44 00 14 72 65 71 75 65 73 74 65 64 2d 61 74 74 72 69 62 75 74 65 73   # requested-attributes
            00 0d 70 72 69 6e 74 65 72 2d 73 74 61 74 65
            44 00 00 00 0d 6d 61 72 6b 65 72 2d 6c 65 76 65 6c 73       # valeur suivante : nom vide
            03                                                          # end-of-attributes-tag
            """);
        Assert.Equal(Convert.ToHexString(attendu), Convert.ToHexString(octets));
    }

    [Fact]
    public void La_reponse_de_la_Samsung_se_lit_en_entier()
    {
        var r = IppCodec.Decode(Trames.Lire("samsung-m2020-prete.ipp.txt"));

        Assert.Equal((1, 1, 0, 42), (r.Major, r.Minor, r.StatusCode, r.RequestId));
        Assert.True(r.Success);
        Assert.Equal([IppTag.OperationAttributes, IppTag.PrinterAttributes, IppTag.UnsupportedAttributes], r.Groups.Select(g => g.Tag));
        Assert.Equal(3, r.Printer("printer-state")!.Values.Single());
        Assert.Equal(IppTag.Enum, r.Printer("printer-state")!.Tag);
        Assert.Equal("Samsung M2020 Series", r.Printer("printer-info")!.Values.Single());   // textWithLanguage : la langue écartée
        Assert.Equal(["Black Toner_S/N_:CRUM-18020727263", "Transfer Roller", "Fuser Life", "Pick-up Roller"], r.Printer("marker-names")!.Values);
        Assert.Equal([96, 86, 86, 86], r.Printer("marker-levels")!.Values);

        // Les valeurs que le plugin n'utilise pas doivent se lire aussi : une seule mal comprise décale toute la suite.
        var media = Assert.IsType<IppCollection>(r.Printer("media-col-default")!.Values.Single());
        var taille = Assert.IsType<IppCollection>(media["media-size"].Single());
        Assert.Equal([21000], taille["x-dimension"]);
        Assert.Equal([29700], taille["y-dimension"]);
        Assert.Equal(["stationery"], media["media-type"]);
        Assert.Equal(new IppResolution(600, 600, 3), r.Printer("printer-resolution-default")!.Values.Single());
        Assert.Equal(new IppRange(1, 99), r.Printer("copies-supported")!.Values.Single());
        Assert.Equal(true, r.Printer("printer-is-accepting-jobs")!.Values.Single());
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 20, 38, 12, TimeSpan.FromHours(2)), r.Printer("printer-current-time")!.Values.Single());
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Printer("printer-firmware-version")!.Values.Single());

        // Après la collection : on est bien retombé sur les attributs de premier niveau, puis le groupe « unsupported ».
        var nonPris = r.Groups.Single(g => g.Tag == IppTag.UnsupportedAttributes).Attributes.Single();
        Assert.Equal(("printer-location", IppTag.Unsupported), (nonPris.Name, nonPris.Tag));
        Assert.Null(nonPris.Values.Single());
        Assert.Null(r.Printer("printer-location"));   // hors du groupe des attributs d'imprimante
    }

    [Theory]
    [InlineData("01 01 00 00 00 00 00 01 04 23 00 01 61 00 04 00 00", "tronquée")]                 // entier coupé
    [InlineData("01 01 00 00 00 00 00 01 04 23 00 01 61 00 02 00 03 03", "au lieu de 4")]           // entier de 2 octets
    [InlineData("01 01 00 00 00 00 00 01 23 00 01 61 00 04 00 00 00 03 03", "avant tout groupe")]   // valeur hors groupe
    [InlineData("01 01 00 00 00 00 00 01 04 21 00 00 00 04 00 00 00 03 03", "sans attribut")]       // suite sans attribut
    [InlineData("01 01 00 00 00 00 00 01 04 21 00 01 61 00 04 00 00 00 03", "pas de fin")]          // pas de 0x03
    [InlineData("01 01 00 00 00 00 00 01 04 34 00 01 61 00 00", "collection tronquée")]          // collection sans fin
    [InlineData("01 01 00", "tronquée")]                                                            // en-tête incomplet
    public void Une_trame_incoherente_est_refusee_sans_lire_hors_des_bornes(string hex, string motif)
    {
        var ex = Assert.Throws<IppFormatException>(() => IppCodec.Decode(Trames.Hex(hex)));
        Assert.Contains(motif, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.63:631/ipp/print", "ipp://192.168.1.63:631/ipp/print")]
    [InlineData("192.168.1.63", "ipp://192.168.1.63:631/ipp/print")]
    [InlineData("ipp://192.168.1.63/ipp/print", "ipp://192.168.1.63:631/ipp/print")]
    [InlineData("http://192.168.1.63/ipp/print", "ipp://192.168.1.63:631/ipp/print")]        // pas 80 : IPP écoute sur 631
    [InlineData("https://imprimante.local:8631/ipp/print", "ipps://imprimante.local:8631/ipp/print")]
    [InlineData("  ipps://192.168.1.63  ", "ipps://192.168.1.63:631/ipp/print")]
    [InlineData("ipp://cups.local:631/printers/Bureau", "ipp://cups.local:631/printers/Bureau")]
    [InlineData("ipp://192.168.1.63:80/ipp/print", "ipp://192.168.1.63:80/ipp/print")]       // port écrit : gardé
    public void Les_adresses_saisies_se_normalisent(string saisie, string attendue)
    {
        Assert.True(IppAdresses.TryNormaliser(saisie, out var u));
        Assert.Equal(attendue, u.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://192.168.1.63/")]
    [InlineData("http://")]
    public void Une_adresse_invalide_est_refusee(string saisie) => Assert.False(IppAdresses.TryNormaliser(saisie, out _));

    [Fact]
    public void IPP_passe_par_HTTP_et_IPPS_par_HTTPS_sur_le_meme_port()
    {
        Assert.Equal("http://10.0.0.5:631/ipp/print", IppAdresses.Transport(new Uri("ipp://10.0.0.5:631/ipp/print")).ToString());
        Assert.Equal("https://10.0.0.5:631/ipp/print", IppAdresses.Transport(new Uri("ipps://10.0.0.5:631/ipp/print")).ToString());
    }
}
