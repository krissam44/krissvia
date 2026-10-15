using System.Text.Json;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.LoadShedding;

/// <summary>Configuration de délestage refusée, avec la raison à montrer telle quelle.</summary>
public sealed class LoadSheddingValidationException(string message) : Exception(message);

/// <summary>
/// Configuration du délestage : vérifiée, enregistrée (un document <c>delestage/config</c>), appliquée au
/// délesteur ; et la mesure de puissance à lui fournir à chaque tick.
/// </summary>
/// <remarks>
/// Toute référence est vérifiée à l'enregistrement : switches et compteurs au registre avec leur famille,
/// thermostats liés auprès du service des thermostats. Un équipement délestable qui ne coupe rien n'est pas
/// une protection : il est refusé.
/// </remarks>
public sealed class LoadSheddingService(
    IConfigurationStore store,
    IEntityRegistry registry,
    IEntityStateReader states,
    ThermostatService thermostats,
    LoadShedder shedder)
{
    public const string Collection = "delestage";
    public const string DocumentId = "config";

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private LoadSheddingConfiguration _configuration = new();
    private IPowerSource? _source;
    private volatile LoadSheddingStatus? _etat;

    public LoadSheddingConfiguration Configuration => _configuration;

    /// <summary>Raison pour laquelle la configuration enregistrée n'a pas pu être appliquée au démarrage.</summary>
    public string? LoadError { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var document = (await store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == DocumentId);
            if (document is null) return;

            try
            {
                var configuration = JsonSerializer.Deserialize<LoadSheddingConfiguration>(document.Json, ThermostatService.Json)
                                    ?? throw new JsonException("document vide");
                await AppliquerAsync(configuration, ct);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Délesteur laissé désactivé plutôt que configuré à moitié ; l'erreur est montrée.
                LoadError = ex.Message;
            }
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<LoadSheddingConfiguration> SaveAsync(LoadSheddingConfiguration configuration, CancellationToken ct = default)
    {
        await ValiderAsync(configuration, ct);

        await _verrou.WaitAsync(ct);
        try
        {
            await store.SaveAsync(Collection,
                new ConfigurationDocument(DocumentId, JsonSerializer.Serialize(configuration, ThermostatService.Json)), ct);
            await AppliquerAsync(configuration, ct);
            LoadError = null;
            return configuration;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>La mesure à donner au délesteur, ramenée à l'unité des seuils ; null si aucune n'est exploitable.</summary>
    public PowerReading? ReadPower() =>
        PowerReadings.ToReading(_source?.Read(), Math.Max(1.0, _configuration.ActiveToApparentFactor));

    /// <summary>Retient ce que le dernier tick a lu et décidé, pour l'API.</summary>
    public void RecordTick(PowerReading? lecture, LoadSheddingTick tick, DateTimeOffset maintenant)
    {
        var reglages = _configuration.ToSettings();
        _etat = new LoadSheddingStatus(
            _configuration.Enabled, lecture?.Value, lecture?.MeasuredAt, tick.SourceUnavailable,
            reglages.AlertThreshold, reglages.RestoreThreshold, shedder.CurrentlyShed, tick.Unconfirmed, maintenant);
    }

    public LoadSheddingStatus Status
    {
        get
        {
            if (_etat is { } etat) return etat;
            var reglages = _configuration.ToSettings();
            return new LoadSheddingStatus(_configuration.Enabled, null, null, false,
                reglages.AlertThreshold, reglages.RestoreThreshold, shedder.CurrentlyShed, [], null);
        }
    }

    // ── Interne ──────────────────────────────────────────────────────────────

    private async Task AppliquerAsync(LoadSheddingConfiguration c, CancellationToken ct)
    {
        await shedder.ConfigureAsync(c.ToSettings(), c.Devices.Select(d => d.ToDevice()).ToList(), ct);
        _source = c.SourceKind == PowerSourceKind.Entity
            ? c.PowerEntityId is { } id ? new EntityPowerSource(states, id, c.PowerQuality) : null
            : new EquipmentSumPowerSource(states,
                c.MeteredEquipment.Select(e => new MeteredEquipment(e.SwitchEntityId, e.PowerEntityId, e.NominalPower)).ToList());
        _configuration = c;
        _etat = null;
    }

    private async Task ValiderAsync(LoadSheddingConfiguration c, CancellationToken ct)
    {
        try
        {
            _ = c.ToSettings();
        }
        catch (ArgumentException ex)
        {
            throw new LoadSheddingValidationException(ex.Message);
        }

        if (c.ActiveToApparentFactor < 1)
            throw new LoadSheddingValidationException("la majoration d'une mesure en W doit être au moins 1.");
        if (c.MaxReadingAgeSeconds <= 0 || c.SettleDelaySeconds < 0 || c.CutVerificationTimeoutSeconds <= 0)
            throw new LoadSheddingValidationException("délais invalides.");

        if (c.SourceKind == PowerSourceKind.Entity)
        {
            if (string.IsNullOrWhiteSpace(c.PowerEntityId))
                throw new LoadSheddingValidationException("source : l'entité de puissance est obligatoire.");
            await VerifierAsync(c.PowerEntityId, EntityKind.Sensor, "source de puissance", ct);
        }
        else
        {
            if (c.MeteredEquipment.Count == 0)
                throw new LoadSheddingValidationException("source : aucun équipement à additionner.");
            foreach (var e in c.MeteredEquipment)
            {
                await VerifierAsync(e.SwitchEntityId, EntityKind.Switch, "équipement mesuré", ct);
                if (e.PowerEntityId is { } p) await VerifierAsync(p, EntityKind.Sensor, "mesure d'équipement", ct);
            }
        }

        if (c.Devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != c.Devices.Count)
            throw new LoadSheddingValidationException("deux équipements délestables ont le même identifiant.");

        foreach (var d in c.Devices)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) throw new LoadSheddingValidationException("équipement délestable sans identifiant.");
            if (d.SwitchEntityIds.Count == 0)
                throw new LoadSheddingValidationException($"« {d.Id} » : aucun switch à couper, ce ne serait pas une protection.");
            if (d.NominalPower <= 0)
                throw new LoadSheddingValidationException($"« {d.Id} » : la puissance nominale doit être positive.");
            if (d.Priority is < 1 or > 10)
                throw new LoadSheddingValidationException($"« {d.Id} » : priorité de 1 (délesté en premier) à 10.");
            if (d.RestoreDelaySeconds < 0) throw new LoadSheddingValidationException($"« {d.Id} » : délai négatif.");

            foreach (var s in d.SwitchEntityIds) await VerifierAsync(s, EntityKind.Switch, $"« {d.Id} »", ct);
            if (d.ThermostatId is { } t && thermostats.Get(t) is null)
                throw new LoadSheddingValidationException($"« {d.Id} » : le thermostat « {t} » n'existe pas.");
        }
    }

    private async Task VerifierAsync(string entityId, EntityKind attendu, string role, CancellationToken ct)
    {
        var entite = await registry.GetAsync(entityId, ct)
                     ?? throw new LoadSheddingValidationException($"{role} : l'entité « {entityId} » n'existe pas.");
        if (entite.Kind != attendu)
            throw new LoadSheddingValidationException($"{role} : « {entityId} » est un {entite.Kind}, pas un {attendu}.");
    }
}
