using System.Text.Json;
using Krissvia.Climate.Alarm;
using Krissvia.Mobile.Client;

namespace Krissvia.Climate.Tests;

/// <summary>
/// L'appli mobile lit l'état de l'alarme tel que la centrale l'écrit (<see cref="AlarmEngine.Valeur"/>) : un champ
/// renommé ici la laisserait afficher « désarmée » pendant une alarme déclenchée, sans que rien ne casse.
/// </summary>
public class AlarmeMobileTests
{
    [Fact]
    public void L_appli_mobile_lit_l_etat_publie_par_la_centrale()
    {
        var t0 = new DateTimeOffset(2026, 10, 30, 3, 12, 0, TimeSpan.Zero);
        var etat = new AlarmState(AlarmMode.Triggered, 0, t0, "Fenêtre chambre", Ring: 1, Ringing: true);
        // L'enveloppe de GET /api/alarm (AlarmApi) autour de la valeur publiée.
        var reponse = JsonSerializer.SerializeToElement(new
        {
            enabled = true,
            state = AlarmEngine.Valeur(etat, "Porte garage"),
            faults_now = new[] { "Porte d'entrée ouverte" },
            locked_seconds = 0,
        }, AlarmService.Json);

        var lu = EtatAlarme.Lire(reponse)!;

        Assert.True(lu.Activee);
        Assert.Equal("triggered", lu.Etat);
        Assert.Equal("Déclenchée", lu.Libelle);
        Assert.True(lu.Sonne);
        Assert.Equal("Fenêtre chambre", lu.Capteur);
        Assert.Equal(t0, lu.DeclencheeA);
        Assert.Equal("Porte garage", lu.DernierRefus);
        Assert.Equal(["Porte d'entrée ouverte"], lu.DefautsMaintenant);
    }

    [Fact]
    public void Chaque_etat_de_la_centrale_a_un_libelle_dans_l_appli()
    {
        foreach (var mode in Enum.GetValues<AlarmMode>())
        {
            var lu = EtatAlarme.Lire(JsonSerializer.SerializeToElement(new
            {
                enabled = true, state = AlarmEngine.Valeur(new AlarmState(mode, 5, null, null), AlarmFaults.None),
            }, AlarmService.Json))!;

            // Un état ajouté à la centrale sans libellé côté appli s'afficherait en code brut.
            Assert.NotEqual(lu.Etat, lu.Libelle);
            Assert.Null(lu.DernierRefus);
        }
    }
}
