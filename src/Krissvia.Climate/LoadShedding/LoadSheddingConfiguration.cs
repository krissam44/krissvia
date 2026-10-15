namespace Krissvia.Climate.LoadShedding;

/// <summary>D'où vient la puissance mesurée.</summary>
public enum PowerSourceKind
{
    /// <summary>Une entité de puissance : Linky (VA) ou pince (W).</summary>
    Entity,

    /// <summary>Somme des équipements suivis : mesure réelle s'ils en publient une, sinon puissance nominale.</summary>
    EquipmentSum,
}

/// <summary>Un équipement entrant dans la somme (source <see cref="PowerSourceKind.EquipmentSum"/>).</summary>
public sealed record MeteredEquipmentConfiguration
{
    public required string SwitchEntityId { get; init; }
    public string? PowerEntityId { get; init; }
    public double NominalPower { get; init; }
}

/// <summary>Une source de puissance du délesteur.</summary>
public sealed record PowerSourceConfiguration
{
    /// <summary>Nom affiché (« Linky », « Pince », « Somme »…).</summary>
    public string Name { get; init; } = "";

    public PowerSourceKind Kind { get; init; } = PowerSourceKind.Entity;
    public string? PowerEntityId { get; init; }

    /// <summary>Apparente (VA, Linky) ou active (W, pince) : une mesure en W est majorée avant comparaison.</summary>
    public PowerQuality PowerQuality { get; init; } = PowerQuality.Apparent;

    /// <summary>Majoration d'une mesure en W pour la comparer à des seuils en VA. ≥ 1.</summary>
    public double ActiveToApparentFactor { get; init; } = 1.10;

    public IReadOnlyList<MeteredEquipmentConfiguration> MeteredEquipment { get; init; } = [];
}

/// <summary>Un équipement délestable, tel qu'enregistré.</summary>
public sealed record SheddableDeviceConfiguration
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>1 = délesté en premier, 10 = en dernier.</summary>
    public int Priority { get; init; } = 5;

    /// <summary>Puissance économisée par la coupure, dans l'unité des seuils.</summary>
    public double NominalPower { get; init; }

    public int RestoreDelaySeconds { get; init; } = 120;
    public IReadOnlyList<string> SwitchEntityIds { get; init; } = [];

    /// <summary>Renseigné : la coupure passe par une inhibition du thermostat, consigne et mode intacts (§8).</summary>
    public string? ThermostatId { get; init; }

    public SheddableDevice ToDevice() =>
        new(Id, Name, Priority, NominalPower, TimeSpan.FromSeconds(RestoreDelaySeconds), SwitchEntityIds, ThermostatId);
}

/// <summary>Configuration complète du délestage : un seul document par installation.</summary>
public sealed record LoadSheddingConfiguration
{
    public bool Enabled { get; init; }

    /// <summary>Puissance souscrite (6 pour la maison, 12 pour l'appartement…).</summary>
    public double SubscriptionKva { get; init; } = 6;

    public double AlertPercent { get; init; } = 80;
    public double RestorePercent { get; init; } = 70;

    /// <summary>
    /// Sources par ordre de préférence (Linky, puis pince, puis somme) : chaque tick prend la première dont la
    /// mesure est fraîche. Une seule source qui se tait laisserait le délesteur sans décision — la protection
    /// disparaîtrait en silence, au moment où on en a besoin.
    /// </summary>
    public IReadOnlyList<PowerSourceConfiguration> Sources { get; init; } = [];

    public int MaxReadingAgeSeconds { get; init; } = 10;
    public int SettleDelaySeconds { get; init; } = 5;
    public int CutVerificationTimeoutSeconds { get; init; } = 5;

    public IReadOnlyList<SheddableDeviceConfiguration> Devices { get; init; } = [];

    public LoadSheddingSettings ToSettings() =>
        LoadSheddingSettings.FromSubscription(SubscriptionKva, AlertPercent, RestorePercent, Enabled) with
        {
            MaxReadingAge = TimeSpan.FromSeconds(MaxReadingAgeSeconds),
            SettleDelay = TimeSpan.FromSeconds(SettleDelaySeconds),
            CutVerificationTimeout = TimeSpan.FromSeconds(CutVerificationTimeoutSeconds),
        };
}

/// <summary>État du délestage à montrer : ce qui a été lu, décidé, et ce qui n'est pas encore confirmé.</summary>
public sealed record LoadSheddingStatus(
    bool Enabled,
    double? Power,
    DateTimeOffset? MeasuredAt,
    string? ActiveSource,
    bool SourceUnavailable,
    double AlertThreshold,
    double RestoreThreshold,
    IReadOnlyList<string> ShedDevices,
    IReadOnlyList<string> UnconfirmedDevices,
    DateTimeOffset? LastTickAt);
