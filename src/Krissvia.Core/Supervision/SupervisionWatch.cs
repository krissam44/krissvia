using Krissvia.Core.Notifications;

namespace Krissvia.Core.Supervision;

/// <summary>Un point surveillé, tel que la page Supervision le montre.</summary>
/// <param name="Alerting">En panne depuis plus que son délai de grâce : l'alerte est (ou a été tentée).</param>
/// <param name="Since">Début de la panne en cours.</param>
public sealed record SupervisionStatus(SupervisionItem Item, bool Alerting, DateTimeOffset? Since);

/// <param name="SensorsInGrace">Les premières minutes après un démarrage, les capteurs ne sont pas encore jugés.</param>
public sealed record SupervisionReport(DateTimeOffset At, IReadOnlyList<SupervisionStatus> Items, bool SensorsInGrace);

/// <summary>
/// La supervision : applique les délais de grâce aux pannes de <see cref="SupervisionRules"/>, lève et rétablit les
/// alertes (<see cref="AlertService"/>, qui dédoublonne et met en quarantaine), et garde le dernier constat pour la page.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Répit au démarrage</b> (<see cref="StartupGrace"/>) : les capteurs n'ont pas encore republié, tout le parc
/// serait « muet » à chaque redémarrage (DomoMud).</item>
/// <item><b>Début d'une panne</b> : celui que dit la donnée (dernière mesure d'un capteur), sinon le premier constat.
/// Une box republie « coupée » à chaque relevé : l'instant de la mesure ne dit pas depuis quand.</item>
/// <item><b>Coupure Internet</b> : pendant la coupure, un serveur ntfy public est injoignable — l'alerte échoue, réessaie,
/// et partirait au retour suivie aussitôt de « rétablie ». Une coupure jamais signalée donne donc, au retour, UN message :
/// « coupée de 10:02 à 10:40 ».</item>
/// <item>Un point qui n'est plus surveillé (plugin désactivé, capteur supprimé, famille éteinte) : son alerte est oubliée
/// sans message — ce n'est pas un retour à la normale.</item>
/// </list>
/// </remarks>
public sealed class SupervisionWatch(AlertService alerts, NotificationService notifier, TimeProvider clock)
{
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);

    private static readonly string[] Prefixes = ["plugin:", "internet:", "presence", "disque", "capteur:", "pile:", "probleme:", "reference:"];

    private readonly DateTimeOffset _demarrage = clock.GetUtcNow();
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _depuis = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _coupuresNonSignalees = new(StringComparer.Ordinal);

    public SupervisionReport? Last { get; private set; }

    /// <summary>Un nouveau constat est prêt. Levé hors verrou.</summary>
    public event Action? Changed;

    public async Task<SupervisionReport> CheckAsync(SupervisionInput entree, CancellationToken ct = default)
    {
        SupervisionReport rapport;
        await _verrou.WaitAsync(ct);
        try
        {
            var reglages = await alerts.GetSettingsAsync(ct);
            var repit = entree.Now - _demarrage < StartupGrace;
            var items = SupervisionRules.Evaluate(entree, reglages, repit);
            var statuts = new List<SupervisionStatus>(items.Count);

            foreach (var it in items)
            {
                if (it.Problem)
                {
                    var depuis = it.Since ?? (_depuis.TryGetValue(it.Key, out var d) ? d : _depuis[it.Key] = entree.Now);
                    var echu = entree.Now - depuis >= TimeSpan.FromMinutes(it.GraceMinutes);
                    if (echu)
                    {
                        var issue = await alerts.RaiseAsync(it.Key, Alerte(it, depuis, entree.Zone), Quarantaine(it, reglages), ct);
                        if (issue == RaiseOutcome.NotDelivered && it.Category == SupervisionCategory.Internet) _coupuresNonSignalees.TryAdd(it.Key, depuis);
                    }

                    statuts.Add(new SupervisionStatus(it, echu, depuis));
                }
                else
                {
                    _depuis.Remove(it.Key, out var debut);
                    var signale = await alerts.ResolveAsync(it.Key, Retour(it, debut, entree), ct);
                    if (_coupuresNonSignalees.Remove(it.Key, out var coupure) && !signale)
                        await notifier.SendAsync(Bilan(it, coupure, entree), ct: ct);
                    statuts.Add(new SupervisionStatus(it, false, null));
                }
            }

            // Ce qui n'est plus surveillé : oublié sans message.
            var vus = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var a in alerts.Active.Where(a => !vus.Contains(a.Key) && Prefixes.Any(p => a.Key.StartsWith(p, StringComparison.Ordinal))))
            {
                // Un capteur en répit au démarrage n'est pas « plus surveillé » : son alerte attend la fin du répit.
                if (repit && a.Key.StartsWith("capteur:", StringComparison.Ordinal)) continue;
                alerts.Dismiss(a.Key);
            }

            foreach (var cle in _depuis.Keys.Where(k => !vus.Contains(k)).ToList()) _depuis.Remove(cle);

            rapport = new SupervisionReport(entree.Now, statuts, repit && reglages.Sensors.Enabled);
            Last = rapport;
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return rapport;
    }

    private static TimeSpan? Quarantaine(SupervisionItem it, AlertSettings r) => it.Category switch
    {
        // Un capteur à la limite du seuil basculerait sans fin : après un retour, un cycle complet (DomoMud).
        SupervisionCategory.Sensor => TimeSpan.FromMinutes(r.Sensors.SilentMinutes),
        _ => null,
    };

    private static Notification Alerte(SupervisionItem it, DateTimeOffset depuis, TimeZoneInfo? zone)
    {
        var heure = SupervisionRules.Heure(depuis, zone);
        return it.Category switch
        {
            SupervisionCategory.Internet => new($"Connexion Internet coupée", $"{it.Name} : coupée depuis {heure}.", 4, ["globe_with_meridians"]),
            SupervisionCategory.Plugin => new($"Service arrêté : {it.Name}",
                $"{it.Name} est activé mais ne se connecte plus au cœur depuis {heure} : ses entités sont indisponibles.", 4, ["warning"]),
            SupervisionCategory.Presence => new("Détection de présence hors service", $"Depuis {heure} : {it.Detail}.", 4, ["warning"]),
            SupervisionCategory.Disk => new("Espace disque faible", $"{Majuscule(it.Detail)}.", 4, ["floppy_disk"]),
            SupervisionCategory.Sensor => new($"Capteur muet : {it.Name}", $"{it.Name} : {it.Detail}.", 4, ["warning"]),
            SupervisionCategory.Problem => new($"Problème : {it.Name}", $"{it.Name} : {it.Detail}, depuis {heure}.", 4, ["warning"]),
            SupervisionCategory.Reference => new($"Référence cassée : {it.Name}",
                $"{it.Name} vise une entité inutilisable depuis {heure} — {it.Detail}. À corriger dans sa fiche.", 4, ["link"]),
            _ => new($"Pile faible : {it.Name}", $"{it.Name} : {it.Detail}.", 3, ["battery"]),
        };
    }

    private static Notification Retour(SupervisionItem it, DateTimeOffset debut, SupervisionInput e) => it.Category switch
    {
        SupervisionCategory.Internet => new("Connexion Internet rétablie", $"{it.Name} : rétablie{Duree(debut, e.Now)}.", 2, ["white_check_mark"]),
        SupervisionCategory.Plugin => new($"Service rétabli : {it.Name}", $"{it.Name} est de nouveau connecté{Duree(debut, e.Now)}.", 2, ["white_check_mark"]),
        SupervisionCategory.Presence => new("Détection de présence rétablie", "Les appareils suivis répondent de nouveau.", 2, ["white_check_mark"]),
        SupervisionCategory.Disk => new("Espace disque correct", $"{Majuscule(it.Detail)}.", 1, ["white_check_mark"]),
        SupervisionCategory.Sensor => new($"Capteur rétabli : {it.Name}", $"{it.Name} émet de nouveau.", 2, ["white_check_mark"]),
        SupervisionCategory.Problem => new($"Problème résolu : {it.Name}", $"{it.Name} : plus de problème signalé.", 2, ["white_check_mark"]),
        SupervisionCategory.Reference => new($"Référence réparée : {it.Name}", $"{it.Name} : {it.Detail}.", 2, ["white_check_mark"]),
        _ => new($"Pile remplacée : {it.Name}", $"{it.Name} : {it.Detail}.", 1, ["battery"]),
    };

    private static Notification Bilan(SupervisionItem it, DateTimeOffset coupure, SupervisionInput e) =>
        new("Coupure Internet", $"{it.Name} : connexion coupée de {SupervisionRules.Heure(coupure, e.Zone)} à {SupervisionRules.Heure(e.Now, e.Zone)}"
            + $"{Duree(coupure, e.Now)}. L'alerte n'avait pas pu partir pendant la coupure.", 3, ["globe_with_meridians"]);

    /// <summary>« (coupure de 38 min) », ou rien quand le début est inconnu.</summary>
    private static string Duree(DateTimeOffset debut, DateTimeOffset fin) =>
        debut == default ? "" : $" (après {Math.Max(1, (int)Math.Round((fin - debut).TotalMinutes))} min)";

    private static string Majuscule(string texte) => texte.Length == 0 ? texte : char.ToUpper(texte[0], System.Globalization.CultureInfo.CurrentCulture) + texte[1..];
}
