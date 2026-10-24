using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;

namespace Krissvia.PluginHost.Services;

/// <summary>Le broker MQTT commun : un par installation (l'appartement, la maison), saisi une fois pour tous les plugins.</summary>
public sealed record SharedBroker
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 1883;
    public string? Username { get; init; }
    public string? Password { get; init; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Le broker commun, dans sa propre collection (<c>mqtt</c>, document <c>broker</c>). Rien d'autre que la saisie de la
/// pastille MQTT ne l'écrit.
/// </summary>
public sealed class SharedBrokerService(IConfigurationStore store)
{
    public const string Collection = "mqtt";
    public const string DocumentId = "broker";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private volatile SharedBroker _broker = new();

    public SharedBroker Current => _broker;

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var document in await store.ListAsync(Collection, ct))
        {
            if (document.Id != DocumentId) continue;
            try
            {
                _broker = JsonSerializer.Deserialize<SharedBroker>(document.Json, Json) ?? new SharedBroker();
            }
            catch (JsonException)
            {
                // Illisible : on repart vide, la pastille le montrera et la saisie le réécrira.
            }
        }
    }

    public async Task SaveAsync(SharedBroker broker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(broker.Host)) throw new ArgumentException("l'adresse du broker est obligatoire.");
        if (broker.Port is < 1 or > 65535) throw new ArgumentException("le port va de 1 à 65535.");
        broker = broker with
        {
            Host = broker.Host.Trim(),
            Username = string.IsNullOrWhiteSpace(broker.Username) ? null : broker.Username.Trim(),
            Password = string.IsNullOrEmpty(broker.Password) ? null : broker.Password,
        };
        await store.SaveAsync(Collection, new ConfigurationDocument(DocumentId, JsonSerializer.Serialize(broker, Json)), ct);
        _broker = broker;
        Changed?.Invoke();
    }
}

/// <summary>Le broker commun dans l'environnement d'un plugin, en fonction pure.</summary>
public static class SharedSettings
{
    /// <summary>
    /// Pour chaque section partagée <c>mqtt</c> du plugin : s'il a saisi sa propre adresse, il garde toute sa section
    /// (broker à part, rare) ; sinon le broker commun remplace adresse, port, identifiant et mot de passe — y compris le
    /// port par défaut de sa fiche, qui n'est pas un choix. Les autres réglages de la section (<c>BaseTopic</c>) restent.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> environnement, IReadOnlyList<(string Path, string Kind)> sections, SharedBroker? broker)
    {
        if (broker is not { IsConfigured: true } || sections.Count == 0) return environnement;
        var resultat = new Dictionary<string, string>(environnement, StringComparer.Ordinal);
        foreach (var (chemin, nature) in sections)
        {
            if (nature != "mqtt" || resultat.ContainsKey($"{chemin}__Host")) continue;
            foreach (var cle in new[] { "Port", "Username", "Password" }) resultat.Remove($"{chemin}__{cle}");
            resultat[$"{chemin}__Host"] = broker.Host;
            resultat[$"{chemin}__Port"] = broker.Port.ToString(CultureInfo.InvariantCulture);
            if (broker.Username is { Length: > 0 } u) resultat[$"{chemin}__Username"] = u;
            if (broker.Password is { Length: > 0 } p) resultat[$"{chemin}__Password"] = p;
        }

        return resultat;
    }
}

public enum MqttHealth
{
    /// <summary>Aucun plugin MQTT activé : rien à surveiller (gris).</summary>
    NoPlugin,

    /// <summary>Tous connectés au broker (vert).</summary>
    Ok,

    /// <summary>Un plugin démarre ou ne s'est pas encore prononcé (orange).</summary>
    Waiting,

    /// <summary>Au moins un plugin ne joint pas le broker (rouge).</summary>
    Problem,
}

/// <summary>Un plugin MQTT activé, tel que la pastille le voit.</summary>
/// <param name="Connected">Connecté au cœur : sans session, son lien n'est pas connu.</param>
public sealed record MqttPluginLink(string Id, string Name, bool Connected, LinkState? Link);

/// <param name="SharedSaved">
/// Faux : aucun broker commun enregistré. Les plugins qui ont gardé leur propre broker marchent, mais un plugin à la
/// fiche vide (un nouveau, typiquement) n'a rien à joindre — et la saisie de la pastille, pré-remplie depuis un plugin,
/// a l'air enregistrée alors qu'elle ne l'est pas. Vécu le 24/10/2026 avec MQTT Discovery.
/// </param>
public sealed record MqttOverview(MqttHealth Health, IReadOnlyList<string> Lines, bool SharedSaved = true)
{
    public const string NotSavedLine = "Broker commun pas encore enregistré : un plugin sans broker propre ne se connectera pas.";

    /// <summary>
    /// L'état de la pastille, d'après ce que disent les plugins de leur propre lien — pas une connexion du cœur, qui serait
    /// verte alors qu'un plugin n'y arrive pas (mauvais mot de passe de son côté). Rouge l'emporte sur orange, orange sur vert.
    /// </summary>
    public static MqttOverview Evaluate(IEnumerable<MqttPluginLink> plugins, bool sharedSaved = true)
    {
        var liste = plugins.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (liste.Count == 0) return new MqttOverview(MqttHealth.NoPlugin, ["Aucun plugin MQTT activé."]);

        var lignes = liste.Select(p => p switch
        {
            { Connected: false } => $"{p.Name} : pas connecté au cœur",
            { Link: null } => $"{p.Name} : en attente du broker",
            { Link.Connected: true } => $"{p.Name} : connecté",
            _ => $"{p.Name} : {p.Link.Detail ?? "broker injoignable"}",
        }).ToList();
        var etat = liste.Any(p => p is { Connected: true, Link.Connected: false }) ? MqttHealth.Problem
            : liste.Any(p => !p.Connected || p.Link is null) ? MqttHealth.Waiting
            : MqttHealth.Ok;
        // Pas enregistré : orange au moins (rien n'est en panne, mais il y a quelque chose à faire), la ligne en tête.
        if (!sharedSaved)
        {
            lignes.Insert(0, NotSavedLine);
            if (etat == MqttHealth.Ok) etat = MqttHealth.Waiting;
        }

        return new MqttOverview(etat, lignes, sharedSaved);
    }
}
