using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Supervision;

/// <summary>Un changement d'IP publique : l'entité, l'ancienne et la nouvelle adresse.</summary>
public sealed record PublicIpChange(string EntityId, string Name, string Previous, string Current);

/// <summary>
/// Prévient sur le téléphone quand l'IP publique change, avec la nouvelle adresse (accès à distance, règle de pare-feu,
/// DNS à mettre à jour). Valable pour toute box qui publie une entité de classe <c>public_ip</c> : la Livebox
/// aujourd'hui, une autre demain.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>C'est un évènement, pas un incident : pas d'alerte « active » ni de retour à la normale, un message par
/// changement.</item>
/// <item>La dernière adresse connue est gardée par entité. Une box qui ne publie plus d'IP le temps de se reconnecter
/// (valeur vide) ne fait pas oublier l'ancienne : A → (rien) → B donne bien « B, avant A ».</item>
/// <item>Au démarrage, l'adresse de référence est la dernière enregistrée (l'état restauré, même indisponible) : une IP
/// qui a changé pendant que le serveur était arrêté est signalée.</item>
/// <item>Un changement d'IP accompagne souvent une reconnexion : le serveur ntfy peut être injoignable quelques
/// minutes. Un envoi échoué est retenté (1, 5, 15 puis 60 min) ; un nouveau changement entre-temps remplace le message
/// en gardant l'adresse d'origine. Notifications désactivées ou non réglées : rien n'est retenté.</item>
/// </list>
/// </remarks>
public sealed class PublicIpWatch(AlertService alerts, NotificationService notifier, IEntityRegistry registry, TimeProvider clock)
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    private sealed record EnAttente(PublicIpChange Changement, int Essai, DateTimeOffset Prochain);

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly Dictionary<string, string> _connues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnAttente> _enAttente = new(StringComparer.Ordinal);

    /// <summary>Un changement vient d'être constaté (envoyé ou mis en attente).</summary>
    public event Action<PublicIpChange, SendOutcome>? Changed;

    /// <summary>Un changement d'état du bus : ne fait quelque chose que pour une entité <c>public_ip</c>.</summary>
    public async Task<PublicIpChange?> OnStateChangedAsync(EntityStateChanged e, CancellationToken ct = default)
    {
        if (registry.Loaded.FirstOrDefault(x => x.EntityId == e.EntityId) is not { Kind: EntityKind.Sensor, DeviceClass: DeviceClasses.PublicIp, Enabled: true } entite)
            return null;
        if (!e.Current.Available || Adresse(e.Current) is not { } nouvelle) return null;

        PublicIpChange changement;
        SendOutcome issue;
        await _verrou.WaitAsync(ct);
        try
        {
            var ancienne = _enAttente.TryGetValue(e.EntityId, out var attente) ? attente.Changement.Previous
                : _connues.GetValueOrDefault(e.EntityId) ?? Adresse(e.Previous);
            _connues[e.EntityId] = nouvelle;
            if (ancienne is null || string.Equals(ancienne, nouvelle, StringComparison.OrdinalIgnoreCase))
            {
                // Revenue à l'adresse d'origine avant que le message parte : plus rien à dire.
                _enAttente.Remove(e.EntityId);
                return null;
            }

            if (!(await alerts.GetSettingsAsync(ct)).PublicIp.Enabled) return null;

            changement = new PublicIpChange(e.EntityId, entite.DisplayName, ancienne, nouvelle);
            issue = await EnvoyerAsync(changement, 0, ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke(changement, issue);
        return changement;
    }

    /// <summary>Retente les envois échoués dont l'heure est venue (appelé toutes les minutes).</summary>
    public async Task RetryPendingAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var maintenant = clock.GetUtcNow();
            foreach (var attente in _enAttente.Values.Where(a => a.Prochain <= maintenant).ToList())
                await EnvoyerAsync(attente.Changement, attente.Essai + 1, ct);
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Sous verrou. Échec : mis en attente pour l'essai suivant, abandonné après le dernier délai.</summary>
    private async Task<SendOutcome> EnvoyerAsync(PublicIpChange c, int essai, CancellationToken ct)
    {
        var resultat = await notifier.SendAsync(Message(c), ct: ct);
        if (resultat.Outcome == SendOutcome.Failed && essai < RetryDelays.Length)
            _enAttente[c.EntityId] = new EnAttente(c, essai, clock.GetUtcNow() + RetryDelays[essai]);
        else
            _enAttente.Remove(c.EntityId);
        return resultat.Outcome;
    }

    public static Notification Message(PublicIpChange c) =>
        new("IP publique changée", $"{c.Name} : nouvelle adresse {c.Current} (avant : {c.Previous}).", 3, ["globe_with_meridians"]);

    /// <summary>L'adresse d'un état, ou null (absent, vide, pas une chaîne). La disponibilité n'est pas regardée ici.</summary>
    public static string? Adresse(EntityStateSnapshot? s) =>
        s is not null && s.Value.ValueKind == JsonValueKind.String && s.Value.GetString()?.Trim() is { Length: > 0 } ip ? ip : null;
}
