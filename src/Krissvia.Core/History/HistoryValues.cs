using System.Text.Json;

namespace Krissvia.Core.History;

/// <summary>
/// Ce qu'on garde de la valeur d'une entité : une ou plusieurs séries numériques. Une série porte le nom de l'entité
/// (<c>sensor.salon_temperature</c>), ou celui de l'entité et d'un champ pour un objet (<c>climate.salon:temperature</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Un nombre est gardé tel quel ; un booléen en 1 ou 0 (la moyenne d'une heure dit alors la part du temps où
/// c'était vrai : la part de chauffe d'un radiateur) ; une lumière par son <c>on</c>.</item>
/// <item>D'un objet, seuls les champs de <see cref="Champs"/> : ceux d'un thermostat (dont l'extérieur et la puissance,
/// qu'il faut à l'auto-TPI) et la puissance d'un délesteur. Une machine virtuelle ou une alarme ne deviennent pas
/// vingt courbes que personne ne regarde.</item>
/// <item>Un texte ne se trace pas.</item>
/// </list>
/// </remarks>
public static class HistoryValues
{
    public static readonly IReadOnlyList<string> Champs = ["temperature", "target", "power", "outdoor_temperature", "humidity", "heating"];

    public static string Serie(string entityId, string? champ = null) => champ is null ? entityId : $"{entityId}:{champ}";

    /// <summary>Les séries que porte cette valeur, avec leur valeur (null : le champ est là mais vide).</summary>
    public static IReadOnlyList<(string Serie, double? Valeur)> Extraire(string entityId, JsonElement valeur)
    {
        switch (valeur.ValueKind)
        {
            case JsonValueKind.Number:
                return [(entityId, valeur.GetDouble())];
            case JsonValueKind.True or JsonValueKind.False:
                return [(entityId, valeur.GetBoolean() ? 1 : 0)];
            case JsonValueKind.Object when valeur.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return [(entityId, on.GetBoolean() ? 1 : 0)];
            case JsonValueKind.Object:
                var series = new List<(string, double?)>();
                foreach (var champ in Champs)
                {
                    if (!valeur.TryGetProperty(champ, out var v)) continue;
                    series.Add((Serie(entityId, champ), v.ValueKind switch
                    {
                        JsonValueKind.Number => v.GetDouble(),
                        JsonValueKind.True => 1,
                        JsonValueKind.False => 0,
                        _ => null,
                    }));
                }

                return series;
            default:
                return [];
        }
    }
}
