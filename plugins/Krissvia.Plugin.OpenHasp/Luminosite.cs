using System.Text.Json;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>Rétroéclairage et veille d'une platine (repris de DomoMud).</summary>
public static class Luminosite
{
    /// <summary>
    /// Plancher du curseur : à 1 la dalle est noire, et le seul réglage qui permettrait de revenir est sur cet écran.
    /// </summary>
    public const int Minimum = 20;
    public const int Maximum = 255;

    public static int Borner(int valeur) => Math.Clamp(valeur, Minimum, Maximum);

    /// <summary>Pourcentage de l'intensité réelle de la dalle, pas de la position du doigt sur le curseur.</summary>
    public static string Libelle(int valeur) =>
        $"{(int)Math.Round(Math.Clamp(valeur, 0, Maximum) * 100.0 / Maximum, MidpointRounding.AwayFromZero)} %";

    /// <summary>
    /// État annoncé sur <c>hasp/{nœud}/state/idle</c> : « off », « short » ou « long », ou null. Selon la version,
    /// openHASP publie la valeur brute (<c>off</c>) ou un objet (<c>{"idle":"off"}</c>) : les deux sont acceptées.
    /// </summary>
    public static string? EtatVeille(string charge)
    {
        var brut = charge.Trim();
        if (brut.Length == 0) return null;
        if (brut[0] != '{') return brut.Trim('"').ToLowerInvariant() is { Length: > 0 } s ? s : null;

        try
        {
            using var doc = JsonDocument.Parse(brut);
            return doc.RootElement.TryGetProperty("idle", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()!.ToLowerInvariant()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
