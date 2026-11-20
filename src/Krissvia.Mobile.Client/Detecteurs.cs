using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>
/// Un capteur d'ouverture ou un détecteur de mouvement. <see cref="Sante"/> est le code du cœur (<c>ok</c>,
/// <c>offline</c>, <c>never_received</c>) : l'appli n'en refait pas la règle.
/// </summary>
/// <param name="Actif">Ouvert, ou mouvement détecté ; null quand l'état ne se lit pas — ni « fermé » ni « calme ».</param>
/// <param name="Surveille">Surveillé par l'alarme.</param>
public sealed record Detecteur(
    string EntityId, string Nom, string? Piece, bool Mouvement, bool? Actif, DateTimeOffset? Depuis, string Sante, double? Batterie, bool Surveille)
{
    /// <summary>« Ouvert », « Fermé », « Mouvement », « Calme », ou ce qui empêche de le savoir.</summary>
    public string Libelle => (Sante, Actif, Mouvement) switch
    {
        ("offline", _, _) => "Indisponible",
        ("never_received", _, _) => "Jamais reçu",
        (_, true, true) => "Mouvement",
        (_, false, true) => "Calme",
        (_, true, false) => "Ouvert",
        (_, false, false) => "Fermé",
        _ => "État inconnu",
    };
}

/// <summary>Les détecteurs tels que le serveur les donne (<c>GET /api/detectors</c>), dans l'ordre d'affichage.</summary>
public sealed record VueDetecteurs(IReadOnlyList<Detecteur> Detecteurs)
{
    public int Ouverts => Detecteurs.Count(d => d is { Mouvement: false, Actif: true });

    public int EnMouvement => Detecteurs.Count(d => d is { Mouvement: true, Actif: true });

    public static VueDetecteurs? Lire(JsonElement json)
    {
        var dto = json.Deserialize<VueDto>(Thermostat.Json);
        if (dto is null) return null;
        return new VueDetecteurs(
        [
            .. (dto.Detectors ?? []).Where(d => d.EntityId is { Length: > 0 }).Select(d => new Detecteur(
                d.EntityId!, d.Name is { Length: > 0 } nom ? nom : d.EntityId!, d.Room, d.Type == "motion", d.Active, d.Since,
                d.Health ?? "never_received", d.Battery, d.Monitored)),
        ]);
    }

    private sealed record VueDto(List<DetecteurDto>? Detectors);

    private sealed record DetecteurDto(
        string? EntityId, string? Name, string? Room, string? Type, bool? Active, DateTimeOffset? Since, string? Health, double? Battery, bool Monitored);
}
