using Krissvia.Plugin.Ipp.Decouverte;
using Krissvia.Plugin.Ipp.Protocole;

namespace Krissvia.Plugin.Ipp.Tests;

/// <summary>
/// Les trames <c>capture-*</c> : capturées sur la Samsung M2020 de l'utilisateur le 24/10/2026, avec son accord, en
/// lecture seule (<c>Get-Printer-Attributes</c> et une question mDNS). Elles ont appris trois choses que les trames
/// écrites d'après la documentation ne disaient pas : l'imprimante ne publie en IPP que son toner (les rouleaux et le
/// four de la carte Gladys n'y sont pas, même en IPP/2.0) ; son <c>printer-info</c> répète le modèle suivi du nom
/// réseau ; elle ne répond au mDNS qu'aux questions ordinaires, envoyées depuis le port 5353.
/// </summary>
public class CaptureTests
{
    [Fact]
    public void La_vraie_reponse_a_la_requete_du_plugin_donne_le_toner()
    {
        var r = ReleveImprimante.Lire(IppCodec.Decode(Trames.Lire("capture-samsung-m2020.ipp.txt")), 10);

        Assert.Equal("16a65700-007c-1000-bb49-30cda7201287", r.Uuid);
        Assert.Equal(("Samsung M2020 Series", "Samsung M2020 Series"), (r.Nom, r.Modele));   // sans « (SEC30CDA7201287) »
        Assert.Equal((EtatImprimante.Prete, "Printer is ready to print"), (r.Etat, r.Message));
        Assert.Empty(r.Motifs);
        // Son seuil à elle (5 %), pas le réglage ; la couleur annoncée est blanche (sic), gardée telle quelle.
        Assert.Equal([new Consommable("black_toner", "Black Toner", 96, false, 5, "toner", "#ffffff")], r.Consommables);
        Assert.False(Diagnostic.Evaluer(r).Probleme);
    }

    [Fact]
    public void La_vraie_reponse_complete_se_lit_jusqu_au_bout()
    {
        var r = IppCodec.Decode(Trames.Lire("capture-samsung-m2020-all.ipp.txt"));

        Assert.True(r.Success);
        Assert.Equal(["1.0", "1.1", "2.0"], r.Printer("ipp-versions-supported")!.Values);
        Assert.Equal([96], r.Printer("marker-levels")!.Values);
        Assert.Null(r.Printer("printer-supply"));   // pas de rouleaux ni de four en IPP
        Assert.Equal(r.Printer("printer-uuid")!.Values, ["urn:uuid:16a65700-007c-1000-bb49-30cda7201287"]);
        Assert.Equal(94, r.Groups.Single(g => g.Tag == IppTag.PrinterAttributes).Attributes.Count);   // aucun perdu en route
    }

    [Fact]
    public void La_vraie_annonce_mDNS_donne_l_adresse_de_l_imprimante()
    {
        var service = Assert.Single(DnsMessage.Assembler(DnsMessage.Lire(Trames.Lire("capture-bonjour-samsung.dns.txt"))));

        Assert.Equal("Samsung M2020 Series (SEC30CDA7201287)", service.Instance);
        Assert.Equal("ipp://192.168.1.63:631/ipp/print", service.Url.ToString());
        Assert.Equal("16a65700-007c-1000-bb49-30cda7201287", service.Uuid);
        Assert.Equal("Samsung M2020 Series", service.Modele);
    }

    [Fact]
    public void Une_question_d_une_autre_machine_n_est_pas_une_reponse()
    {
        // Même trame, bit « réponse » éteint : c'est ce que d'autres machines envoient au groupe qu'on écoute.
        var trame = Trames.Lire("capture-bonjour-samsung.dns.txt");
        trame[2] &= 0x7F;

        Assert.Empty(DnsMessage.Lire(trame));
    }

    [Fact]
    public void Un_printer_info_choisi_par_l_utilisateur_reste_le_nom()
    {
        var r = ReleveImprimante.Lire(ReleveTests.Reponse(("printer-info", ["Bureau"]), ("printer-make-and-model", ["HP LaserJet Pro M404"])), 10);

        Assert.Equal("Bureau", r.Nom);
    }
}
