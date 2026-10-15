using System.Text.Json;

namespace Krissvia.Contracts.Tests;

/// <summary>
/// Ces tests fixent le JSON du contrat : un plugin écrit dans un autre langage doit pouvoir
/// parler exactement ce format. Les modifier, c'est modifier le contrat.
/// </summary>
public class MessageSerializationTests
{
    private static string Ecrire(Message message) => JsonSerializer.Serialize(message, ContractJson.Options);

    private static JsonElement Json(object? valeur) => JsonSerializer.SerializeToElement(valeur);

    private static IEnumerable<Message> Exemples()
    {
        var prise = new DeviceDescriptor
        {
            UniqueId = "0x00158d0001a2b3c4",
            Name = "Prise salon",
            Manufacturer = "Tuya",
            Entities =
            [
                new EntityDescriptor
                {
                    UniqueId = "0x00158d0001a2b3c4:switch",
                    Kind = EntityKind.Switch,
                    Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff],
                },
                new EntityDescriptor
                {
                    UniqueId = "0x00158d0001a2b3c4:power",
                    Kind = EntityKind.Sensor,
                    DeviceClass = DeviceClasses.Power,
                    Unit = Units.Watt,
                    StateClass = StateClass.Measurement,
                },
            ],
        };

        yield return new Hello
        {
            PluginId = "zigbee2mqtt",
            PluginVersion = "0.1.0",
            ContractVersion = ContractInfo.Version,
        };
        yield return new Configure { Config = Json(new { broker = "192.168.1.10", port = 1883 }) };
        yield return new Declare { Devices = [prise] };
        yield return new StateBatch
        {
            States =
            [
                new EntityState
                {
                    EntityUniqueId = "0x00158d0001a2b3c4:power",
                    Value = Json(1200.5),
                    Timestamp = new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero),
                },
            ],
        };
        yield return new Availability { DeviceUniqueId = "0x00158d0001a2b3c4", Online = false };
        yield return new Discovered { Candidates = [prise] };
        yield return new CommandRequest
        {
            Id = "7f3a",
            EntityUniqueId = "0x00158d0001a2b3c4:switch",
            Command = StandardCommands.TurnOff,
            Priority = CommandPriority.Safety,
            TtlSeconds = 5,
        };
        yield return new CommandResult { Id = "7f3a", Outcome = CommandOutcome.Unreachable, Detail = "timeout" };
        yield return new Ping();
        yield return new Shutdown { Reason = "mise à jour" };
    }

    [Fact]
    public void Chaque_message_survit_a_un_aller_retour_JSON()
    {
        foreach (var message in Exemples())
        {
            var json = Ecrire(message);

            var relu = JsonSerializer.Deserialize<Message>(json, ContractJson.Options);

            Assert.NotNull(relu);
            Assert.Equal(message.GetType(), relu.GetType());
            Assert.Equal(json, Ecrire(relu));
        }
    }

    [Fact]
    public void Le_type_du_message_est_ecrit_dans_le_champ_type()
    {
        var json = Ecrire(new CommandRequest { Id = "1", EntityUniqueId = "e", Command = StandardCommands.TurnOn });

        Assert.Contains("\"type\":\"command\"", json);
    }

    [Fact]
    public void Le_champ_type_peut_ne_pas_etre_en_premier()
    {
        const string json = """{"plugin_id":"zigbee2mqtt","plugin_version":"0.1.0","contract_version":"0.1","type":"hello"}""";

        var message = JsonSerializer.Deserialize<Message>(json, ContractJson.Options);

        var hello = Assert.IsType<Hello>(message);
        Assert.Equal("zigbee2mqtt", hello.PluginId);
    }

    [Fact]
    public void Les_enums_sont_ecrits_en_snake_case()
    {
        var entite = new EntityDescriptor
        {
            UniqueId = "x",
            Kind = EntityKind.BinarySensor,
            StateClass = StateClass.TotalIncreasing,
        };

        var json = JsonSerializer.Serialize(entite, ContractJson.Options);

        Assert.Contains("\"kind\":\"binary_sensor\"", json);
        Assert.Contains("\"state_class\":\"total_increasing\"", json);
    }

    [Fact]
    public void Un_champ_absent_n_est_pas_ecrit()
    {
        // Sans identifiant d'appareil, la disponibilité concerne le plugin lui-même.
        var json = Ecrire(new Availability { Online = true });

        Assert.DoesNotContain("device_unique_id", json);
    }

    [Fact]
    public void Un_champ_obligatoire_manquant_est_refuse()
    {
        const string json = """{"type":"hello","plugin_id":"x"}""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Message>(json, ContractJson.Options));
    }

    [Fact]
    public void Un_type_de_message_inconnu_est_refuse()
    {
        const string json = """{"type":"inconnu"}""";

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<Message>(json, ContractJson.Options));
    }
}
