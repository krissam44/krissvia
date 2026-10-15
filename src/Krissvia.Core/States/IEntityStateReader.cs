using System.Text.Json;

namespace Krissvia.Core.States;

/// <summary>Dernier état connu d'une entité.</summary>
/// <param name="Value">Valeur brute telle que le plugin l'a publiée.</param>
/// <param name="MeasuredAt">
/// Instant de la mesure (UTC) : celui du plugin, à défaut l'heure de réception. Conservé tel quel après un
/// redémarrage : une mesure ancienne doit rester ancienne, sinon tout contrôle de fraîcheur devient aveugle.
/// </param>
/// <param name="Available">Faux si l'entité est indisponible (plugin mort, appareil injoignable).</param>
public sealed record EntityStateSnapshot(JsonElement Value, DateTimeOffset MeasuredAt, bool Available = true)
{
    /// <summary>Valeur numérique, si l'entité est disponible et porte un nombre.</summary>
    public bool TryGetNumber(out double value)
    {
        if (Available && Value.ValueKind == JsonValueKind.Number && Value.TryGetDouble(out value)) return true;

        value = 0;
        return false;
    }

    /// <summary>Valeur booléenne, si l'entité est disponible et porte vrai ou faux.</summary>
    public bool TryGetBool(out bool value)
    {
        if (Available && Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = Value.ValueKind == JsonValueKind.True;
            return true;
        }

        value = false;
        return false;
    }
}

/// <summary>
/// Lecture de l'état des entités. Le magasin d'états du cœur l'implémentera ; les moteurs ne dépendent
/// que de cette interface, ce qui les rend testables sans plugin.
/// </summary>
public interface IEntityStateReader
{
    /// <summary>Dernier état connu, ou null si l'entité n'a jamais publié.</summary>
    EntityStateSnapshot? Get(string entityId);
}
