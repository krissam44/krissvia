using System.Text.Json;
using Krissvia.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.Plugin.Zigbee2Mqtt.Z2m;

/// <summary>Publication MQTT à faire pour exécuter un ordre.</summary>
internal sealed record PublicationMqtt(string Topic, string Charge);

/// <summary>
/// Le cœur du plugin, sans réseau : messages MQTT de Zigbee2MQTT → messages du contrat, et ordres du contrat →
/// publications MQTT. Tout l'état du plugin vit ici, derrière un seul verrou.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>L'identité d'un appareil est son adresse IEEE ; le friendly_name ne sert qu'à lire et écrire les topics.
/// Renommer un appareil dans Zigbee2MQTT ne change donc aucune entité (cause de l'incident « Chbre » de DomoMud).</item>
/// <item>Les messages peuvent arriver dans le désordre au démarrage (états retenus avant <c>bridge/devices</c>) :
/// une joignabilité est gardée par nom jusqu'à ce que l'appareil soit connu, un état d'appareil inconnu est perdu
/// (Zigbee2MQTT le republiera).</item>
/// <item>Les valeurs sont gardées pour la resynchronisation, sauf les événements, jamais rejoués.</item>
/// </list>
/// </remarks>
internal sealed partial class Zigbee2MqttTranslator(string baseTopic, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _verrou = new();

    private Dictionary<string, AppareilTraduit> _parNom = new(StringComparer.Ordinal);
    private Dictionary<string, (AppareilTraduit Appareil, EntityBinding Lien)> _parEntite = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceDescriptor> _declares = [];
    private string? _declarationJson;

    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _joignabiliteParNom = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lecturesDemandees = new(StringComparer.Ordinal);
    private readonly HashSet<string> _entenduesDepuisConnexion = new(StringComparer.Ordinal);
    private bool _pontEnLigne;
    private bool _mqttConnecte;

    /// <summary>Un seul abonnement : un friendly_name peut contenir des « / », que « + » ne traverserait pas.</summary>
    public string Abonnement => $"{baseTopic}/#";

    // ── MQTT → contrat ───────────────────────────────────────────────────────

    public IReadOnlyList<Message> MqttConnecte()
    {
        lock (_verrou)
        {
            _mqttConnecte = true;
            // Pendant la coupure, un relais a pu changer d'état sans qu'on l'entende : la valeur gardée ne prouve
            // plus rien, on redemandera.
            _lecturesDemandees.Clear();
            _entenduesDepuisConnexion.Clear();
        }

        return [];   // bridge/state, retenu, dira si Zigbee2MQTT tourne
    }

    /// <summary>Broker perdu : plus aucune mesure n'arrivera, tout le plugin est hors ligne.</summary>
    public IReadOnlyList<Message> MqttDeconnecte()
    {
        lock (_verrou)
        {
            _mqttConnecte = false;
            _pontEnLigne = false;
        }

        return [new Availability { Online = false }];
    }

    public IReadOnlyList<Message> TraiterMessage(string topic, string charge)
    {
        if (!topic.StartsWith(baseTopic + "/", StringComparison.Ordinal)) return [];
        var suite = topic[(baseTopic.Length + 1)..];

        lock (_verrou)
        {
            try
            {
                return suite switch
                {
                    "bridge/devices" => AppareilsRecus(charge),
                    "bridge/state" => PontRecu(charge),
                    _ when suite.StartsWith("bridge/", StringComparison.Ordinal) => [],
                    _ when suite.EndsWith("/availability", StringComparison.Ordinal) =>
                        JoignabiliteRecue(suite[..^"/availability".Length], charge),
                    _ when suite.EndsWith("/set", StringComparison.Ordinal) || suite.EndsWith("/get", StringComparison.Ordinal) => [],
                    _ => EtatRecu(suite, charge),
                };
            }
            catch (JsonException ex)
            {
                ChargeIllisible(_logger, topic, ex.Message);
                return [];
            }
        }
    }

    private IReadOnlyList<Message> AppareilsRecus(string charge)
    {
        var traduits = Z2mParser.ParseDevices(charge).Select(ExposeMapper.Traduire).OfType<AppareilTraduit>().ToList();

        _parNom = traduits.ToDictionary(a => a.Source.FriendlyName, StringComparer.Ordinal);
        _parEntite = traduits
            .SelectMany(a => a.Liens.Select(l => (a, l)))
            .ToDictionary(x => x.l.UniqueId, x => (x.a, x.l), StringComparer.Ordinal);
        foreach (var disparue in _valeurs.Keys.Where(k => !_parEntite.ContainsKey(k)).ToList()) _valeurs.Remove(disparue);

        // Zigbee2MQTT republie bridge/devices à chaque événement du réseau : on ne redéclare que ce qui a changé.
        var declares = traduits.Select(a => a.Descripteur).ToList();
        var json = JsonSerializer.Serialize(declares, ContractJson.Options);
        if (json == _declarationJson) return [];

        _declares = declares;
        _declarationJson = json;
        Declaration(_logger, declares.Count, _parEntite.Count);

        var messages = new List<Message> { new Declare { Devices = declares } };
        messages.AddRange(JoignabilitesConnues());
        return messages;
    }

    private IReadOnlyList<Message> PontRecu(string charge)
    {
        if (EnLigne(charge) is not { } enLigne) return [];

        _pontEnLigne = enLigne;
        return [new Availability { Online = enLigne }];
    }

    private IReadOnlyList<Message> JoignabiliteRecue(string nom, string charge)
    {
        if (EnLigne(charge) is not { } enLigne) return [];

        _joignabiliteParNom[nom] = enLigne;
        return _parNom.TryGetValue(nom, out var appareil)
            ? [new Availability { DeviceUniqueId = appareil.Source.IeeeAddress, Online = enLigne }]
            : [];
    }

    private IReadOnlyList<Message> EtatRecu(string nom, string charge)
    {
        if (!_parNom.TryGetValue(nom, out var appareil)) return [];

        using var document = JsonDocument.Parse(charge);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return [];

        var etats = new List<EntityState>();
        foreach (var lien in appareil.Liens)
        {
            _valeurs.TryGetValue(lien.UniqueId, out var precedente);
            if (lien.Lire(document.RootElement, _valeurs.ContainsKey(lien.UniqueId) ? precedente : null) is not { } valeur) continue;

            if (!lien.EstEvenement) _valeurs[lien.UniqueId] = valeur;
            _entenduesDepuisConnexion.Add(lien.UniqueId);
            etats.Add(new EntityState { EntityUniqueId = lien.UniqueId, Value = valeur });
        }

        return etats.Count == 0 ? [] : [new StateBatch { States = etats }];
    }

    // ── Lecture des états inconnus ───────────────────────────────────────────

    /// <summary>
    /// Les <c>/get</c> à publier : un par appareil dont un relais ou une lumière lisible n'a rien publié depuis la
    /// connexion au broker, une seule fois par connexion. Un appareil annoncé hors ligne n'est pas interrogé. C'est une
    /// lecture : elle ne change l'état d'aucun relais.
    /// </summary>
    public IReadOnlyList<PublicationMqtt> LecturesAFaire()
    {
        lock (_verrou)
        {
            var lectures = new List<PublicationMqtt>();
            foreach (var (nom, appareil) in _parNom)
            {
                if (_lecturesDemandees.Contains(appareil.Source.IeeeAddress)) continue;
                if (_joignabiliteParNom.TryGetValue(nom, out var enLigne) && !enLigne) continue;

                var proprietes = appareil.Liens
                    .Where(l => !_entenduesDepuisConnexion.Contains(l.UniqueId))
                    .SelectMany(l => l.ProprietesADemander)
                    .Distinct()
                    .ToList();
                if (proprietes.Count == 0) continue;

                var charge = new System.Text.Json.Nodes.JsonObject();
                foreach (var propriete in proprietes) charge[propriete] = "";
                lectures.Add(new PublicationMqtt($"{baseTopic}/{nom}/get", charge.ToJsonString()));
                _lecturesDemandees.Add(appareil.Source.IeeeAddress);
            }

            return lectures;
        }
    }

    // ── Resynchronisation ────────────────────────────────────────────────────

    /// <summary>Ce que le cœur doit recevoir à chaque ouverture de session.</summary>
    public IReadOnlyList<Message> Instantane()
    {
        lock (_verrou)
        {
            var messages = new List<Message>();
            if (_declarationJson is not null) messages.Add(new Declare { Devices = _declares });

            if (_valeurs.Count > 0)
                messages.Add(new StateBatch
                {
                    States = _valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value }).ToList(),
                });

            // Le plugin d'abord : un « online » d'appareil ne vaut rien si Zigbee2MQTT lui-même est arrêté.
            messages.Add(new Availability { Online = _mqttConnecte && _pontEnLigne });
            messages.AddRange(JoignabilitesConnues());
            return messages;
        }
    }

    private IEnumerable<Message> JoignabilitesConnues() =>
        _joignabiliteParNom
            .Where(kv => _parNom.ContainsKey(kv.Key))
            .Select(kv => new Availability { DeviceUniqueId = _parNom[kv.Key].Source.IeeeAddress, Online = kv.Value })
            .ToList();

    // ── Contrat → MQTT ───────────────────────────────────────────────────────

    /// <summary>La publication qui exécute l'ordre, ou le refus (entité inconnue, commande non prise en charge).</summary>
    public (PublicationMqtt? Publication, string? Erreur) Traduire(CommandRequest ordre)
    {
        lock (_verrou)
        {
            if (!_parEntite.TryGetValue(ordre.EntityUniqueId, out var cible))
                return (null, $"entité « {ordre.EntityUniqueId} » inconnue de Zigbee2MQTT");

            var traduit = cible.Lien.Commander(ordre.Command, ordre.Args);
            if (traduit.Charge is not { } charge) return (null, traduit.Erreur);

            // Le topic est recalculé à chaque ordre : il suit un renommage de l'appareil dans Zigbee2MQTT.
            return (new PublicationMqtt($"{baseTopic}/{cible.Appareil.Source.FriendlyName}/set", charge.ToJsonString()), null);
        }
    }

    /// <summary>« online » / « offline », brut (anciennes versions) ou en JSON <c>{"state": …}</c> (versions récentes).</summary>
    private static bool? EnLigne(string charge)
    {
        var texte = charge.Trim();
        if (texte.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(texte);
            texte = document.RootElement.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
        }

        return texte switch
        {
            "online" => true,
            "offline" => false,
            _ => null,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Zigbee2MQTT : {Appareils} appareil(s), {Entites} entité(s) déclarée(s) au cœur.")]
    private static partial void Declaration(ILogger logger, int appareils, int entites);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message illisible sur {Topic} : {Error}.")]
    private static partial void ChargeIllisible(ILogger logger, string topic, string error);
}
