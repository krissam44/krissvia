using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.LoadShedding;

/// <summary>
/// Publie le délesteur comme une entité du cœur (intégration <c>coeur.delestage</c>, classe <c>load_shedding</c>) :
/// le tableau de bord, l'API et le flux en direct le voient comme toute autre entité, à la manière des thermostats.
/// </summary>
/// <remarks>
/// Valeur : un objet — activé, puissance lue et sa source, abonnement, seuils, équipements délestés (avec leur nom) et
/// ceux dont la coupure n'est pas confirmée. Publié après chaque passage du délesteur, seulement s'il a changé ; rien
/// avant le premier passage. Pas d'ordre : on règle le délesteur dans son écran.
/// </remarks>
public sealed class LoadSheddingEntities(LoadSheddingService service, IEntityRegistry registry, IEntityStateWriter writer)
{
    public const string IntegrationId = CoreIntegrations.LoadShedding;
    public const string UniqueId = "delesteur";

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private string? _entite;
    private string? _publie;

    public async Task PublishAsync(CancellationToken ct = default)
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
                        // « Maison Délesteur », sensor.maison_delesteur : le registre accole le nom de l'appareil et celui de
                        // l'entité (ou sa classe), et « Délesteur Délesteur » ne se lirait pas mieux. Renommable dans Entités.
                        UniqueId = UniqueId, Name = "Maison",
                        Entities = [new EntityDescriptor { UniqueId = UniqueId, Kind = EntityKind.Sensor, Name = "Délesteur", DeviceClass = DeviceClasses.LoadShedding }],
                    },
                ], ct);
                _entite = (await registry.FindByUniqueIdAsync(IntegrationId, UniqueId, ct))?.EntityId;
                if (_entite is null) return;
            }

            if (service.Status.LastTickAt is null) return;   // « jamais reçu » vaut mieux qu'un état inventé
            var etat = Etat(service.Configuration, service.Status);
            var json = etat.GetRawText();
            if (json == _publie) return;

            writer.Update(_entite, etat);
            _publie = json;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>L'état publié (pur, testé) : de quoi dessiner la carte du délesteur de DomoMud.</summary>
    public static JsonElement Etat(LoadSheddingConfiguration c, LoadSheddingStatus s)
    {
        var noms = c.Devices.ToDictionary(d => d.Id, d => d.Name is { Length: > 0 } n ? n : d.Id, StringComparer.Ordinal);
        JsonArray Liste(IEnumerable<string> ids) => [.. ids.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = noms.GetValueOrDefault(id, id) })];

        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["enabled"] = s.Enabled,
            ["power"] = s.Power is { } p ? Math.Round(p) : null,
            ["source"] = s.ActiveSource,
            ["source_unavailable"] = s.SourceUnavailable,
            ["subscription_va"] = c.SubscriptionKva * 1000,
            ["alert_threshold"] = Math.Round(s.AlertThreshold),
            ["restore_threshold"] = Math.Round(s.RestoreThreshold),
            ["shedding"] = s.ShedDevices.Count > 0,
            ["shed"] = Liste(s.ShedDevices),
            ["unconfirmed"] = Liste(s.UnconfirmedDevices),
            ["devices"] = c.Devices.Count,
        });
    }
}
