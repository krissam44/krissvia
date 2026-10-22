using System.Text.Json;

namespace Krissvia.Contracts;

/// <summary>
/// Famille technique d'une entité, fournie par le plugin. L'usage (radiateur, chauffe-eau…)
/// est choisi par l'utilisateur dans le cœur et ne fait pas partie du contrat.
/// </summary>
public enum EntityKind
{
    Sensor,
    BinarySensor,
    Switch,
    Light,
    Cover,
    Event,

    /// <summary>
    /// Thermostat. Publié par le cœur (module Climate), pas par un plugin : état composite (ambiante, consigne, mode,
    /// statut…), modes disponibles dans <c>extra</c>.
    /// </summary>
    Climate,
}

public enum StateClass
{
    /// <summary>Mesure instantanée (température, puissance).</summary>
    Measurement,

    /// <summary>Compteur qui ne fait que croître (index d'énergie).</summary>
    TotalIncreasing,
}

/// <summary>Une capacité d'un appareil : une mesure, un interrupteur, un volet…</summary>
public sealed record EntityDescriptor
{
    /// <summary>
    /// Identifiant immuable, unique au sein du plugin (ex. adresse IEEE + suffixe).
    /// Jamais un nom affiché : un renommage ne doit rien casser.
    /// </summary>
    public required string UniqueId { get; init; }

    public required EntityKind Kind { get; init; }

    /// <summary>Nom proposé (« Gauche », « Puissance »…). L'utilisateur peut le changer dans le cœur.</summary>
    public string? Name { get; init; }

    /// <summary>Voir <see cref="DeviceClasses"/>. Chaîne ouverte : un plugin peut en proposer d'autres.</summary>
    public string? DeviceClass { get; init; }

    /// <summary>Voir <see cref="Units"/>.</summary>
    public string? Unit { get; init; }

    public StateClass? StateClass { get; init; }

    /// <summary>Commandes acceptées (voir <see cref="StandardCommands"/>).</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>Données propres au plugin, opaques pour le cœur (adresse IP, canal…).</summary>
    public IReadOnlyDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Un appareil physique et ses entités.</summary>
public sealed record DeviceDescriptor
{
    public required string UniqueId { get; init; }
    public string? Name { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<EntityDescriptor> Entities { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Valeur d'une entité. Le type de <see cref="Value"/> dépend du <see cref="EntityKind"/> :
/// nombre (sensor), booléen (binary_sensor, switch), objet (light, cover), chaîne (event).
/// </summary>
public sealed record EntityState
{
    public required string EntityUniqueId { get; init; }
    public required JsonElement Value { get; init; }

    /// <summary>Instant de la mesure côté plugin (UTC). Absent : le cœur utilise l'heure de réception.</summary>
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>Classes de mesure normalisées (valeur de <see cref="EntityDescriptor.DeviceClass"/>).</summary>
public static class DeviceClasses
{
    public const string Temperature = "temperature";
    public const string Humidity = "humidity";
    public const string Power = "power";

    /// <summary>Puissance apparente, en VA : ce que comparent l'abonnement et le disjoncteur (trame Linky SINSTS).</summary>
    public const string ApparentPower = "apparent_power";
    public const string Energy = "energy";
    public const string Voltage = "voltage";
    public const string Current = "current";
    /// <summary>sensor : niveau en %. binary_sensor : true = pile faible.</summary>
    public const string Battery = "battery";
    public const string Pressure = "pressure";
    public const string Illuminance = "illuminance";
    public const string CarbonDioxide = "carbon_dioxide";

    /// <summary>Vitesse du vent, en km/h.</summary>
    public const string WindSpeed = "wind_speed";

    /// <summary>
    /// sensor dont la valeur est un OBJET : les conditions du moment et la prévision d'un lieu (docs/PLUGINS-PYTHON.md,
    /// plugin Météo). Les grandeurs dont le cœur se sert (température extérieure d'un thermostat) sont publiées à part,
    /// en entités numériques.
    /// </summary>
    public const string Weather = "weather";

    /// <summary>sensor dont la valeur est un OBJET : l'état du délesteur, publié par le cœur (puissance, seuils, délestés).</summary>
    public const string LoadShedding = "load_shedding";

    /// <summary>sensor dont la valeur est un libellé (tarif en cours…), pas un nombre.</summary>
    public const string Enum = "enum";

    /// <summary>binary_sensor : true = OUVERT (le plugin inverse les conventions contraires).</summary>
    public const string Opening = "opening";

    /// <summary>
    /// binary_sensor : true = la personne (son téléphone) est à la maison. Distinct d'<see cref="Occupancy"/>, qui dit
    /// qu'un détecteur voit du mouvement dans une pièce : la présence du foyer se calcule sur ceux-ci.
    /// </summary>
    public const string Presence = "presence";

    /// <summary>
    /// sensor dont la valeur est un OBJET : la présence du foyer, calculée par le cœur sur les capteurs
    /// <see cref="Presence"/> (maison occupée, qui est là, détection perdue).
    /// </summary>
    public const string HomePresence = "home_presence";

    /// <summary>
    /// sensor dont la valeur est un OBJET : l'état de l'alarme, publié par le cœur (état, décompte, capteur en cause,
    /// défauts du dernier contrôle). Lecture seule : l'armement passe par un code (page Alarme, API).
    /// </summary>
    public const string Alarm = "alarm";

    // Supervision de la connexion Internet : les mêmes classes quelle que soit la box (Livebox, Freebox…), pour que la
    // supervision du cœur ne dépende pas du fournisseur.

    /// <summary>binary_sensor : true = la connexion Internet est établie.</summary>
    public const string Connectivity = "connectivity";

    /// <summary>sensor dont la valeur est une chaîne : l'adresse IP publique.</summary>
    public const string PublicIp = "public_ip";

    /// <summary>sensor : un débit, en kbit/s (synchronisation descendante ou montante).</summary>
    public const string DataRate = "data_rate";

    /// <summary>
    /// sensor dont la valeur est un OBJET : l'état complet de la connexion (lien, type, IP, débits et leurs maximums,
    /// marge de bruit, atténuation). La même forme pour toute box : la carte « Connexion Internet » le dessine.
    /// </summary>
    public const string InternetConnection = "internet_connection";

    /// <summary>
    /// sensor dont la valeur est un OBJET : les appareils vus sur le réseau local (<c>hosts</c> : nom, MAC, IP,
    /// connecté, suivi). Commande <c>set_tracked</c> <c>{"mac", "tracked"}</c> pour suivre la présence d'un appareil.
    /// </summary>
    public const string NetworkHosts = "network_hosts";

    /// <summary>binary_sensor : true = présence détectée.</summary>
    public const string Occupancy = "occupancy";

    /// <summary>binary_sensor : true = fuite d'eau détectée.</summary>
    public const string Moisture = "moisture";

    /// <summary>binary_sensor : true = fumée détectée.</summary>
    public const string Smoke = "smoke";

    /// <summary>binary_sensor : true = boîtier ouvert ou arraché.</summary>
    public const string Tamper = "tamper";

    /// <summary>binary_sensor : true = vibration détectée.</summary>
    public const string Vibration = "vibration";
}

/// <summary>Unités normalisées : le plugin convertit, le cœur ne reçoit que celles-ci.</summary>
public static class Units
{
    public const string Celsius = "°C";
    public const string Percent = "%";
    public const string Watt = "W";
    public const string VoltAmpere = "VA";
    public const string KilowattHour = "kWh";
    public const string Volt = "V";
    public const string Ampere = "A";
    public const string Hectopascal = "hPa";
    public const string Lux = "lx";
    public const string PartsPerMillion = "ppm";
    public const string KilometerPerHour = "km/h";
    public const string KilobitPerSecond = "kbit/s";
}

/// <summary>Commandes standard par famille. Un plugin peut en déclarer d'autres.</summary>
public static class StandardCommands
{
    // switch, light
    public const string TurnOn = "turn_on";
    public const string TurnOff = "turn_off";
    public const string Toggle = "toggle";

    // light : args {"brightness": 0..100}
    public const string SetBrightness = "set_brightness";

    // cover
    public const string Open = "open";
    public const string Close = "close";
    public const string Stop = "stop";
    public const string SetPosition = "set_position";

    // climate : args {"mode": "comfort"} ; {"temperature": 19.5} (null : consigne du mode) ; {"delta": 0.5} ;
    // {"active": true} (planning)
    public const string SetMode = "set_mode";
    public const string SetTarget = "set_target";
    public const string AdjustTarget = "adjust_target";
    public const string SetSchedule = "set_schedule";
}
