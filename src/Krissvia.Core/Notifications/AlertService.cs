using System.Text.Json;
using System.Text.Json.Serialization;
using Krissvia.Core.Configuration;

namespace Krissvia.Core.Notifications;

/// <summary>Alerte gel : minimum prévu sur l'horizon, sous le seuil, pour chaque lieu du plugin Météo.</summary>
public sealed record FrostAlertSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Seuil, en °C : à ce minimum prévu ou en dessous, l'alerte part.</summary>
    public double ThresholdCelsius { get; init; } = 1.0;

    /// <summary>Horizon de la prévision regardée, en heures (24 dans DomoMud).</summary>
    public int HorizonHours { get; init; } = 24;
}

/// <summary>Les familles d'alertes et leurs réglages. Une seule pour l'instant ; les suivantes s'y ajouteront.</summary>
public sealed record AlertSettings
{
    public FrostAlertSettings Frost { get; init; } = new();
}

/// <summary>Un incident signalé et pas encore rétabli.</summary>
public sealed record ActiveAlert(string Key, string Title, string Message, DateTimeOffset Since, SendOutcome Delivery);

public enum RaiseOutcome { Raised, AlreadyActive, Quarantined, NotDelivered }

/// <summary>
/// Aiguillage des alertes, repris de DomoMud : ce qu'un simple envoi ne fait pas.
/// <list type="bullet">
/// <item>Une alerte par incident (clé) : tant qu'il dure, on ne renotifie pas.</item>
/// <item>Le retour à la normale ne se dit que si l'alerte était active : pas de « tout va bien » surprise.</item>
/// <item>Quarantaine : un incident rétabli ne peut pas être re-signalé avant un délai, sinon une valeur à la limite
/// du seuil noie le téléphone (quatre allers-retours en 90 min sur une sonde, dans DomoMud).</item>
/// </list>
/// </summary>
/// <remarks>
/// Écart avec DomoMud : un envoi ÉCHOUÉ (serveur injoignable) ne marque pas l'incident comme signalé ; la prochaine
/// analyse réessaie. Notifications désactivées ou non réglées, en revanche, l'incident est noté — la page le montre —
/// sans être renvoyé à chaque analyse. L'état est en mémoire : après un redémarrage, une situation toujours dégradée
/// est signalée une fois, ce qui est voulu.
/// </remarks>
public sealed class AlertService(NotificationService notifier, IConfigurationStore store, TimeProvider clock)
{
    public const string Collection = "alertes";
    public const string DocumentId = "reglages";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _verrou = new();
    private readonly Dictionary<string, ActiveAlert> _actives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retours = new(StringComparer.Ordinal);
    private AlertSettings? _reglages;

    /// <summary>Une alerte levée ou rétablie, des réglages changés : la page se met à jour.</summary>
    public event Action? Changed;

    public IReadOnlyList<ActiveAlert> Active
    {
        get
        {
            lock (_verrou) return [.. _actives.Values.OrderBy(a => a.Since)];
        }
    }

    public bool IsActive(string key)
    {
        lock (_verrou) return _actives.ContainsKey(key);
    }

    /// <summary>Signale un incident ; n'envoie que s'il n'était pas déjà signalé (ni en quarantaine).</summary>
    public async Task<RaiseOutcome> RaiseAsync(string key, Notification n, TimeSpan? quarantaine = null, CancellationToken ct = default)
    {
        var maintenant = clock.GetUtcNow();
        lock (_verrou)
        {
            if (_actives.ContainsKey(key)) return RaiseOutcome.AlreadyActive;
            if (quarantaine is { } q && _retours.TryGetValue(key, out var retour) && maintenant - retour < q)
                return RaiseOutcome.Quarantined;
        }

        var envoi = await notifier.SendAsync(n, ct: ct);
        if (envoi.Outcome == SendOutcome.Failed) return RaiseOutcome.NotDelivered;

        lock (_verrou) _actives[key] = new ActiveAlert(key, n.Title, n.Message, maintenant, envoi.Outcome);
        Changed?.Invoke();
        return RaiseOutcome.Raised;
    }

    /// <summary>Signale la fin d'un incident ; n'envoie que s'il était signalé. Vrai si un message de fin est parti.</summary>
    public async Task<bool> ResolveAsync(string key, Notification n, CancellationToken ct = default)
    {
        lock (_verrou)
        {
            if (!_actives.Remove(key)) return false;
            _retours[key] = clock.GetUtcNow();   // début de la quarantaine
        }

        Changed?.Invoke();
        return (await notifier.SendAsync(n, ct: ct)).Sent;
    }

    public async Task<AlertSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        if (_reglages is { } r) return r;
        var document = (await store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == DocumentId);
        try
        {
            _reglages = document is null ? new AlertSettings() : JsonSerializer.Deserialize<AlertSettings>(document.Json, Json) ?? new();
        }
        catch (JsonException)
        {
            _reglages = new AlertSettings();
        }

        return _reglages;
    }

    public async Task SaveSettingsAsync(AlertSettings reglages, CancellationToken ct = default)
    {
        if (reglages.Frost.HorizonHours is < 1 or > 48)
            throw new NotificationValidationException("horizon de l'alerte gel : de 1 à 48 h (la prévision publiée)");
        if (reglages.Frost.ThresholdCelsius is < -30 or > 10)
            throw new NotificationValidationException("seuil de l'alerte gel : de −30 à 10 °C");

        await store.SaveAsync(Collection, new ConfigurationDocument(DocumentId, JsonSerializer.Serialize(reglages, Json)), ct);
        _reglages = reglages;
        Changed?.Invoke();
    }
}
