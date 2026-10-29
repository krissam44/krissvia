using System.Text.Json;
using Krissvia.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krissvia.Plugin.MqttDiscovery.Discovery;

/// <summary>Publication MQTT à faire pour exécuter un ordre.</summary>
internal sealed record PublicationMqtt(string Topic, string Charge);

/// <summary>
/// Le cœur du plugin, sans réseau : configurations MQTT Discovery et états → messages du contrat, ordres du contrat →
/// publications. Tout l'état vit ici, derrière un seul verrou.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Le cœur prend chaque <c>declare</c> pour la liste COMPLÈTE des appareils : une entité absente y est marquée
/// disparue. Les configurations arrivant une à une (messages retenus, à la connexion), la déclaration n'est pas envoyée
/// à chaque message : elle est marquée en attente (<see cref="DeclarationEnAttente"/>) et le plugin l'envoie quand le
/// flot s'est tu.</item>
/// <item>Un état reçu avant la déclaration de son entité est gardé, et part avec elle : le cœur jetterait un état
/// d'entité inconnue.</item>
/// <item>Un état fait repasser l'entité « disponible » côté cœur. Un appareil hors ligne garde pourtant son dernier
/// état RETENU sur le broker, relu à chaque connexion : le « hors ligne » est donc réaffirmé derrière chaque état
/// d'une entité qu'on sait injoignable.</item>
/// </list>
/// </remarks>
internal sealed partial class DiscoveryTranslator(string prefixe, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _verrou = new();

    /// <summary>Les entités manuelles des réglages, fixées au démarrage (le cœur relance le plugin quand elles changent).</summary>
    private readonly Dictionary<string, EntiteHa> _manuelles = new(StringComparer.Ordinal);

    /// <summary>Par topic de configuration : la charge reçue (pour ignorer une republication identique) et l'entité.</summary>
    private readonly Dictionary<string, (string Charge, EntiteHa? Entite)> _configs = new(StringComparer.Ordinal);
    private Dictionary<string, EntiteHa> _entites = new(StringComparer.Ordinal);
    private ILookup<string, EntiteHa> _parTopicEtat = Array.Empty<EntiteHa>().ToLookup(e => "");
    private ILookup<string, EntiteHa> _parTopicDispo = Array.Empty<EntiteHa>().ToLookup(e => "");

    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Charge, long Ordre)> _dispoRecue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _dispoEnvoyee = new(StringComparer.Ordinal);
    private long _ordre;

    private HashSet<string> _declarees = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceDescriptor> _declaration = [];
    private string? _declarationJson;
    private bool _enAttente;

    private readonly HashSet<string> _abonnes = new(StringComparer.Ordinal);
    private bool _mqttConnecte;

    /// <summary>
    /// Avec des entités manuelles : elles sont déclarées dès la connexion, même si aucun appareil ne s'annonce. Une ligne
    /// refusée ou en double (même topic et même champ qu'une autre) est écartée, avec une ligne au journal.
    /// </summary>
    public DiscoveryTranslator(string prefixe, IEnumerable<EntiteManuelle> manuelles, ILogger? logger = null) : this(prefixe, logger)
    {
        foreach (var ligne in manuelles)
        {
            if (EntitesManuelles.VersConfig(ligne, out var refus) is not { } config || EntiteHa.Creer(config, out refus) is not { } entite)
                ManuelleIgnoree(_logger, ligne.Topic, refus);
            else if (!_manuelles.TryAdd(entite.UniqueId, entite))
                ManuelleEnDouble(_logger, entite.UniqueId);
        }

        if (_manuelles.Count == 0) return;
        Reindexer();
        _enAttente = true;
    }

    /// <summary>L'abonnement de départ : toutes les configurations. Les topics d'état s'y ajoutent à mesure.</summary>
    public string Abonnement => $"{prefixe}/#";

    // ── Broker ───────────────────────────────────────────────────────────────

    public IReadOnlyList<Message> MqttConnecte()
    {
        lock (_verrou)
        {
            _mqttConnecte = true;
            _abonnes.Clear();   // la nouvelle connexion n'a que l'abonnement de départ
            // « en ligne » pour le plugin rend disponibles toutes ses entités mesurées : on redit celles qui ne le sont pas.
            return [new Availability { Online = true }, .. HorsLigneConnues()];
        }
    }

    /// <summary>Broker perdu : plus aucune mesure n'arrivera.</summary>
    public IReadOnlyList<Message> MqttDeconnecte()
    {
        lock (_verrou)
        {
            _mqttConnecte = false;
            return [new Availability { Online = false }];
        }
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    public IReadOnlyList<Message> TraiterMessage(string topic, string charge)
    {
        lock (_verrou)
        {
            if (ConfigParser.Composant(prefixe, topic) is not null)
            {
                ConfigRecue(topic, charge);
                return [];
            }

            var messages = new List<Message>();
            var etats = new List<EntityState>();
            foreach (var entite in _parTopicEtat[topic])
            {
                _valeurs.TryGetValue(entite.UniqueId, out var precedente);
                if (entite.Lire(charge, _valeurs.ContainsKey(entite.UniqueId) ? precedente : null) is not { } valeur) continue;

                _valeurs[entite.UniqueId] = valeur;
                if (_declarees.Contains(entite.UniqueId)) etats.Add(new EntityState { EntityUniqueId = entite.UniqueId, Value = valeur });
            }

            if (etats.Count > 0)
            {
                messages.Add(new StateBatch { States = etats });
                messages.AddRange(etats
                    .Where(e => _dispoEnvoyee.TryGetValue(e.EntityUniqueId, out var enLigne) && !enLigne)
                    .Select(e => new Availability { EntityUniqueId = e.EntityUniqueId, Online = false }));
            }

            if (_parTopicDispo[topic].Any())
            {
                _dispoRecue[topic] = (charge, ++_ordre);
                foreach (var entite in _parTopicDispo[topic].Where(e => _declarees.Contains(e.UniqueId)))
                    if (Changement(entite) is { } m) messages.Add(m);
            }

            return messages;
        }
    }

    private void ConfigRecue(string topic, string charge)
    {
        if (_configs.TryGetValue(topic, out var connue) && connue.Charge == charge) return;   // republication

        if (string.IsNullOrWhiteSpace(charge))
        {
            // Charge vide retenue : l'appareil retire l'entité (Home Assistant la supprime).
            if (!_configs.Remove(topic)) return;
            Retiree(_logger, topic);
        }
        else
        {
            EntiteHa? entite = null;
            if (ConfigParser.Lire(prefixe, topic, charge) is not { } config) ConfigIllisible(_logger, topic);
            else if ((entite = EntiteHa.Creer(config, out var refus)) is null) ConfigIgnoree(_logger, topic, refus);
            _configs[topic] = (charge, entite);
        }

        Reindexer();
        _enAttente = true;
    }

    private void Reindexer()
    {
        // Deux configurations pour le même identifiant : la dernière lue gagne (un appareil renommé, l'ancienne retenue).
        _entites = new Dictionary<string, EntiteHa>(_manuelles, StringComparer.Ordinal);
        foreach (var (_, (_, entite)) in _configs)
            if (entite is not null) _entites[entite.UniqueId] = entite;

        _parTopicEtat = _entites.Values.Where(e => e.Config.EtatTopic is not null).ToLookup(e => e.Config.EtatTopic!, StringComparer.Ordinal);
        _parTopicDispo = _entites.Values
            .SelectMany(e => e.Config.Dispo.Select(s => (s.Topic, e)))
            .ToLookup(x => x.Topic, x => x.e, StringComparer.Ordinal);

        foreach (var disparue in _valeurs.Keys.Where(k => !_entites.ContainsKey(k)).ToList()) _valeurs.Remove(disparue);
    }

    // ── Joignabilité ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ce que disent les sources d'une entité, selon <c>availability_mode</c> : <c>latest</c> la dernière reçue,
    /// <c>all</c> toutes en ligne, <c>any</c> une seule suffit. Null tant qu'aucune n'a parlé : on ne sait rien.
    /// </summary>
    private bool? Joignable(EntiteHa entite)
    {
        var lues = new List<(bool EnLigne, long Ordre)>();
        foreach (var source in entite.Config.Dispo)
        {
            if (!_dispoRecue.TryGetValue(source.Topic, out var recue)) continue;
            var texte = source.Gabarit is { } g ? g.Evaluer(recue.Charge) : recue.Charge;
            if (texte == source.EnLigne) lues.Add((true, recue.Ordre));
            else if (texte == source.HorsLigne) lues.Add((false, recue.Ordre));
        }

        if (lues.Count == 0) return null;
        return entite.Config.ModeDispo switch
        {
            "all" => lues.Count == entite.Config.Dispo.Count ? lues.All(l => l.EnLigne) : lues.Any(l => !l.EnLigne) ? false : null,
            "any" => lues.Any(l => l.EnLigne),
            _ => lues.MaxBy(l => l.Ordre).EnLigne,
        };
    }

    private Availability? Changement(EntiteHa entite)
    {
        if (Joignable(entite) is not { } enLigne) return null;
        if (_dispoEnvoyee.TryGetValue(entite.UniqueId, out var avant) && avant == enLigne) return null;

        _dispoEnvoyee[entite.UniqueId] = enLigne;
        return new Availability { EntityUniqueId = entite.UniqueId, Online = enLigne };
    }

    private IEnumerable<Message> HorsLigneConnues() =>
        _dispoEnvoyee.Where(kv => !kv.Value && _declarees.Contains(kv.Key))
            .Select(kv => new Availability { EntityUniqueId = kv.Key, Online = false })
            .ToList();

    // ── Déclaration ──────────────────────────────────────────────────────────

    /// <summary>Vrai quand une configuration a changé depuis la dernière déclaration.</summary>
    public bool DeclarationEnAttente
    {
        get
        {
            lock (_verrou) return _enAttente;
        }
    }

    /// <summary>
    /// La déclaration, suivie des états gardés et des joignabilités connues ; vide si rien n'a changé pour le cœur
    /// (une configuration ignorée ne modifie pas la liste).
    /// </summary>
    public IReadOnlyList<Message> Declarer()
    {
        lock (_verrou)
        {
            _enAttente = false;

            var appareils = _entites.Values
                .GroupBy(e => e.Config.Appareil.Id, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var a = g.First().Config.Appareil;
                    return new DeviceDescriptor
                    {
                        UniqueId = a.Id,
                        Name = a.Nom ?? a.Id,
                        Manufacturer = a.Fabricant,
                        Model = a.Modele,
                        Entities = g.OrderBy(e => e.UniqueId, StringComparer.Ordinal).Select(e => e.Descripteur).ToList(),
                    };
                })
                .ToList();

            var json = JsonSerializer.Serialize(appareils, ContractJson.Options);
            if (json == _declarationJson) return [];

            _declaration = appareils;
            _declarationJson = json;
            _declarees = new HashSet<string>(_entites.Keys, StringComparer.Ordinal);
            foreach (var retiree in _dispoEnvoyee.Keys.Where(k => !_declarees.Contains(k)).ToList()) _dispoEnvoyee.Remove(retiree);
            Declaration(_logger, appareils.Count, _declarees.Count);

            var messages = new List<Message> { new Declare { Devices = appareils } };
            messages.AddRange(EtatsEtJoignabilites(recalculer: true));
            return messages;
        }
    }

    /// <summary>Les valeurs gardées des entités déclarées, puis leur joignabilité.</summary>
    private List<Message> EtatsEtJoignabilites(bool recalculer)
    {
        var messages = new List<Message>();
        var etats = _valeurs.Where(kv => _declarees.Contains(kv.Key))
            .Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value })
            .ToList();
        if (etats.Count > 0) messages.Add(new StateBatch { States = etats });

        foreach (var entite in _entites.Values.Where(e => _declarees.Contains(e.UniqueId)))
        {
            if (recalculer && Joignable(entite) is { } enLigne) _dispoEnvoyee[entite.UniqueId] = enLigne;
            if (_dispoEnvoyee.TryGetValue(entite.UniqueId, out var connue))
                messages.Add(new Availability { EntityUniqueId = entite.UniqueId, Online = connue });
        }

        return messages;
    }

    /// <summary>Ce que le cœur doit recevoir à chaque ouverture de session.</summary>
    public IReadOnlyList<Message> Instantane()
    {
        lock (_verrou)
        {
            var messages = new List<Message>();
            if (_declarationJson is null) return [new Availability { Online = _mqttConnecte }];

            messages.Add(new Declare { Devices = _declaration });
            var suite = EtatsEtJoignabilites(recalculer: false);
            // Le plugin d'abord, les entités ensuite : un « en ligne » général ne doit pas effacer un « hors ligne » précis.
            messages.AddRange(suite.OfType<StateBatch>());
            messages.Add(new Availability { Online = _mqttConnecte });
            messages.AddRange(suite.OfType<Availability>());
            return messages;
        }
    }

    // ── Abonnements ──────────────────────────────────────────────────────────

    /// <summary>Les topics d'état et de joignabilité pas encore suivis sur cette connexion ; marqués comme suivis.</summary>
    public IReadOnlyList<string> AbonnementsAFaire()
    {
        lock (_verrou)
        {
            if (!_mqttConnecte) return [];

            var topics = _entites.Values
                .SelectMany(e => e.Config.Dispo.Select(d => d.Topic).Prepend(e.Config.EtatTopic))
                .OfType<string>()
                .Where(t => !t.StartsWith(prefixe + "/", StringComparison.Ordinal))   // déjà couverts par l'abonnement de départ
                .Distinct(StringComparer.Ordinal)
                .Where(_abonnes.Add)
                .ToList();
            return topics;
        }
    }

    /// <summary>Abonnement refusé ou broker perdu entre-temps : à refaire au prochain tour.</summary>
    public void AbonnementEchoue(string topic)
    {
        lock (_verrou) _abonnes.Remove(topic);
    }

    // ── Contrat → MQTT ───────────────────────────────────────────────────────

    public (PublicationMqtt? Publication, string? Erreur) Traduire(CommandRequest ordre)
    {
        lock (_verrou)
        {
            if (!_declarees.Contains(ordre.EntityUniqueId) || !_entites.TryGetValue(ordre.EntityUniqueId, out var entite))
                return (null, $"entité « {ordre.EntityUniqueId} » inconnue (configuration retirée du broker ?)");

            var connue = _valeurs.TryGetValue(entite.UniqueId, out var v) ? v : (JsonElement?)null;
            var traduit = entite.Commander(ordre.Command, ordre.Args, connue);
            return traduit.Charge is { } charge ? (new PublicationMqtt(entite.Config.CommandeTopic!, charge), null) : (null, traduit.Erreur);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT Discovery : {Appareils} appareil(s), {Entites} entité(s) déclarée(s) au cœur.")]
    private static partial void Declaration(ILogger logger, int appareils, int entites);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration illisible sur {Topic} : ni JSON ni objet.")]
    private static partial void ConfigIllisible(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration {Topic} ignorée : {Motif}.")]
    private static partial void ConfigIgnoree(ILogger logger, string topic, string? motif);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entité manuelle sur {Topic} ignorée : {Motif}.")]
    private static partial void ManuelleIgnoree(ILogger logger, string? topic, string? motif);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entité manuelle « {UniqueId} » en double (même topic, même champ) : seule la première est gardée.")]
    private static partial void ManuelleEnDouble(ILogger logger, string uniqueId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Entité retirée par l'appareil : {Topic}.")]
    private static partial void Retiree(ILogger logger, string topic);
}
