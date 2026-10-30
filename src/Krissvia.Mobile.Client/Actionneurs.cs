using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>
/// Un interrupteur ou une lumière. Ce qui le pilote (<see cref="PiloteePar"/>, <see cref="Programmation"/>,
/// <see cref="Deleste"/>) et s'il se commande d'ici (<see cref="Commandable"/>) viennent du serveur.
/// </summary>
/// <param name="Allume">Null : jamais publié — ni allumé ni éteint.</param>
public sealed record Actionneur(
    string EntityId,
    string Nom,
    string? Usage,
    bool? Allume,
    bool Disponible,
    string? PiloteePar,
    string? Programmation,
    bool Deleste,
    bool Commandable)
{
    /// <summary>Pourquoi on ne le bascule pas, ou ce qu'il faut savoir avant de le faire ; null s'il n'y a rien à dire.</summary>
    public string? Remarque =>
        !Disponible ? "Injoignable"
        : PiloteePar is { } t ? $"Thermostat « {t} »"
        : Deleste ? "Délesté"
        : Programmation is { } p ? $"Programmation « {p} »"
        : null;
}

public sealed record PieceActionneurs(string? Nom, IReadOnlyList<Actionneur> Actionneurs);

/// <summary>L'écran Actionneurs tel que le serveur le calcule (<c>GET /api/actuators</c>).</summary>
public sealed record VueActionneurs(IReadOnlyList<PieceActionneurs> Pieces)
{
    public int Total => Pieces.Sum(p => p.Actionneurs.Count);

    public int Allumes => Pieces.Sum(p => p.Actionneurs.Count(a => a.Allume == true));

    public static VueActionneurs? Lire(JsonElement json)
    {
        var dto = json.Deserialize<VueDto>(Thermostat.Json);
        if (dto is null) return null;

        return new VueActionneurs(
        [
            .. (dto.Rooms ?? []).Select(p => new PieceActionneurs(p.Room,
            [
                .. (p.Actuators ?? []).Where(a => a.EntityId is { Length: > 0 }).Select(a => new Actionneur(
                    a.EntityId!, a.Name is { Length: > 0 } n ? n : a.EntityId!, a.Usage, a.On, a.Available,
                    a.DrivenBy, a.ScheduledBy, a.Shed, a.Controllable)),
            ])),
        ]);
    }

    private sealed record VueDto(List<PieceDto>? Rooms);

    private sealed record PieceDto(string? Room, List<ActionneurDto>? Actuators);

    private sealed record ActionneurDto(
        string? EntityId, string? Name, string? Usage, bool? On, bool Available, string? DrivenBy, string? ScheduledBy,
        bool Shed, bool Controllable);
}
