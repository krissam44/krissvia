namespace Krissvia.Climate.LoadShedding;

/// <summary>
/// Mesure de puissance fournie au délesteur. L'unité (VA ou W) est celle des seuils ; la conversion et
/// la qualité de la mesure (apparente, active, estimée) sont du ressort de l'adaptateur de source.
/// </summary>
/// <param name="Value">Puissance instantanée.</param>
/// <param name="MeasuredAt">Instant de la mesure côté plugin (UTC), pas celui de la réception.</param>
public readonly record struct PowerReading(double Value, DateTimeOffset MeasuredAt);

/// <summary>Réglages du délesteur. Les seuils sont en puissance absolue (voir <see cref="FromSubscription"/>).</summary>
public sealed record LoadSheddingSettings
{
    public static LoadSheddingSettings Disabled { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>Au-dessus : on déleste.</summary>
    public double AlertThreshold { get; init; }

    /// <summary>En dessous : on peut rétablir. Doit être inférieur au seuil d'alerte (hystérésis).</summary>
    public double RestoreThreshold { get; init; }

    /// <summary>Au-delà de cet âge, une mesure est considérée absente (jamais lue comme zéro).</summary>
    public TimeSpan MaxReadingAge { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Après une action (coupure ou rétablissement), aucune nouvelle décision tant qu'une mesure
    /// postérieure à ce délai n'est pas arrivée : la mesure précédente ne reflète pas encore l'action.
    /// À régler d'après la latence réelle de la chaîne (période de publication + temps de réaction).
    /// </summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Attente de l'état qui confirme une coupure (ARCHITECTURE.md §7.3). Passé ce délai, la coupure n'est pas
    /// acquise : elle ne compte pas comme économie, l'équipement suivant est délesté, et elle est retentée.
    /// </summary>
    public TimeSpan CutVerificationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Seuils déduits de l'abonnement : 6 kVA à 80 % / 70 % donne 4 800 / 4 200 VA.</summary>
    public static LoadSheddingSettings FromSubscription(double kva, double alertPercent, double restorePercent, bool enabled = true)
    {
        if (kva <= 0) throw new ArgumentOutOfRangeException(nameof(kva), kva, "L'abonnement doit être positif.");
        if (restorePercent >= alertPercent)
            throw new ArgumentOutOfRangeException(nameof(restorePercent), restorePercent,
                "Le seuil de restauration doit être inférieur au seuil d'alerte.");

        return new LoadSheddingSettings
        {
            Enabled = enabled,
            AlertThreshold = kva * 1000.0 * alertPercent / 100.0,
            RestoreThreshold = kva * 1000.0 * restorePercent / 100.0,
        };
    }
}

/// <summary>Un équipement délestable.</summary>
/// <param name="Id">Identifiant stable.</param>
/// <param name="Name">Nom affiché.</param>
/// <param name="Priority">1 = délesté en premier, 10 = délesté en dernier (rétabli en premier).</param>
/// <param name="NominalPower">Puissance estimée économisée par la coupure.</param>
/// <param name="RestoreDelay">Durée minimale de coupure avant tout rétablissement.</param>
/// <param name="SwitchEntityIds">Entités à couper.</param>
/// <param name="ThermostatId">
/// Renseigné : la coupure passe par une inhibition du thermostat (consigne et mode intacts).
/// Absent : coupure directe des switches.
/// </param>
public sealed record SheddableDevice(
    string Id,
    string Name,
    int Priority,
    double NominalPower,
    TimeSpan RestoreDelay,
    IReadOnlyList<string> SwitchEntityIds,
    string? ThermostatId = null);

public enum ShedTransition
{
    None,

    /// <summary>On passe de « rien de délesté » à « au moins un équipement délesté ».</summary>
    Started,

    /// <summary>Tout est rétabli.</summary>
    Ended,
}

/// <summary>Résultat d'un tick du délesteur.</summary>
/// <param name="Shed">Équipements délestés pendant ce tick.</param>
/// <param name="Restored">Équipements rétablis pendant ce tick.</param>
/// <param name="Unconfirmed">Équipements délestés dont la coupure n'est pas encore confirmée.</param>
/// <param name="Transition">Bascule à notifier (une seule fois par épisode).</param>
/// <param name="SourceUnavailable">Mesure absente ou périmée : le délesteur n'a rien décidé.</param>
public sealed record LoadSheddingTick(
    IReadOnlyList<string> Shed,
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Unconfirmed,
    ShedTransition Transition,
    bool SourceUnavailable);
