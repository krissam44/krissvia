using System.Collections.Concurrent;

namespace Krissvia.Climate;

/// <summary>
/// L'état qu'un pilote (une programmation) veut pour un switch en ce moment. Le délesteur s'en sert au
/// rétablissement : il remet l'état VOULU, pas « allumé » — sinon un chauffe-eau délesté à 13:58 et rétabli à 14:01
/// serait rallumé alors que sa programmation l'avait éteint à 14:00.
/// </summary>
/// <remarks>
/// Un registre partagé plutôt qu'une référence entre moteurs : la programmation interroge déjà le délesteur (ne pas
/// rallumer un équipement délesté), l'inverse ferait une dépendance circulaire.
/// </remarks>
public sealed class DesiredSwitchStates
{
    private readonly ConcurrentDictionary<string, bool> _etats = new(StringComparer.Ordinal);

    public void Set(string entityId, bool on) => _etats[entityId] = on;

    public void Clear(string entityId) => _etats.TryRemove(entityId, out _);

    /// <summary>L'état voulu, ou null si aucun pilote ne s'en charge (le délesteur rallume alors, comme avant).</summary>
    public bool? Get(string entityId) => _etats.TryGetValue(entityId, out var on) ? on : null;
}
