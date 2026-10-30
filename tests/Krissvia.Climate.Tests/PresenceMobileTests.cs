using System.Text.Json;
using Krissvia.Climate.Presence;
using Krissvia.Climate.Thermostats;
using Krissvia.Mobile.Client;

namespace Krissvia.Climate.Tests;

/// <summary>
/// L'appli mobile lit <c>GET /api/absence</c> tel que le serveur l'écrit (PublicApi.MapAbsenceApi, options de
/// <see cref="ThermostatService.Json"/>) : un champ renommé ici lui ferait afficher « Maison occupée » pendant une
/// absence forcée, et allumer le mauvais bouton du forçage.
/// </summary>
public class PresenceMobileTests
{
    [Fact]
    public void L_appli_mobile_lit_l_absence_le_forcage_et_les_appareils()
    {
        var retour = new DateTimeOffset(2026, 12, 27, 18, 0, 0, TimeSpan.FromHours(1));
        var videDepuis = new DateTimeOffset(2026, 10, 30, 8, 15, 0, TimeSpan.Zero);
        var presence = new HomePresenceState(false, false, [], ["Redmi-Note-11"], ["Xiaomi-14T"], videDepuis);
        // L'enveloppe de GET /api/absence autour des objets du cœur.
        var reponse = JsonSerializer.SerializeToElement(new
        {
            state = new HomeAbsenceState(true, AbsenceSource.Manual, retour),
            period = new ScheduledAbsence(retour.AddDays(-7), retour),
            forced = (bool?)true,
            presence,
        }, ThermostatService.Json);

        var lu = EtatPresence.Lire(reponse)!;

        Assert.True(lu.Absente);
        Assert.Equal("manual", lu.Source);
        Assert.Equal(Forcage.Absent, lu.Forcage);
        Assert.Equal(retour, lu.Retour);
        Assert.Equal(["Redmi-Note-11"], lu.Absents);
        Assert.Equal(["Xiaomi-14T"], lu.Inconnus);
        Assert.Equal(videDepuis, lu.VideDepuis);
        Assert.Equal(retour, lu.Periode!.Retour);
    }

    [Fact]
    public void La_detection_perdue_et_le_forcage_present_arrivent_aussi()
    {
        var presence = new HomePresenceState(true, true, [], [], ["Redmi-Note-11"], null);
        var reponse = JsonSerializer.SerializeToElement(new
        {
            state = new HomeAbsenceState(false, AbsenceSource.Manual, null),
            forced = (bool?)false,
            presence,
        }, ThermostatService.Json);

        var lu = EtatPresence.Lire(reponse)!;

        Assert.True(lu.DetectionPerdue);
        Assert.Equal(Forcage.Present, lu.Forcage);
        Assert.Equal("Présence forcée", lu.Libelle);
    }

    [Theory]
    [InlineData(AbsenceSource.None, "none")]
    [InlineData(AbsenceSource.Detection, "detection")]
    [InlineData(AbsenceSource.Scheduled, "scheduled")]
    [InlineData(AbsenceSource.Manual, "manual")]
    public void Chaque_source_arrive_sous_le_code_que_l_appli_attend(AbsenceSource source, string code)
    {
        var reponse = JsonSerializer.SerializeToElement(new { state = new HomeAbsenceState(true, source, null) }, ThermostatService.Json);

        Assert.Equal(code, EtatPresence.Lire(reponse)!.Source);
    }
}
