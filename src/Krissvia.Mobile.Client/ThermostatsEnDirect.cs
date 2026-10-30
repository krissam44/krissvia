using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>
/// Les thermostats tenus à jour par les messages du flux en direct. Ne fait aucune entrée-sortie : il reçoit les
/// messages tels quels, ce qui le rend testable sans serveur.
/// </summary>
/// <remarks>
/// Thread-safe : le flux écrit depuis sa boucle, l'écran lit depuis son fil.
/// </remarks>
public sealed class ThermostatsEnDirect
{
    private readonly Lock _verrou = new();
    private Dictionary<string, EntiteDto> _entites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EtatDto?> _etats = new(StringComparer.Ordinal);
    private bool _recu;

    /// <summary>Levé après chaque message qui a changé quelque chose.</summary>
    public event Action? Changed;

    /// <summary>Vrai dès la première liste reçue : avant, « aucun thermostat » serait un mensonge.</summary>
    public bool Recu
    {
        get { lock (_verrou) return _recu; }
    }

    /// <summary>Les identifiants suivis, pour restreindre le flux.</summary>
    public IReadOnlyList<string> EntityIds
    {
        get { lock (_verrou) return [.. _entites.Keys.Order(StringComparer.Ordinal)]; }
    }

    /// <summary>Les thermostats actifs, par nom.</summary>
    public IReadOnlyList<Thermostat> Thermostats
    {
        get
        {
            lock (_verrou)
            {
                return
                [
                    .. _entites.Values
                        .Select(e => Thermostat.Depuis(e, _etats.GetValueOrDefault(e.EntityId)))
                        .OrderBy(t => t.Nom, StringComparer.CurrentCultureIgnoreCase),
                ];
            }
        }
    }

    /// <summary>
    /// Oublie tout, comme avant la première liste : au changement de lieu, les thermostats de l'appartement ne doivent
    /// pas rester affichés sous le nom de la maison le temps que le flux de la maison réponde.
    /// </summary>
    public void Vider()
    {
        lock (_verrou)
        {
            _entites = new(StringComparer.Ordinal);
            _etats.Clear();
            _recu = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Remplace la liste par celle de <c>/api/entities</c> (toutes les entités : seules les climate sont gardées).</summary>
    public void Remplacer(JsonElement entites)
    {
        lock (_verrou) RemplacerSansNotifier(entites);
        Changed?.Invoke();
    }

    /// <summary>
    /// Applique un message du flux : <c>entities</c> remplace la liste et ses états, <c>states</c> met à jour les états
    /// des thermostats connus. Un message d'un autre type, ou illisible, est ignoré : renvoie faux.
    /// </summary>
    public bool Appliquer(string message)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var racine = document.RootElement;
            if (racine.ValueKind != JsonValueKind.Object || !racine.TryGetProperty("type", out var type)) return false;

            switch (type.GetString())
            {
                case "entities" when racine.TryGetProperty("entities", out var entites):
                    lock (_verrou) RemplacerSansNotifier(entites);
                    break;

                case "states" when racine.TryGetProperty("states", out var etats) && etats.ValueKind == JsonValueKind.Object:
                    var change = false;
                    lock (_verrou)
                    {
                        foreach (var etat in etats.EnumerateObject())
                        {
                            // Une entité hors de la liste (filtre élargi, autre client) ne crée pas de thermostat.
                            if (!_entites.ContainsKey(etat.Name)) continue;
                            _etats[etat.Name] = etat.Value.ValueKind == JsonValueKind.Null ? null : etat.Value.Deserialize<EtatDto>(Thermostat.Json);
                            change = true;
                        }
                    }
                    if (!change) return false;
                    break;

                default:
                    return false;
            }
        }

        Changed?.Invoke();
        return true;
    }

    private void RemplacerSansNotifier(JsonElement entites)
    {
        if (entites.ValueKind != JsonValueKind.Array) return;

        var climats = entites.EnumerateArray()
            .Select(e => e.Deserialize<EntiteDto>(Thermostat.Json))
            // Une entité désactivée ou manquante (thermostat supprimé) n'a rien à faire sur l'écran.
            .Where(e => e is { Kind: "climate", Enabled: true, Missing: false })
            .ToDictionary(e => e!.EntityId, e => e!, StringComparer.Ordinal);

        _entites = climats;
        _etats.Clear();
        foreach (var (id, entite) in climats) _etats[id] = entite.State;
        _recu = true;
    }
}
