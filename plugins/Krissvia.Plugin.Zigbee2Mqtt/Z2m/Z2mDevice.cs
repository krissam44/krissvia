using System.Text.Json;

namespace Krissvia.Plugin.Zigbee2Mqtt.Z2m;

/// <summary>Un appareil tel que Zigbee2MQTT le publie dans <c>bridge/devices</c> (message retenu).</summary>
internal sealed record Z2mDevice
{
    /// <summary>Adresse IEEE : la seule identité stable. Le friendly_name change quand on renomme l'appareil.</summary>
    public required string IeeeAddress { get; init; }

    public required string FriendlyName { get; init; }

    /// <summary>Coordinator, Router ou EndDevice.</summary>
    public string? Type { get; init; }

    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? Description { get; init; }
    public bool Disabled { get; init; }
    public IReadOnlyList<Z2mExpose> Exposes { get; init; } = [];
}

/// <summary>
/// Une capacité exposée. Les types génériques (<c>numeric</c>, <c>binary</c>, <c>enum</c>…) portent une
/// <see cref="Property"/> ; les types spécifiques (<c>switch</c>, <c>light</c>, <c>cover</c>, <c>climate</c>…)
/// regroupent des <see cref="Features"/>.
/// </summary>
internal sealed record Z2mExpose
{
    public required string Type { get; init; }
    public string? Name { get; init; }
    public string? Property { get; init; }
    public string? Endpoint { get; init; }
    public string? Unit { get; init; }

    /// <summary>Bits : 1 = publiée dans l'état, 2 = modifiable par <c>/set</c>, 4 = lisible par <c>/get</c>.</summary>
    public int Access { get; init; }

    public JsonElement? ValueOn { get; init; }
    public JsonElement? ValueOff { get; init; }
    public JsonElement? ValueToggle { get; init; }
    public double? ValueMax { get; init; }
    public IReadOnlyList<Z2mExpose> Features { get; init; } = [];

    public bool Publiee => (Access & 1) != 0;
    public bool Modifiable => (Access & 2) != 0;
    public bool Lisible => (Access & 4) != 0;
}

/// <summary>Lecture tolérante de <c>bridge/devices</c> : un champ inattendu ne doit pas faire perdre tout le réseau.</summary>
internal static class Z2mParser
{
    public static IReadOnlyList<Z2mDevice> ParseDevices(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var appareils = new List<Z2mDevice>();
        foreach (var d in document.RootElement.EnumerateArray())
        {
            var ieee = Chaine(d, "ieee_address");
            if (ieee is null) continue;

            var definition = d.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.Object ? def : (JsonElement?)null;
            appareils.Add(new Z2mDevice
            {
                IeeeAddress = ieee,
                FriendlyName = Chaine(d, "friendly_name") ?? ieee,
                Type = Chaine(d, "type"),
                Disabled = d.TryGetProperty("disabled", out var desactive) && desactive.ValueKind == JsonValueKind.True,
                Vendor = definition is { } v ? Chaine(v, "vendor") : null,
                Model = definition is { } m ? Chaine(m, "model") : null,
                Description = definition is { } ds ? Chaine(ds, "description") : null,
                Exposes = definition is { } e ? Liste(e, "exposes") : [],
            });
        }

        return appareils;
    }

    private static IReadOnlyList<Z2mExpose> Liste(JsonElement parent, string nom)
    {
        if (!parent.TryGetProperty(nom, out var tableau) || tableau.ValueKind != JsonValueKind.Array) return [];

        var liste = new List<Z2mExpose>();
        foreach (var x in tableau.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object || Chaine(x, "type") is not { } type) continue;

            liste.Add(new Z2mExpose
            {
                Type = type,
                Name = Chaine(x, "name"),
                Property = Chaine(x, "property"),
                Endpoint = Chaine(x, "endpoint"),
                Unit = Chaine(x, "unit"),
                Access = x.TryGetProperty("access", out var a) && a.TryGetInt32(out var acces) ? acces : 1,
                ValueOn = Valeur(x, "value_on"),
                ValueOff = Valeur(x, "value_off"),
                ValueToggle = Valeur(x, "value_toggle"),
                ValueMax = x.TryGetProperty("value_max", out var max) && max.TryGetDouble(out var vmax) ? vmax : null,
                Features = Liste(x, "features"),
            });
        }

        return liste;
    }

    private static string? Chaine(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? Valeur(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
}
