using System.Globalization;
using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Plugin.Teleinfo;

/// <summary>
/// La trame Linky (mode standard) publiée en JSON sur MQTT par un module branché sur la prise TIC → entités du
/// contrat. Sans réseau ni horloge propre : tout l'état du plugin vit ici, derrière un seul verrou.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Les valeurs arrivent en chaînes (<c>"SINSTS": "01234"</c>) : converties ici, une seule fois. Une valeur
/// illisible est ignorée, jamais lue comme zéro — un zéro sur SINSTS ferait tout rétablir au délesteur.</item>
/// <item>Les index sont en Wh dans la trame, publiés en kWh.</item>
/// <item>Le compteur parle seul et souvent : un silence prolongé (module débranché, Wi-Fi perdu) rend le plugin
/// hors ligne au lieu de laisser croire que la dernière puissance est toujours vraie.</item>
/// </list>
/// </remarks>
internal sealed class TeleinfoTranslator(string topic, string deviceId, string deviceName, TimeSpan silenceMax)
{
    private sealed record Champ(
        string Cle, string Suffixe, string? Nom, string? Classe, string? Unite, StateClass? ClasseEtat, Func<string, JsonElement?> Convertir);

    private static readonly Champ[] Champs =
    [
        new("SINSTS", "sinsts", null, DeviceClasses.ApparentPower, Units.VoltAmpere, StateClass.Measurement, Entier),
        new("EAST", "east", "index total", DeviceClasses.Energy, Units.KilowattHour, StateClass.TotalIncreasing, WattheuresEnKwh),
        new("EASF01", "easf01", "index 1", DeviceClasses.Energy, Units.KilowattHour, StateClass.TotalIncreasing, WattheuresEnKwh),
        new("EASF02", "easf02", "index 2", DeviceClasses.Energy, Units.KilowattHour, StateClass.TotalIncreasing, WattheuresEnKwh),
        new("IRMS1", "irms1", null, DeviceClasses.Current, Units.Ampere, StateClass.Measurement, Entier),
        new("URMS1", "urms1", null, DeviceClasses.Voltage, Units.Volt, StateClass.Measurement, Entier),
        new("LTARF", "ltarf", "tarif", DeviceClasses.Enum, null, null, Libelle),
        new("NTARF", "ntarf", "numero tarif", null, null, null, Entier),
    ];

    private readonly Lock _verrou = new();
    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
    private DateTimeOffset? _dernierMessage;
    private bool _muet;
    private bool _mqttConnecte;

    public string Abonnement => topic;

    public Declare Declaration() => new()
    {
        Devices =
        [
            new DeviceDescriptor
            {
                UniqueId = deviceId,
                Name = deviceName,
                Manufacturer = "Enedis",
                Model = "Linky (TIC standard)",
                Entities = Champs.Select(c => new EntityDescriptor
                {
                    UniqueId = $"{deviceId}:{c.Suffixe}",
                    Kind = EntityKind.Sensor,
                    Name = c.Nom,
                    DeviceClass = c.Classe,
                    Unit = c.Unite,
                    StateClass = c.ClasseEtat,
                }).ToList(),
            },
        ],
    };

    public IReadOnlyList<Message> MqttConnecte()
    {
        lock (_verrou) _mqttConnecte = true;
        return [];
    }

    public IReadOnlyList<Message> MqttDeconnecte()
    {
        lock (_verrou) _mqttConnecte = false;
        return [new Availability { Online = false }];
    }

    public IReadOnlyList<Message> TraiterMessage(string sujet, string charge, DateTimeOffset maintenant)
    {
        if (sujet != topic) return [];

        Dictionary<string, string> brut;
        try
        {
            using var document = JsonDocument.Parse(charge);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];

            // Les modules TIC n'écrivent pas tous les étiquettes à l'identique : la casse ne compte pas.
            brut = document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .GroupBy(p => p.Name.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First().Value.ValueKind == JsonValueKind.String
                    ? g.First().Value.GetString()!.Trim()
                    : g.First().Value.GetRawText());
        }
        catch (JsonException)
        {
            return [];
        }

        lock (_verrou)
        {
            var etats = new List<EntityState>();
            foreach (var champ in Champs)
            {
                if (!brut.TryGetValue(champ.Cle, out var texte) || champ.Convertir(texte) is not { } valeur) continue;

                var id = $"{deviceId}:{champ.Suffixe}";
                _valeurs[id] = valeur;
                etats.Add(new EntityState { EntityUniqueId = id, Value = valeur });
            }

            if (etats.Count == 0) return [];

            var messages = new List<Message>();
            if (_muet)
            {
                _muet = false;
                messages.Add(new Availability { Online = true });
            }

            _dernierMessage = maintenant;
            messages.Add(new StateBatch { States = etats });
            return messages;
        }
    }

    /// <summary>À appeler régulièrement : signale une fois le passage au silence.</summary>
    public IReadOnlyList<Message> Surveiller(DateTimeOffset maintenant)
    {
        lock (_verrou)
        {
            if (_muet || _dernierMessage is not { } dernier || maintenant - dernier <= silenceMax) return [];

            _muet = true;
            return [new Availability { Online = false }];
        }
    }

    public IReadOnlyList<Message> Instantane()
    {
        lock (_verrou)
        {
            var messages = new List<Message> { Declaration() };
            if (_valeurs.Count > 0)
                messages.Add(new StateBatch
                {
                    States = _valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value }).ToList(),
                });
            messages.Add(new Availability { Online = _mqttConnecte && !_muet && _dernierMessage is not null });
            return messages;
        }
    }

    // ── Conversions ──────────────────────────────────────────────────────────

    private static JsonElement? Entier(string texte) =>
        long.TryParse(texte, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? JsonSerializer.SerializeToElement(n) : null;

    private static JsonElement? WattheuresEnKwh(string texte) =>
        double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out var wh) && wh >= 0
            ? JsonSerializer.SerializeToElement(Math.Round(wh / 1000, 3))
            : null;

    private static JsonElement? Libelle(string texte) =>
        texte.Length > 0 ? JsonSerializer.SerializeToElement(texte) : null;
}
