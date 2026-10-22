using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>Le code tapé sur le digicode : masqué, borné, oublié après usage ou abandon.</summary>
public class ClavierTests
{
    private static Clavier Taper(FakeTimeProvider horloge, params string[] touches)
    {
        var clavier = new Clavier(horloge);
        foreach (var t in touches) clavier.Appuyer(t);
        return clavier;
    }

    [Fact]
    public void Le_code_ne_s_affiche_qu_en_etoiles_et_se_corrige()
    {
        var clavier = Taper(new FakeTimeProvider(), "1", "2", "3", "9", "<", "4");

        Assert.Equal("* * * *", clavier.Etat().Masque);
        Assert.Equal("1234", clavier.Prendre());
    }

    [Fact]
    public void Un_code_pris_est_oublie_et_C_efface_tout()
    {
        var horloge = new FakeTimeProvider();
        var clavier = Taper(horloge, "1", "2");
        clavier.Prendre();
        Assert.Null(clavier.Prendre());
        Assert.Equal("", clavier.Etat().Masque);

        clavier.Appuyer("5");
        clavier.Appuyer("C");
        Assert.Null(clavier.Prendre());
    }

    [Fact]
    public void Un_code_abandonne_expire_et_ne_sert_pas_au_suivant()
    {
        var horloge = new FakeTimeProvider();
        var clavier = Taper(horloge, "1", "2", "3", "4");

        horloge.Advance(Clavier.Expiration);

        Assert.Equal("", clavier.Etat().Masque);
        clavier.Appuyer("9");
        Assert.Equal("9", clavier.Prendre());   // la saisie repart de zéro, sans les quatre chiffres d'avant
    }

    [Fact]
    public void Le_code_est_borne_et_les_touches_inconnues_ignorees()
    {
        var clavier = Taper(new FakeTimeProvider(), [.. Enumerable.Repeat("7", 20), "x", "12"]);

        Assert.Equal(new string('7', Clavier.MaxChiffres), clavier.Prendre());
    }

    [Fact]
    public void Le_message_s_efface_de_lui_meme_ou_a_la_touche_suivante()
    {
        var horloge = new FakeTimeProvider();
        var clavier = new Clavier(horloge);
        clavier.Signaler("Code incorrect");
        Assert.Equal("Code incorrect", clavier.Etat().Message);

        horloge.Advance(Clavier.DureeMessage);
        Assert.Null(clavier.Etat().Message);

        clavier.Signaler("Code incorrect");
        clavier.Appuyer("1");
        Assert.Null(clavier.Etat().Message);
    }
}
