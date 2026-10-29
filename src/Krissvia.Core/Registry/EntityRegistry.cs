using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Core.Registry;

/// <summary>
/// Registre d'entités : attribue les identifiants stables et garde l'instantané en mémoire
/// (la résolution d'une entité est appelée à chaque mesure reçue, elle ne peut pas interroger SQLite).
/// </summary>
/// <remarks>
/// Règles tirées de DomoMud :
/// <list type="bullet">
/// <item>un seul verrou, jamais imbriqué : les méthodes publiques de lecture ne le prennent que pour le chargement initial ;</item>
/// <item>l'événement <see cref="Krissvia.Core.Events.RegistryChanged"/> n'est jamais publié en tenant le verrou ;</item>
/// <item>l'instantané n'est remplacé qu'après une écriture réussie.</item>
/// </list>
/// </remarks>
public sealed class EntityRegistry(IEntityRepository repository, Krissvia.Core.Events.IEventPublisher? events = null) : IEntityRegistry
{
    private readonly SemaphoreSlim _ecriture = new(1, 1);
    private volatile Snapshot? _instantane;


    private sealed record Snapshot(
        IReadOnlyList<RegisteredDevice> Devices,
        IReadOnlyDictionary<string, RegisteredEntity> Entities,
        IReadOnlyDictionary<(string, string), RegisteredEntity> ParIdentifiantPlugin,
        IReadOnlyList<RegisteredEntity> Liste)
    {
        public static Snapshot Creer(IEnumerable<RegisteredDevice> devices, IEnumerable<RegisteredEntity> entities)
        {
            var liste = entities.ToList();
            return new Snapshot(
                devices.ToList(),
                liste.ToDictionary(e => e.EntityId),
                liste.ToDictionary(e => (e.IntegrationId, e.UniqueId)),
                liste.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList());
        }
    }

    // ── Lecture ──────────────────────────────────────────────────────────────

    public async Task<RegisteredEntity?> GetAsync(string entityId, CancellationToken ct = default)
    {
        var s = await InstantaneAsync(ct);
        return s.Entities.GetValueOrDefault(entityId);
    }

    public async Task<RegisteredEntity?> FindByUniqueIdAsync(string integrationId, string uniqueId, CancellationToken ct = default)
    {
        var s = await InstantaneAsync(ct);
        return s.ParIdentifiantPlugin.GetValueOrDefault((integrationId, uniqueId));
    }

    public async Task<IReadOnlyList<RegisteredEntity>> ListAsync(CancellationToken ct = default)
    {
        var s = await InstantaneAsync(ct);
        return s.Entities.Values.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<RegisteredDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var s = await InstantaneAsync(ct);
        return s.Devices.OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public IReadOnlyList<RegisteredEntity> Loaded => _instantane?.Liste ?? [];

    public Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default) =>
        repository.ResolveAliasAsync(alias, ct);

    // ── Déclaration d'un plugin ──────────────────────────────────────────────

    public async Task<RegistrationResult> RegisterAsync(string integrationId, IReadOnlyList<DeviceDescriptor> devices, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationId);

        var crees = new List<string>();
        var devenusManquants = new List<string>();
        var modifie = false;

        await _ecriture.WaitAsync(ct);
        try
        {
            var courant = await ChargerSousVerrouAsync(ct);
            var pris = new HashSet<string>(courant.Entities.Keys);
            var appareils = new List<RegisteredDevice>();
            var entites = new List<RegisteredEntity>();
            var declarees = new HashSet<string>();
            var identifiantsDeclares = devices.Select(d => d.UniqueId).ToHashSet(StringComparer.Ordinal);
            var repris = new HashSet<string>(StringComparer.Ordinal);

            foreach (var descripteur in devices)
            {
                var extra = SerialiserExtra(descripteur.Extra);
                var propose = descripteur.Name ?? descripteur.UniqueId;
                var existant = courant.Devices.FirstOrDefault(d =>
                                   d.IntegrationId == integrationId && d.UniqueId == descripteur.UniqueId)
                               ?? Renomme(courant, integrationId, descripteur, identifiantsDeclares, repris);
                if (existant is not null) repris.Add(existant.DeviceId);

                var appareil = existant is null
                    ? new RegisteredDevice
                    {
                        DeviceId = Guid.NewGuid().ToString("N"),
                        IntegrationId = integrationId,
                        UniqueId = descripteur.UniqueId,
                        DisplayName = propose,
                        ProposedName = propose,
                        Manufacturer = descripteur.Manufacturer,
                        Model = descripteur.Model,
                        ExtraJson = extra,
                    }
                    : existant with
                    {
                        UniqueId = descripteur.UniqueId,
                        DisplayName = Suivre(existant.DisplayName, existant.ProposedName, propose),
                        ProposedName = propose,
                        Manufacturer = descripteur.Manufacturer,
                        Model = descripteur.Model,
                        ExtraJson = extra,
                    };

                if (appareil != existant) appareils.Add(appareil);

                foreach (var d in descripteur.Entities)
                {
                    if (!declarees.Add(d.UniqueId))
                        throw new ArgumentException($"L'entité « {d.UniqueId} » est déclarée deux fois.", nameof(devices));

                    courant.ParIdentifiantPlugin.TryGetValue((integrationId, d.UniqueId), out var entiteExistante);

                    if (entiteExistante is null)
                    {
                        var id = EntityIdGenerator.Generate(d.Kind, appareil.DisplayName, d.Name ?? d.DeviceClass, pris);
                        pris.Add(id);
                        crees.Add(id);

                        entites.Add(new RegisteredEntity
                        {
                            EntityId = id,
                            DeviceId = appareil.DeviceId,
                            IntegrationId = integrationId,
                            UniqueId = d.UniqueId,
                            Kind = d.Kind,
                            DeviceClass = d.DeviceClass,
                            Unit = d.Unit,
                            StateClass = d.StateClass,
                            Commands = d.Commands,
                            ExtraJson = SerialiserExtra(d.Extra),
                            DisplayName = NomAffiche(appareil.DisplayName, d),
                            ProposedName = NomAffiche(appareil.DisplayName, d),
                        });
                    }
                    else
                    {
                        var proposeEntite = NomAffiche(appareil.DisplayName, d);
                        var mise = entiteExistante with
                        {
                            DeviceId = appareil.DeviceId,
                            DisplayName = Suivre(entiteExistante.DisplayName, entiteExistante.ProposedName, proposeEntite),
                            ProposedName = proposeEntite,
                            Kind = d.Kind,
                            DeviceClass = d.DeviceClass,
                            Unit = d.Unit,
                            StateClass = d.StateClass,
                            Commands = d.Commands,
                            ExtraJson = SerialiserExtra(d.Extra),
                            Missing = false,
                        };

                        if (!Identiques(mise, entiteExistante)) entites.Add(mise);
                    }
                }
            }

            // Ce que le plugin ne déclare plus n'est pas supprimé : on le marque.
            foreach (var disparue in courant.Entities.Values.Where(e =>
                         e.IntegrationId == integrationId && !e.Missing && !declarees.Contains(e.UniqueId)))
            {
                entites.Add(disparue with { Missing = true });
                devenusManquants.Add(disparue.EntityId);
            }

            if (appareils.Count + entites.Count > 0)
            {
                await AppliquerAsync(courant, appareils, entites, ct);
                modifie = true;
            }
        }
        finally
        {
            _ecriture.Release();
        }

        if (modifie) events?.Publish(new Krissvia.Core.Events.RegistryChanged());
        return new RegistrationResult(crees, devenusManquants);
    }

    // ── Champs de l'utilisateur ──────────────────────────────────────────────

    public Task RenameAsync(string entityId, string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return MettreAJourAsync(entityId, e => e with { DisplayName = displayName.Trim() }, ct);
    }

    public Task SetUsageAsync(string entityId, string? usage, CancellationToken ct = default) =>
        MettreAJourAsync(entityId, e => e with { Usage = string.IsNullOrWhiteSpace(usage) ? null : usage.Trim() }, ct);

    public Task SetEnabledAsync(string entityId, bool enabled, CancellationToken ct = default) =>
        MettreAJourAsync(entityId, e => e with { Enabled = enabled }, ct);

    public async Task<bool> DeleteAsync(string entityId, CancellationToken ct = default)
    {
        await _ecriture.WaitAsync(ct);
        try
        {
            var courant = await ChargerSousVerrouAsync(ct);
            if (!courant.Entities.TryGetValue(entityId, out var entite)) return false;
            if (!entite.Missing)
                throw new InvalidOperationException(
                    $"« {entite.DisplayName} » est encore déclarée par son plugin : supprimée, elle reviendrait à sa prochaine connexion. Désactivez-la plutôt.");

            var orpheline = courant.Entities.Values.Any(e => e.DeviceId == entite.DeviceId && e.EntityId != entityId) ? null : entite.DeviceId;
            await repository.DeleteEntityAsync(entityId, orpheline, ct);

            var entites = courant.Entities.Values.Where(e => e.EntityId != entityId);
            var appareils = courant.Devices.Where(d => d.DeviceId != orpheline);
            _instantane = Snapshot.Creer(appareils, entites);
        }
        finally
        {
            _ecriture.Release();
        }

        events?.Publish(new Krissvia.Core.Events.RegistryChanged());
        return true;
    }

    public async Task SetRoomAsync(string deviceId, string? room, CancellationToken ct = default)
    {
        await _ecriture.WaitAsync(ct);
        try
        {
            var courant = await ChargerSousVerrouAsync(ct);
            var appareil = courant.Devices.FirstOrDefault(d => d.DeviceId == deviceId)
                           ?? throw new KeyNotFoundException($"Appareil inconnu : {deviceId}");
            var mis = appareil with { Room = string.IsNullOrWhiteSpace(room) ? null : room.Trim() };
            if (mis != appareil) await AppliquerAsync(courant, [mis], [], ct);
        }
        finally
        {
            _ecriture.Release();
        }
    }

    public Task SetWidgetAsync(string entityId, string? widget, CancellationToken ct = default) =>
        MettreAJourAsync(entityId, e => e with { Widget = string.IsNullOrWhiteSpace(widget) ? null : widget.Trim() }, ct);

    public Task SetNominalPowerAsync(string entityId, double? watts, CancellationToken ct = default)
    {
        if (watts is { } w && (double.IsNaN(w) || w < 0))
            throw new ArgumentOutOfRangeException(nameof(watts), watts, "Une puissance ne peut pas être négative.");
        return MettreAJourAsync(entityId, e => e with { NominalPower = watts is > 0 ? Math.Round(watts.Value) : null }, ct);
    }

    public async Task AddAliasAsync(string alias, string entityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        await _ecriture.WaitAsync(ct);
        try
        {
            var courant = await ChargerSousVerrouAsync(ct);
            if (!courant.Entities.ContainsKey(entityId))
                throw new KeyNotFoundException($"Entité inconnue : {entityId}");

            var existant = await repository.ResolveAliasAsync(alias, ct);
            if (existant is not null && existant != entityId)
                throw new InvalidOperationException($"L'alias « {alias} » désigne déjà {existant}.");

            if (existant is null) await repository.AddAliasAsync(alias, entityId, ct);
        }
        finally
        {
            _ecriture.Release();
        }
    }

    // ── Interne ──────────────────────────────────────────────────────────────

    private async Task MettreAJourAsync(string entityId, Func<RegisteredEntity, RegisteredEntity> modification, CancellationToken ct)
    {
        var modifie = false;

        await _ecriture.WaitAsync(ct);
        try
        {
            var courant = await ChargerSousVerrouAsync(ct);
            if (!courant.Entities.TryGetValue(entityId, out var entite))
                throw new KeyNotFoundException($"Entité inconnue : {entityId}");

            var mise = modification(entite);
            if (!Identiques(mise, entite))
            {
                await AppliquerAsync(courant, [], [mise], ct);
                modifie = true;
            }
        }
        finally
        {
            _ecriture.Release();
        }

        if (modifie) events?.Publish(new Krissvia.Core.Events.RegistryChanged());
    }

    /// <summary>Lecture publique : ne prend le verrou que pour le tout premier chargement.</summary>
    private async Task<Snapshot> InstantaneAsync(CancellationToken ct)
    {
        if (_instantane is { } courant) return courant;

        await _ecriture.WaitAsync(ct);
        try
        {
            return await ChargerSousVerrouAsync(ct);
        }
        finally
        {
            _ecriture.Release();
        }
    }

    /// <summary>À n'appeler QUE verrou déjà pris (le verrou n'est pas réentrant).</summary>
    private async Task<Snapshot> ChargerSousVerrouAsync(CancellationToken ct) =>
        _instantane ??= Snapshot.Creer(
            await repository.ListDevicesAsync(ct),
            await repository.ListEntitiesAsync(ct));

    /// <summary>Écrit en base, puis seulement alors remplace l'instantané.</summary>
    private async Task AppliquerAsync(
        Snapshot courant,
        IReadOnlyList<RegisteredDevice> devices,
        IReadOnlyList<RegisteredEntity> entities,
        CancellationToken ct)
    {
        await repository.SaveAsync(devices, entities, ct);

        var appareils = courant.Devices.ToDictionary(d => d.DeviceId);
        foreach (var d in devices) appareils[d.DeviceId] = d;

        var entites = courant.Entities.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var e in entities) entites[e.EntityId] = e;

        _instantane = Snapshot.Creer(appareils.Values, entites.Values);
    }

    private static bool Identiques(RegisteredEntity a, RegisteredEntity b) =>
        a.Commands.SequenceEqual(b.Commands) && (a with { Commands = b.Commands }) == b;

    /// <summary>
    /// Le nom affiché suit le nom proposé tant que l'utilisateur ne l'a pas changé (il lui est encore égal). Sans nom
    /// proposé connu (enregistré avant le schéma v7), on ne sait pas : le nom reste.
    /// </summary>
    private static string Suivre(string affiche, string? anciennementPropose, string propose) =>
        anciennementPropose is not null && affiche == anciennementPropose ? propose : affiche;

    /// <summary>
    /// Un appareil à l'identifiant nouveau dont toutes les entités déjà connues viennent d'UN appareil que la
    /// déclaration ne mentionne plus : c'est le même, renommé. Une entité manuelle de MQTT Discovery tire l'identifiant
    /// de son appareil de son nom ; sans cette règle, le renommer en créait un second, vide l'ancien, et perdait sa pièce.
    /// Un identifiant matériel (adresse IEEE, MAC) ne change jamais : la règle ne joue pas pour lui.
    /// </summary>
    private static RegisteredDevice? Renomme(Snapshot courant, string integrationId, DeviceDescriptor descripteur,
        HashSet<string> identifiantsDeclares, HashSet<string> repris)
    {
        var origines = descripteur.Entities
            .Select(d => courant.ParIdentifiantPlugin.GetValueOrDefault((integrationId, d.UniqueId))?.DeviceId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (origines.Count != 1 || repris.Contains(origines[0])) return null;

        var ancien = courant.Devices.FirstOrDefault(d => d.DeviceId == origines[0]);
        return ancien is not null && ancien.IntegrationId == integrationId && !identifiantsDeclares.Contains(ancien.UniqueId) ? ancien : null;
    }

    private static string NomAffiche(string appareil, EntityDescriptor d) =>
        d.Name is not null ? $"{appareil} {d.Name}"
        : d.DeviceClass is not null ? $"{appareil} {d.DeviceClass}"
        : appareil;

    private static string? SerialiserExtra(IReadOnlyDictionary<string, JsonElement>? extra) =>
        extra is null || extra.Count == 0 ? null : JsonSerializer.Serialize(extra);
}
