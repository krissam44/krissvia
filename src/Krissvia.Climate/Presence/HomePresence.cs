using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.Presence;

/// <summary>La présence du foyer à un instant : de quoi décider (occupée) et de quoi l'expliquer (qui, et pourquoi).</summary>
public sealed record HomePresenceState(
    bool Occupied,
    bool DetectionLost,
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Absent,
    IReadOnlyList<string> Unknown,
    DateTimeOffset? EmptySince)
{
    public int Tracked => Present.Count + Absent.Count + Unknown.Count;
}

/// <summary>
/// La présence du foyer, calculée par le cœur sur tous les capteurs de classe <c>presence</c> (la Livebox aujourd'hui,
/// d'autres sources demain), et publiée comme l'entité <c>sensor.maison_presence</c> (classe <c>home_presence</c>).
/// C'est elle que l'absence programmée consultera.
/// </summary>
/// <remarks>
/// Règles de DomoMud :
/// <list type="bullet">
/// <item>Quelqu'un de présent : maison occupée.</item>
/// <item>Aucun capteur suivi : occupée — on ne prétend pas savoir que la maison est vide.</item>
/// <item>Personne de présent mais un capteur sans valeur (plugin arrêté, box injoignable) : <b>occupée par
/// sécurité</b>, détection perdue. Chauffer une maison vide quelques heures coûte des centimes ; une détection figée
/// sur « vide » laisserait le chauffage en hors-gel jusqu'au retour dans le froid.</item>
/// <item>Tous les suivis présents absents : vide, depuis le premier constat.</item>
/// </list>
/// </remarks>
public sealed class HomePresence(IEntityRegistry registry, IEntityStateReader states, IEntityStateWriter writer, TimeProvider clock)
{
    public const string IntegrationId = CoreIntegrations.Presence;
    public const string UniqueId = "presence";

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private string? _entite;
    private string? _publie;
    private DateTimeOffset? _videDepuis;

    public HomePresenceState? Current { get; private set; }

    /// <summary>Recalcule et publie si l'état a changé (appelé toutes les 10 s).</summary>
    public async Task<HomePresenceState> UpdateAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (_entite is null)
            {
                await registry.RegisterAsync(IntegrationId,
                [
                    new DeviceDescriptor
                    {
                        UniqueId = UniqueId, Name = "Maison",
                        Entities = [new EntityDescriptor { UniqueId = UniqueId, Kind = EntityKind.Sensor, Name = "Présence", DeviceClass = DeviceClasses.HomePresence }],
                    },
                ], ct);
                _entite = (await registry.FindByUniqueIdAsync(IntegrationId, UniqueId, ct))?.EntityId;
            }

            var etat = Compute(registry.Loaded, states, _videDepuis, clock.GetUtcNow());
            _videDepuis = etat.EmptySince;
            Current = etat;

            if (_entite is { } id)
            {
                var valeur = Valeur(etat);
                var json = valeur.GetRawText();
                if (json != _publie)
                {
                    writer.Update(id, valeur);
                    _publie = json;
                }
            }

            return etat;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Le calcul, pur : les capteurs de présence et leur état, le constat de vide précédent, l'heure.</summary>
    public static HomePresenceState Compute(IEnumerable<RegisteredEntity> entites, IEntityStateReader states, DateTimeOffset? videDepuis, DateTimeOffset maintenant)
    {
        List<string> presents = [], absents = [], inconnus = [];
        foreach (var e in entites.Where(e => e is { Kind: EntityKind.BinarySensor, DeviceClass: DeviceClasses.Presence, Enabled: true, Missing: false })
                     .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var nom = Nom(e.DisplayName);
            if (states.Get(e.EntityId) is { } s && s.TryGetBool(out var la)) (la ? presents : absents).Add(nom);
            else inconnus.Add(nom);
        }

        var suivis = presents.Count + absents.Count + inconnus.Count;
        var perdue = presents.Count == 0 && inconnus.Count > 0;
        var vide = suivis > 0 && presents.Count == 0 && !perdue;
        return new HomePresenceState(!vide, perdue, presents, absents, inconnus, vide ? videDepuis ?? maintenant : null);
    }

    /// <summary>« Téléphone de Christian Présence » → « Téléphone de Christian » ; un nom changé dans Entités reste tel quel.</summary>
    private static string Nom(string affiche) =>
        affiche.EndsWith(" Présence", StringComparison.OrdinalIgnoreCase) ? affiche[..^" Présence".Length] : affiche;

    public static JsonElement Valeur(HomePresenceState e) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["occupied"] = e.Occupied,
        ["detection_lost"] = e.DetectionLost,
        ["tracked"] = e.Tracked,
        ["present"] = new JsonArray([.. e.Present.Select(n => (JsonNode)n)]),
        ["absent"] = new JsonArray([.. e.Absent.Select(n => (JsonNode)n)]),
        ["unknown"] = new JsonArray([.. e.Unknown.Select(n => (JsonNode)n)]),
        ["empty_since"] = e.EmptySince?.ToString("O"),
    });
}
