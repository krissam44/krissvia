using Krissvia.Contracts;
using static Krissvia.Plugin.Zigbee2Mqtt.Tests.ReseauZigbee;

namespace Krissvia.Plugin.Zigbee2Mqtt.Tests;

/// <summary>La tranche verticale : un appareil Zigbee jusqu'au magasin d'états du cœur, et un ordre du cœur jusqu'à l'appareil.</summary>
public class BoutEnBoutTests
{
    [Fact]
    public async Task Les_appareils_et_leurs_mesures_arrivent_dans_le_coeur()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();

        await banc.Z2m.DemarrerAsync();

        var temperature = await banc.EntityIdAsync($"{Sonde}:temperature");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(temperature) is { Available: true }, "température disponible");
        Assert.Equal(19.5, banc.Etats.Get(temperature)!.Value.GetDouble());
        Assert.Contains("zigbee2mqtt", banc.Hub.ConnectedPlugins);
    }

    [Fact]
    public async Task Un_ordre_du_coeur_actionne_la_prise_et_son_nouvel_etat_revient()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        await banc.Z2m.DemarrerAsync();
        var prise = await banc.EntityIdAsync($"{Prise}:state");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(prise) is { Available: true }, "prise disponible");

        var issue = await banc.Commandeur.SendAsync(prise, StandardCommands.TurnOff, CommandPriority.Safety);

        Assert.Equal(CommandOutcome.Accepted, issue);
        Assert.Contains(("zigbee2mqtt/Prise lave-linge/set", """{"state":"OFF"}"""), banc.Z2m.Publications);
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(prise)!.Value.ValueKind == System.Text.Json.JsonValueKind.False, "prise éteinte");
    }

    [Fact]
    public async Task Un_relais_silencieux_est_interroge_et_son_etat_arrive_dans_le_coeur()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        banc.Z2m.EtatsReels["Inter cuisine"] = """{"state_l1":"OFF","state_l2":"ON"}""";

        await banc.Z2m.DemarrerAsync();   // ne publie aucun état pour l'interrupteur

        var voie2 = await banc.EntityIdAsync($"{Inter}:state_l2");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(voie2) is { Available: true }, "état de la voie 2 obtenu par /get");
        Assert.True(banc.Etats.Get(voie2)!.Value.GetBoolean());
        Assert.Contains(banc.Z2m.Publications, p => p.Topic == "zigbee2mqtt/Inter cuisine/get");
    }

    [Fact]
    public async Task Un_ordre_impossible_echoue_sans_rien_publier()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        await banc.Z2m.DemarrerAsync();
        var temperature = await banc.EntityIdAsync($"{Sonde}:temperature");

        Assert.Equal(CommandOutcome.Failed, await banc.Commandeur.SendAsync(temperature, StandardCommands.TurnOn));
        Assert.DoesNotContain(banc.Z2m.Publications, p => p.Topic.EndsWith("/set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Broker_perdu_les_entites_deviennent_indisponibles_et_les_ordres_injoignables()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        await banc.Z2m.DemarrerAsync();
        var prise = await banc.EntityIdAsync($"{Prise}:state");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(prise) is { Available: true }, "prise disponible");

        await banc.Z2m.CouperBrokerAsync();

        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(prise) is { Available: false }, "prise indisponible");
        Assert.Equal(CommandOutcome.Unreachable, await banc.Commandeur.SendAsync(prise, StandardCommands.TurnOff));
    }

    [Fact]
    public async Task Apres_une_coupure_avec_le_coeur_le_plugin_se_reconnecte_et_resynchronise()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        await banc.Z2m.DemarrerAsync();
        var temperature = await banc.EntityIdAsync($"{Sonde}:temperature");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(temperature) is { Available: true }, "température disponible");

        banc.CouperLaConnexion();

        // Le cœur constate la coupure (entités indisponibles), puis la resynchronisation les rallume avec leur valeur.
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(temperature) is { Available: false }, "coupure constatée");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(temperature) is { Available: true }, "resynchronisation");
        Assert.Equal(19.5, banc.Etats.Get(temperature)!.Value.GetDouble());
    }

    [Fact]
    public async Task Un_appareil_hors_ligne_dans_Zigbee2MQTT_l_est_aussi_dans_le_coeur()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync();
        await banc.Z2m.DemarrerAsync();
        var temperature = await banc.EntityIdAsync($"{Sonde}:temperature");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(temperature) is { Available: true }, "température disponible");

        await banc.Z2m.PublierAsync("zigbee2mqtt/Sonde salon/availability", """{"state":"offline"}""");

        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(temperature) is { Available: false }, "sonde hors ligne");
    }
}
