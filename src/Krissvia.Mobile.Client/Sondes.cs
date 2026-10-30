using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>Moyenne d'une pièce, calculée par le cœur sur ses seules sondes à jour.</summary>
/// <param name="Moyenne">Null : aucune sonde à jour dans la pièce — pas « 0 °C ».</param>
/// <param name="Ecartees">Sondes hors ligne ou muettes, écartées de la moyenne.</param>
public sealed record Piece(string Nom, double? Moyenne, int AJour, int Total, int Ecartees);

/// <summary>
/// Une sonde de température. <see cref="Sante"/> est le code du cœur (<c>ok</c>, <c>stale</c>, <c>offline</c>,
/// <c>never_received</c>) : l'appli n'en refait pas la règle.
/// </summary>
public sealed record Sonde(
    string EntityId,
    string Nom,
    string? Piece,
    double? Temperature,
    double? Humidite,
    double? Batterie,
    DateTimeOffset? MesureeA,
    string Sante)
{
    /// <summary>Une sonde dont la valeur ne se lit pas comme une mesure du moment.</summary>
    public bool Douteuse => Sante != "ok";
}

/// <summary>L'écran Sondes tel que le serveur le calcule (<c>GET /api/sensors</c>).</summary>
public sealed record VueSondes(IReadOnlyList<Piece> Pieces, IReadOnlyList<Sonde> Sondes, int MuetteApresMinutes)
{
    public static VueSondes? Lire(JsonElement json)
    {
        var dto = json.Deserialize<VueDto>(Thermostat.Json);
        if (dto is null) return null;

        return new VueSondes(
            [.. (dto.Rooms ?? []).Select(r => new Piece(r.Room ?? "?", r.Average, r.Active, r.Total, r.Unreliable))],
            [
                .. (dto.Sensors ?? []).Where(s => s.EntityId is { Length: > 0 }).Select(s => new Sonde(
                    s.EntityId!, s.Name is { Length: > 0 } nom ? nom : s.EntityId!, s.Room, s.Temperature, s.Humidity, s.Battery,
                    s.MeasuredAt, s.Health ?? "never_received")),
            ],
            dto.StaleAfterMinutes ?? 60);
    }

    /// <summary>Le libellé d'une sonde douteuse, null pour une sonde à jour.</summary>
    public string? Alerte(Sonde sonde) => sonde.Sante switch
    {
        "ok" => null,
        "offline" => "Hors ligne",
        "stale" => $"Muette +{MuetteApresMinutes} min",
        "never_received" => "Jamais reçue",
        _ => sonde.Sante,
    };

    private sealed record VueDto(List<PieceDto>? Rooms, List<SondeDto>? Sensors, int? StaleAfterMinutes);

    private sealed record PieceDto(string? Room, double? Average, int Active, int Total, int Unreliable);

    private sealed record SondeDto(
        string? EntityId, string? Name, string? Room, double? Temperature, double? Humidity, double? Battery,
        DateTimeOffset? MeasuredAt, string? Health);
}
