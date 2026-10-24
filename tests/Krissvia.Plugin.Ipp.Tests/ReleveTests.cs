using Krissvia.Plugin.Ipp.Decouverte;
using Krissvia.Plugin.Ipp.Protocole;

namespace Krissvia.Plugin.Ipp.Tests;

/// <summary>Du relevé IPP à ce que publie le plugin : consommables, état, et le diagnostic « problème ».</summary>
public class ReleveTests
{
    private static ReleveImprimante Lire(string trame, int seuil = 10) => ReleveImprimante.Lire(IppCodec.Decode(Trames.Lire(trame)), seuil);

    [Fact]
    public void La_Samsung_donne_ses_quatre_consommables_sans_numero_de_serie()
    {
        var r = Lire("samsung-m2020-prete.ipp.txt");

        Assert.Equal("16a65700-007c-1000-bb49-30cda71ce2f2", r.Uuid);   // sans « urn:uuid: », en minuscules
        Assert.Equal(("Samsung M2020 Series", "Samsung M2020 Series"), (r.Nom, r.Modele));
        Assert.Equal(EtatImprimante.Prete, r.Etat);
        Assert.Empty(r.Motifs);   // « none » n'est pas un motif
        Assert.Equal(
            [
                new Consommable("black_toner", "Black Toner", 96, false, 10, "toner", "#000000"),
                new Consommable("transfer_roller", "Transfer Roller", 86, false, 10, "transfer-roller", null),
                new Consommable("fuser_life", "Fuser Life", 86, false, 10, "fuser", null),
                new Consommable("pick_up_roller", "Pick-up Roller", 86, false, 10, "other", null),
            ],
            r.Consommables);
    }

    [Fact]
    public void Le_seuil_de_l_imprimante_prime_sur_le_reglage_et_les_niveaux_speciaux_restent_inconnus()
    {
        var r = Lire("samsung-m2020-bourrage.ipp.txt", seuil: 20);

        // marker-low-levels : 10 pour le toner (le sien), 0 pour les autres (pas de seuil : celui du réglage).
        Assert.Equal([10, 20, 20, 20], r.Consommables.Select(c => c.SeuilBas));
        // -3 : « il en reste », niveau inconnu ; -2 : inconnu.
        Assert.Equal([8, 86, null, null], r.Consommables.Select(c => c.Niveau));
        Assert.Equal([false, false, true, false], r.Consommables.Select(c => c.Restant));
        Assert.Equal([true, false, false, false], r.Consommables.Select(c => c.Bas));
        Assert.Equal(EtatImprimante.Arretee, r.Etat);
        Assert.Equal(["media-jam-error", "toner-low-warning", "cover-open-report"], r.Motifs);
    }

    [Theory]
    [InlineData("Black Toner_S/N_:CRUM-18020727263", "Black Toner")]
    [InlineData("Black Toner S/N:CRUM-1802", "Black Toner")]
    [InlineData("Cyan Cartridge", "Cyan Cartridge")]
    [InlineData("  Fuser Life  ", "Fuser Life")]
    [InlineData("S/N:123", null)]
    [InlineData("", null)]
    public void Le_numero_de_serie_sort_du_nom(string brut, string? attendu) => Assert.Equal(attendu, ReleveImprimante.NomPropre(brut));

    [Theory]
    [InlineData("Black Toner", "black_toner")]
    [InlineData("Pick-up Roller", "pick_up_roller")]
    [InlineData("Unité d'imagerie", "unite_d_imagerie")]
    [InlineData("???", "consommable")]
    public void L_identifiant_d_un_consommable_est_stable(string nom, string id) => Assert.Equal(id, ReleveImprimante.Identifiant(nom));

    [Fact]
    public void Deux_consommables_de_meme_nom_gardent_chacun_leur_entite()
    {
        var r = ReleveImprimante.Lire(Reponse(("marker-names", ["Toner", "Toner", "Toner"]), ("marker-levels", [50, 40, 30])), 10);

        Assert.Equal(["toner", "toner_2", "toner_3"], r.Consommables.Select(c => c.Id));
    }

    [Fact]
    public void Un_niveau_sans_nom_reste_un_consommable()
    {
        var r = ReleveImprimante.Lire(Reponse(("marker-levels", [70, 5])), 10);

        Assert.Equal(["Consommable 1", "Consommable 2"], r.Consommables.Select(c => c.Nom));
        Assert.Equal([false, true], r.Consommables.Select(c => c.Bas));
    }

    internal static IppResponse Reponse(params (string Nom, object?[] Valeurs)[] attributs) =>
        new(1, 1, 0, 1, [new IppGroup(IppTag.PrinterAttributes, [.. attributs.Select(a => new IppAttribute(a.Nom, 0, a.Valeurs))])]);
}

public class DiagnosticTests
{
    private static ReleveImprimante Releve(EtatImprimante etat = EtatImprimante.Prete, string[]? motifs = null, params Consommable[] consommables) =>
        new("u", "Imprimante", null, etat, motifs ?? [], null, consommables);

    private static Consommable Toner(int? niveau, int seuil = 10) => new("toner", "Toner", niveau, false, seuil, "toner", null);

    [Fact]
    public void Une_imprimante_prete_et_pleine_n_a_pas_de_probleme()
    {
        var d = Diagnostic.Evaluer(Releve(consommables: Toner(96)));

        Assert.False(d.Probleme);
        Assert.Empty(d.Alertes);
    }

    [Fact]
    public void Une_imprimante_arretee_est_un_probleme()
    {
        var d = Diagnostic.Evaluer(Releve(EtatImprimante.Arretee));

        Assert.True(d.Probleme);
        Assert.Equal(["Imprimante arrêtée"], d.Alertes);
    }

    [Fact]
    public void Le_bourrage_de_la_trame_est_un_probleme_et_se_dit_en_francais()
    {
        var d = Diagnostic.Evaluer(ReleveImprimante.Lire(IppCodec.Decode(Trames.Lire("samsung-m2020-bourrage.ipp.txt")), 10));

        Assert.True(d.Probleme);
        // « cover-open-report » : simple information, jamais montrée.
        Assert.Equal(["Imprimante arrêtée", "Bourrage papier", "Toner bas", "Black Toner bas (8 %)"], d.Alertes);
    }

    [Theory]
    [InlineData("media-jam-error", true)]
    [InlineData("media-jam", true)]               // sans suffixe, une panne de la liste
    [InlineData("some-vendor-thing-error", true)] // inconnu, mais en -error
    [InlineData("toner-low-warning", true)]       // consommable bas d'après l'imprimante
    [InlineData("toner-low", true)]
    [InlineData("marker-supply-low-warning", true)]
    [InlineData("marker-waste-almost-full-warning", true)]
    [InlineData("opc-near-eol-warning", true)]
    [InlineData("toner-low-report", false)]       // simple information
    [InlineData("paused", false)]                 // sans suffixe, mais pas une panne
    [InlineData("sleep", false)]
    [InlineData("other-warning", false)]
    [InlineData("media-jam-report", false)]
    public void Un_motif_est_un_probleme_selon_sa_nature_et_son_suffixe(string motif, bool probleme) =>
        Assert.Equal(probleme, Diagnostic.Evaluer(Releve(motifs: [motif])).Probleme);

    [Theory]
    [InlineData(10, 10, true)]    // au seuil : bas
    [InlineData(11, 10, false)]
    [InlineData(0, 10, true)]
    [InlineData(null, 10, false)] // niveau inconnu : on ne conclut rien
    public void Un_consommable_sous_son_seuil_est_un_probleme(int? niveau, int seuil, bool probleme)
    {
        var d = Diagnostic.Evaluer(Releve(consommables: Toner(niveau, seuil)));

        Assert.Equal(probleme, d.Probleme);
        Assert.Equal(probleme ? [$"Toner bas ({niveau} %)"] : [], d.Alertes);
    }

    [Fact]
    public void Un_avertissement_ordinaire_se_montre_sans_etre_un_probleme()
    {
        var d = Diagnostic.Evaluer(Releve(motifs: ["paused"]));

        Assert.False(d.Probleme);
        Assert.Equal(["En pause"], d.Alertes);
    }

    [Theory]
    [InlineData("toner-low-warning", "toner-low", "warning")]
    [InlineData("media-jam-error", "media-jam", "error")]
    [InlineData("cover-open-report", "cover-open", "report")]
    [InlineData("media-jam", "media-jam", null)]
    public void Un_motif_se_decoupe_en_base_et_suffixe(string motif, string base_, string? suffixe) =>
        Assert.Equal((base_, suffixe), Diagnostic.Decouper(motif));
}

public class MdnsTests
{
    [Fact]
    public void La_reponse_Bonjour_de_la_Samsung_donne_son_adresse_IPP()
    {
        var enregistrements = DnsMessage.Lire(Trames.Lire("bonjour-samsung.dns.txt"));

        var service = Assert.Single(DnsMessage.Assembler(enregistrements));
        Assert.Equal("Samsung M2020 Series (SEC30CDA71CE2F2)", service.Instance);
        // L'adresse IP plutôt que SEC30CDA71CE2F2.local ; le chemin vient de « rp ».
        Assert.Equal("ipp://192.168.1.63:631/ipp/print", service.Url.ToString());
        Assert.Equal("16a65700-007c-1000-bb49-30cda71ce2f2", service.Uuid);
        Assert.Equal("Samsung M2020 Series", service.Modele);
    }

    [Fact]
    public void Sans_adresse_ni_chemin_on_garde_le_nom_local_et_le_chemin_d_IPP_Everywhere()
    {
        var service = Assert.Single(DnsMessage.Assembler(
        [
            new DnsRecord("_ipps._tcp.local", DnsRecord.TypePtr, Target: "Bureau._ipps._tcp.local"),
            new DnsRecord("Bureau._ipps._tcp.local", DnsRecord.TypeSrv, Target: "bureau.local", Port: 443),
        ]));

        Assert.Equal("Bureau", service.Instance);
        Assert.Equal("ipps://bureau.local:443/ipp/print", service.Url.ToString());
    }

    [Fact]
    public void Le_chemin_annonce_par_rp_est_celui_de_l_adresse()
    {
        // Une file CUPS partagée : le chemin n'est pas celui d'IPP Everywhere.
        var service = Assert.Single(DnsMessage.Assembler(
        [
            new DnsRecord("_ipp._tcp.local", DnsRecord.TypePtr, Target: "Bureau @ serveur._ipp._tcp.local"),
            new DnsRecord("Bureau @ serveur._ipp._tcp.local", DnsRecord.TypeSrv, Target: "serveur.local", Port: 631),
            new DnsRecord("Bureau @ serveur._ipp._tcp.local", DnsRecord.TypeTxt, Txt: new Dictionary<string, string> { ["rp"] = "printers/Bureau" }),
        ]));

        Assert.Equal("ipp://serveur.local:631/printers/Bureau", service.Url.ToString());
    }

    [Fact]
    public void L_annonce_ipp_passe_avant_l_annonce_ipps_de_la_meme_imprimante()
    {
        var services = DnsMessage.Assembler(
        [
            new DnsRecord("_ipps._tcp.local", DnsRecord.TypePtr, Target: "X._ipps._tcp.local"),
            new DnsRecord("X._ipps._tcp.local", DnsRecord.TypeSrv, Target: "x.local", Port: 631),
            new DnsRecord("_ipp._tcp.local", DnsRecord.TypePtr, Target: "X._ipp._tcp.local"),
            new DnsRecord("X._ipp._tcp.local", DnsRecord.TypeSrv, Target: "x.local", Port: 631),
            new DnsRecord("x.local", DnsRecord.TypeA, Address: System.Net.IPAddress.Parse("10.0.0.9")),
        ]);

        Assert.Equal(["ipp://10.0.0.9:631/ipp/print", "ipps://10.0.0.9:631/ipp/print"], services.Select(s => s.Url.ToString()));
    }

    [Fact]
    public void La_question_est_un_PTR_par_service()
    {
        var q = DnsMessage.Question(0x0102, DnsMessage.ServiceIpp);

        Assert.Equal(Convert.ToHexString(Trames.Hex("""
            01 02 00 00 00 01 00 00 00 00 00 00         # id, requête, 1 question
            04 5f 69 70 70 04 5f 74 63 70 05 6c 6f 63 61 6c 00
            00 0c 00 01                                 # PTR, IN
            """)), Convert.ToHexString(q));
    }

    [Theory]
    [InlineData("12 34 84 00 00 00 00 01 00 00 00 00 c0 0c 00 0c 00 01 00 00 00 01 00 02 c0 0c")]   // pointeur sur lui-même
    [InlineData("12 34 84 00 00 00 00 01 00 00 00 00 04 5f 69 70")]                                  // tronquée
    [InlineData("12 34")]
    public void Une_trame_DNS_illisible_ne_fait_pas_tomber_la_decouverte(string hex) =>
        Assert.Empty(DnsMessage.Lire(Trames.Hex(hex)));
}
