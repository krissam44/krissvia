using Krissvia.Contracts;

namespace Krissvia.Plugin.Zigbee2Mqtt.Z2m;

/// <summary>Un appareil traduit : sa description pour le cœur, et les liens de chacune de ses entités.</summary>
internal sealed record AppareilTraduit(Z2mDevice Source, DeviceDescriptor Descripteur, IReadOnlyList<EntityBinding> Liens);

/// <summary>
/// Traduit les <c>exposes</c> Zigbee2MQTT en entités du contrat.
/// </summary>
/// <remarks>
/// Liste blanche délibérée : seules les propriétés connues deviennent des entités. Un appareil Zigbee expose
/// aussi des réglages (<c>power_on_behavior</c>, <c>child_lock</c>, délais…) et des diagnostics
/// (<c>linkquality</c>) qui encombreraient le registre sans rien apporter aux moteurs ; on en ajoute une quand
/// un besoin réel apparaît. Une propriété MODIFIABLE n'est jamais une mesure : c'est un réglage.
/// </remarks>
internal static class ExposeMapper
{
    private sealed record Mesure(string Classe, string Unite, StateClass ClasseEtat = StateClass.Measurement);

    private static readonly Dictionary<string, Mesure> Mesures = new(StringComparer.Ordinal)
    {
        ["temperature"] = new(DeviceClasses.Temperature, Units.Celsius),
        ["local_temperature"] = new(DeviceClasses.Temperature, Units.Celsius),   // tête thermostatique
        ["humidity"] = new(DeviceClasses.Humidity, Units.Percent),
        ["pressure"] = new(DeviceClasses.Pressure, Units.Hectopascal),
        ["illuminance"] = new(DeviceClasses.Illuminance, Units.Lux),
        ["illuminance_lux"] = new(DeviceClasses.Illuminance, Units.Lux),
        ["co2"] = new(DeviceClasses.CarbonDioxide, Units.PartsPerMillion),
        ["power"] = new(DeviceClasses.Power, Units.Watt),
        ["energy"] = new(DeviceClasses.Energy, Units.KilowattHour, StateClass.TotalIncreasing),
        // L'index des modules SONOFF (BASIC-ZB1GSP) : à côté, energy_today / _month / _yesterday ne sont que des tranches
        // de cet index, que le cœur recalcule lui-même ; les reprendre ferait trois compteurs pour un seul circuit.
        ["total_energy"] = new(DeviceClasses.Energy, Units.KilowattHour, StateClass.TotalIncreasing),
        ["voltage"] = new(DeviceClasses.Voltage, Units.Volt),
        ["current"] = new(DeviceClasses.Current, Units.Ampere),
        ["battery"] = new(DeviceClasses.Battery, Units.Percent),
    };

    /// <summary>Unités Zigbee2MQTT → unité du contrat et facteur. Les piles Aqara annoncent leur tension en mV.</summary>
    private static readonly Dictionary<string, (string Unite, double Facteur)> Conversions = new(StringComparer.Ordinal)
    {
        ["mV"] = (Units.Volt, 0.001),
        ["mA"] = (Units.Ampere, 0.001),
        ["Wh"] = (Units.KilowattHour, 0.001),
        ["kW"] = (Units.Watt, 1000),
    };

    private static readonly Dictionary<string, (string Classe, bool Inverse)> Binaires = new(StringComparer.Ordinal)
    {
        // contact = true quand l'aimant est en place, donc FERMÉ ; le contrat dit true = ouvert. L'inversion ne sert
        // que si l'appareil ne déclare pas value_on (voir EtatBinaireBinding).
        ["contact"] = (DeviceClasses.Opening, true),
        ["occupancy"] = (DeviceClasses.Occupancy, false),
        ["presence"] = (DeviceClasses.Occupancy, false),
        ["water_leak"] = (DeviceClasses.Moisture, false),
        ["smoke"] = (DeviceClasses.Smoke, false),
        ["tamper"] = (DeviceClasses.Tamper, false),
        ["vibration"] = (DeviceClasses.Vibration, false),
        ["battery_low"] = (DeviceClasses.Battery, false),
    };

    /// <summary>Null pour le coordinateur, un appareil désactivé, ou un appareil sans rien d'utile.</summary>
    public static AppareilTraduit? Traduire(Z2mDevice appareil)
    {
        if (appareil.Type == "Coordinator" || appareil.Disabled) return null;

        var liens = new List<EntityBinding>();
        var pris = new HashSet<string>(StringComparer.Ordinal);
        void Ajouter(EntityBinding? lien)
        {
            if (lien is not null && pris.Add(lien.UniqueId)) liens.Add(lien);
        }

        foreach (var expose in appareil.Exposes)
        {
            switch (expose.Type)
            {
                case "switch":
                    Ajouter(Interrupteur(appareil, expose));
                    break;
                case "light":
                    Ajouter(Lumiere(appareil, expose));
                    break;
                case "cover":
                    Ajouter(Volet(appareil, expose));
                    break;
                case "climate":
                    // Pas d'entité climat pour l'instant (la régulation est au cœur) : seule la sonde est utile.
                    foreach (var f in expose.Features.Where(f => f.Property == "local_temperature")) Ajouter(Generique(appareil, f));
                    break;
                default:
                    Ajouter(Generique(appareil, expose));
                    break;
            }
        }

        if (liens.Count == 0) return null;

        var descripteur = new DeviceDescriptor
        {
            UniqueId = appareil.IeeeAddress,
            Name = appareil.FriendlyName,
            Manufacturer = appareil.Vendor,
            Model = appareil.Model,
            Entities = liens.Select(l => l.Descriptor).ToList(),
        };

        return new AppareilTraduit(appareil, descripteur, liens);
    }

    private static EntityBinding? Generique(Z2mDevice appareil, Z2mExpose x)
    {
        if (x.Property is not { } propriete || !x.Publiee) return null;

        switch (x.Type)
        {
            case "numeric" when !x.Modifiable:
                var nomStandard = x.Name ?? propriete;
                if (!Mesures.TryGetValue(nomStandard, out var mesure)) return null;

                var (unite, facteur) = x.Unit is { } u && Conversions.TryGetValue(u, out var c) ? c : (mesure.Unite, 1.0);
                // Une unité inconnue (et non convertible) est un signal à ne pas maquiller : on la transmet telle quelle.
                if (x.Unit is { } brute && !Conversions.ContainsKey(brute) && brute != mesure.Unite) unite = brute;

                return new MesureBinding(Id(appareil, propriete), NomEntite(x), propriete,
                    mesure.Classe, unite, mesure.ClasseEtat, facteur);

            case "binary" when !x.Modifiable:
                if (!Binaires.TryGetValue(x.Name ?? propriete, out var binaire)) return null;
                return new EtatBinaireBinding(Id(appareil, propriete), NomEntite(x), propriete,
                    binaire.Classe, x.ValueOn, binaire.Inverse);

            case "enum" when propriete == "action":
                return new EvenementBinding(Id(appareil, propriete), NomEntite(x), propriete);

            default:
                return null;
        }
    }

    private static EntityBinding? Interrupteur(Z2mDevice appareil, Z2mExpose x)
    {
        var etat = x.Features.FirstOrDefault(f => f.Type == "binary" && f.Name == "state" && f.Modifiable);
        if (etat?.Property is not { } propriete) return null;

        return new InterrupteurBinding(Id(appareil, propriete), x.Endpoint ?? etat.Endpoint, propriete,
            etat.ValueOn, etat.ValueOff, etat.ValueToggle, etat.Lisible);
    }

    private static EntityBinding? Lumiere(Z2mDevice appareil, Z2mExpose x)
    {
        var etat = x.Features.FirstOrDefault(f => f.Type == "binary" && f.Name == "state");
        if (etat?.Property is not { } propriete) return null;

        var luminosite = x.Features.FirstOrDefault(f => f.Type == "numeric" && f.Name == "brightness" && f.Modifiable);
        var point = x.Endpoint ?? etat.Endpoint;
        return new LumiereBinding(Id(appareil, point is null ? "light" : $"light_{point}"), point, propriete,
            etat.ValueOn, luminosite?.Property, luminosite?.ValueMax ?? 254, etat.Lisible);
    }

    private static EntityBinding? Volet(Z2mDevice appareil, Z2mExpose x)
    {
        var etat = x.Features.FirstOrDefault(f => f.Name == "state");
        var position = x.Features.FirstOrDefault(f => f.Type == "numeric" && f.Name == "position");
        if (etat?.Property is null && position?.Property is null) return null;

        var point = x.Endpoint ?? etat?.Endpoint;
        return new VoletBinding(Id(appareil, point is null ? "cover" : $"cover_{point}"), point, etat?.Property, position?.Property);
    }

    /// <summary>Adresse IEEE + propriété : stable quand on renomme l'appareil dans Zigbee2MQTT.</summary>
    private static string Id(Z2mDevice appareil, string suffixe) => $"{appareil.IeeeAddress}:{suffixe}";

    /// <summary>Sur un appareil multivoie, le point d'accès distingue les entités (« temperature l1 »).</summary>
    private static string? NomEntite(Z2mExpose x) =>
        x.Endpoint is { } point ? $"{x.Name ?? x.Property} {point}" : null;
}
