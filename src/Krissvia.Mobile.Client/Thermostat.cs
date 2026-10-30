using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Krissvia.Mobile.Client;

/// <summary>Une entité telle que <c>/api/entities</c> et <c>/api/live</c> la donnent (API.md, « Entités et flux en direct »).</summary>
internal sealed record EntiteDto(
    string EntityId,
    string? Name,
    string? Kind,
    string? Room,
    bool Enabled,
    bool Missing,
    JsonElement? Extra,
    EtatDto? State);

/// <summary>Dernier état d'une entité. Absent : jamais publié, ce qui n'est pas zéro.</summary>
internal sealed record EtatDto(JsonElement Value, bool Available, DateTimeOffset MeasuredAt);

/// <summary>Un mode proposé par le thermostat, avec le libellé du serveur, à afficher tel quel.</summary>
public sealed record ModeThermostat(string Id, string Label);

/// <summary>Prochaine bascule du planning dans les 24 h, en heure locale du serveur.</summary>
public sealed record ProchaineBascule(DateTime At, string Mode, string Label)
{
    /// <summary>« Confort à 18:00 », « Éco demain à 06:00 » : le jour n'est dit que s'il change.</summary>
    public string Texte(DateTime maintenant) =>
        At.Date == maintenant.Date ? $"{Label} à {At:HH:mm}"
        : At.Date == maintenant.Date.AddDays(1) ? $"{Label} demain à {At:HH:mm}"
        : $"{Label} le {At.ToString("dd/MM 'à' HH:mm", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Un thermostat vu de l'appli : une entité <c>climate</c> du cœur, lue telle que le serveur la publie. Rien n'est
/// recalculé ici — ni le statut, ni la consigne régulée, ni la prochaine bascule — : deux endroits qui calculent le
/// même libellé finissent par se contredire (le statut de l'appli de DomoMud).
/// </summary>
public sealed record Thermostat
{
    public required string EntityId { get; init; }
    public required string Nom { get; init; }
    public string? Piece { get; init; }
    public int? Radiateurs { get; init; }

    /// <summary>Du plus chaud au plus froid : l'ordre du serveur.</summary>
    public IReadOnlyList<ModeThermostat> Modes { get; init; } = [];
    public double PasConsigne { get; init; } = 0.5;

    /// <summary>Faux tant que le moteur n'a rien publié : la carte le dit au lieu d'afficher des zéros.</summary>
    public bool APublie { get; init; }

    /// <summary>Faux si l'entité est indisponible : les valeurs sont les dernières connues.</summary>
    public bool Disponible { get; init; }

    public double? Temperature { get; init; }
    public double? Humidite { get; init; }
    public double? Exterieure { get; init; }

    /// <summary>La consigne réellement suivie (préchauffage, absence compris), pas celle du mode.</summary>
    public double? Consigne { get; init; }
    public bool ConsigneAjustee { get; init; }
    public string? Mode { get; init; }
    public bool PlanningActif { get; init; }
    public bool Chauffe { get; init; }

    /// <summary>Part de chauffe du cycle TPI, 0 à 1.</summary>
    public double? Puissance { get; init; }
    public bool FenetreOuverte { get; init; }
    public bool Absence { get; init; }

    /// <summary>Code du statut (<c>heating</c>, <c>inhibited</c>…) : sert aux couleurs, jamais au texte.</summary>
    public string? Statut { get; init; }

    /// <summary>Libellé du statut, calculé par le cœur : à afficher tel quel.</summary>
    public string? LibelleStatut { get; init; }
    public ProchaineBascule? ProchaineBascule { get; init; }

    /// <summary>Le libellé du mode suivi, celui du serveur (« Confort −1 »).</summary>
    public string? LibelleMode => Mode is null ? null : Modes.FirstOrDefault(m => m.Id == Mode)?.Label ?? Mode;

    /// <summary>Le mode à montrer sélectionné : aucun quand la consigne est ajustée à la main, comme sur le web.</summary>
    public string? ModeActif => ConsigneAjustee ? null : Mode;

    /// <summary>En mode <c>off</c>, le serveur refuse tout ajustement : les boutons ± disparaissent.</summary>
    public bool ConsigneReglable => APublie && Mode != "off";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    internal static Thermostat Depuis(EntiteDto entite, EtatDto? etat)
    {
        var extra = entite.Extra is { ValueKind: JsonValueKind.Object } e ? e.Deserialize<ExtraClimat>(Json) : null;
        var valeur = etat?.Value is { ValueKind: JsonValueKind.Object } v ? v.Deserialize<ValeurClimat>(Json) : null;

        return new Thermostat
        {
            EntityId = entite.EntityId,
            Nom = entite.Name is { Length: > 0 } nom ? nom : entite.EntityId,
            Piece = entite.Room,
            Radiateurs = extra?.Radiators,
            Modes = extra?.Modes?.Where(m => m.Id is { Length: > 0 }).Select(m => new ModeThermostat(m.Id!, m.Label ?? m.Id!)).ToList() ?? [],
            PasConsigne = extra?.TargetStep is > 0 and var pas ? pas : 0.5,
            APublie = valeur is not null,
            Disponible = etat?.Available ?? false,
            Temperature = valeur?.Temperature,
            Humidite = valeur?.Humidity,
            Exterieure = valeur?.OutdoorTemperature,
            Consigne = valeur?.Target,
            ConsigneAjustee = valeur?.TargetAdjusted ?? false,
            Mode = valeur?.Mode,
            PlanningActif = valeur?.ScheduleActive ?? false,
            Chauffe = valeur?.Heating ?? false,
            Puissance = valeur?.Power,
            FenetreOuverte = valeur?.WindowOpen ?? false,
            Absence = valeur?.Away ?? false,
            Statut = valeur?.Status,
            LibelleStatut = valeur?.StatusLabel,
            ProchaineBascule = valeur?.NextChange is { At: { } at, Label: { Length: > 0 } libelle } p
                && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var quand)
                ? new ProchaineBascule(quand, p.Mode ?? string.Empty, libelle)
                : null,
        };
    }

    private sealed record ExtraClimat(List<ModeDto>? Modes, double? TargetStep, int? Radiators);

    private sealed record ModeDto(string? Id, string? Label);

    private sealed record ValeurClimat(
        double? Temperature,
        double? Humidity,
        double? OutdoorTemperature,
        double? Target,
        bool? TargetAdjusted,
        string? Mode,
        bool? ScheduleActive,
        bool? Heating,
        double? Power,
        bool? WindowOpen,
        bool? Away,
        string? Status,
        string? StatusLabel,
        BasculeDto? NextChange);

    private sealed record BasculeDto(string? At, string? Mode, string? Label);
}
