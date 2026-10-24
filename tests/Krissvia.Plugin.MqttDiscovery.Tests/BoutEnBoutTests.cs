using Krissvia.Contracts;

namespace Krissvia.Plugin.MqttDiscovery.Tests;

/// <summary>La tranche verticale : deux modules ESPHome jusqu'au registre et au magasin d'états du cœur, et retour.</summary>
public class BoutEnBoutTests
{
    private const string Etage = "esp32-sht30-etage", MacEtage = "a1b2c3d4e5f6";
    private const string Salon = "esp32-sht30-salon", MacSalon = "f6e5d4c3b2a1";

    /// <summary>Les deux sondes tournent déjà : configurations, statut et mesures retenus sur le broker.</summary>
    private static void DeuxSondes(SimulateurBroker b)
    {
        foreach (var (noeud, mac, temperature, humidite) in new[] { (Etage, MacEtage, "17.2", "75"), (Salon, MacSalon, "21.4", "52") })
        {
            b.Retenir(Esphome.TopicConfig(noeud, "sensor", "temperature"), Esphome.Temperature(noeud, mac));
            b.Retenir(Esphome.TopicConfig(noeud, "sensor", "humidite"), Esphome.Humidite(noeud, mac));
            b.Retenir(Esphome.TopicStatut(noeud), "online");
            b.Retenir(Esphome.TopicEtat(noeud, "sensor", "temperature"), temperature);
            b.Retenir(Esphome.TopicEtat(noeud, "sensor", "humidite"), humidite);
        }
    }

    [Fact]
    public async Task Les_sondes_deja_en_service_arrivent_dans_le_coeur_avec_leurs_mesures()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync(DeuxSondes);

        var etage = await banc.EntityIdAsync($"{MacEtage}/ESPsensortemperature");
        var salon = await banc.EntityIdAsync($"{MacSalon}/ESPsensortemperature");
        var humidite = await banc.EntityIdAsync($"{MacSalon}/ESPsensorhumidite");

        // Les mesures n'arrivent que si le plugin s'est abonné aux topics d'état annoncés par les configurations.
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(salon) is { Available: true } && banc.Etats.Get(etage) is { Available: true }
                                                 && banc.Etats.Get(humidite) is { Available: true }, "mesures des deux sondes");
        Assert.Equal(17.2, banc.Etats.Get(etage)!.Value.GetDouble());
        Assert.Equal(21.4, banc.Etats.Get(salon)!.Value.GetDouble());
        Assert.Equal(52, banc.Etats.Get(humidite)!.Value.GetDouble());

        // Les quatre entités, aucune laissée « disparue » par une déclaration partielle.
        var entites = await banc.Registre.ListAsync();
        Assert.Equal(4, entites.Count(e => e.IntegrationId == "mqtt-discovery"));
        Assert.DoesNotContain(entites, e => e.Missing);
    }

    [Fact]
    public async Task Une_sonde_qui_s_eteint_devient_indisponible_dans_le_coeur()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync(DeuxSondes);
        var etage = await banc.EntityIdAsync($"{MacEtage}/ESPsensortemperature");
        var salon = await banc.EntityIdAsync($"{MacSalon}/ESPsensortemperature");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(etage) is { Available: true }, "sonde de l'étage disponible");

        // Dernière volonté du module, publiée par le broker quand le Wi-Fi tombe.
        await banc.Broker.PublierAsync(Esphome.TopicStatut(Etage), "offline");

        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(etage) is { Available: false }, "sonde de l'étage indisponible");
        Assert.True(banc.Etats.Get(salon)!.Available);
    }

    [Fact]
    public async Task Une_nouvelle_mesure_arrive_dans_le_coeur()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync(DeuxSondes);
        var etage = await banc.EntityIdAsync($"{MacEtage}/ESPsensortemperature");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(etage) is { Available: true }, "sonde disponible");

        await banc.Broker.PublierAsync(Esphome.TopicEtat(Etage, "sensor", "temperature"), "18.1");

        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(etage)!.Value.GetDouble() == 18.1, "nouvelle mesure");
    }

    [Fact]
    public async Task Un_ordre_du_coeur_part_sur_le_command_topic_du_relais()
    {
        await using var banc = await BancBoutEnBout.DemarrerAsync(b =>
        {
            b.Retenir(Esphome.TopicConfig(Etage, "switch", "relais"), Esphome.Relais(Etage, MacEtage));
            b.Retenir(Esphome.TopicEtat(Etage, "switch", "relais"), "OFF");
        });
        var relais = await banc.EntityIdAsync($"{MacEtage}/ESPswitchrelais");
        await BancBoutEnBout.AttendreAsync(() => banc.Etats.Get(relais) is { Available: true }, "relais disponible");

        var issue = await banc.Commandeur.SendAsync(relais, StandardCommands.TurnOn, CommandPriority.Normal);

        Assert.Equal(CommandOutcome.Accepted, issue);
        Assert.Contains(("krissvia/esp32-sht30-etage/switch/relais/command", "ON"), banc.Broker.Publications);
    }
}
