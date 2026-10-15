using System.Text.Json;
using System.Text.RegularExpressions;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;

namespace Krissvia.Climate.DeviceSchedules;

/// <summary>Programmation refusée, avec la raison à montrer telle quelle.</summary>
public sealed class DeviceScheduleValidationException(string message) : Exception(message);

/// <summary>
/// Programmations des équipements : vérifiées, enregistrées (collection <c>programmations</c>), appliquées au moteur.
/// </summary>
/// <remarks>
/// Un switch n'a qu'un seul pilote : il est refusé s'il est déjà le radiateur d'un thermostat ou s'il appartient à une
/// autre programmation. Deux pilotes se contrediraient à chaque changement, sans que rien ne dise lequel a raison.
/// </remarks>
public sealed partial class DeviceScheduleService(
    IConfigurationStore store, IEntityRegistry registry, ThermostatService thermostats, DeviceScheduleEngine engine)
{
    public const string Collection = "programmations";

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private SortedDictionary<string, DeviceSchedule> _programmations = new(StringComparer.Ordinal);

    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    /// <summary>Une programmation a changé ; levé verrou rendu, la boucle du moteur se réveille.</summary>
    public event Action? Changed;

    public IReadOnlyList<DeviceSchedule> List() => _programmations.Values.ToList();

    public DeviceSchedule? Get(string id) => _programmations.GetValueOrDefault(id);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var chargees = new SortedDictionary<string, DeviceSchedule>(StringComparer.Ordinal);
            var erreurs = new List<string>();
            foreach (var document in await store.ListAsync(Collection, ct))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<DeviceSchedule>(document.Json, ThermostatService.Json)
                            ?? throw new JsonException("document vide");
                    p.Validate();
                    chargees[p.Id] = p;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    erreurs.Add($"{document.Id} : {ex.Message}");
                }
            }

            _programmations = chargees;
            LoadErrors = erreurs;
            await engine.ConfigureAsync(_programmations.Values.ToList(), ct);
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<DeviceSchedule> SaveAsync(DeviceSchedule programmation, CancellationToken ct = default)
    {
        if (programmation.Id is null || !IdentifiantValide().IsMatch(programmation.Id))
            throw new DeviceScheduleValidationException("identifiant : minuscules, chiffres, « _ » et « - », 64 caractères au plus.");
        try
        {
            programmation.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new DeviceScheduleValidationException(ex.Message);
        }

        foreach (var s in programmation.SwitchEntityIds)
        {
            var entite = await registry.GetAsync(s, ct)
                         ?? throw new DeviceScheduleValidationException($"l'entité « {s} » n'existe pas.");
            if (entite.Kind != EntityKind.Switch)
                throw new DeviceScheduleValidationException($"« {s} » est un {entite.Kind}, pas un switch.");
            if (thermostats.List().FirstOrDefault(t => t.SwitchEntityIds.Contains(s)) is { } t)
                throw new DeviceScheduleValidationException(
                    $"« {entite.DisplayName} » est déjà piloté par le thermostat « {(t.Name is { Length: > 0 } n ? n : t.Id)} ».");
        }

        await _verrou.WaitAsync(ct);
        try
        {
            if (_programmations.Values.FirstOrDefault(p => p.Id != programmation.Id && p.SwitchEntityIds.Intersect(programmation.SwitchEntityIds).Any()) is { } autre)
                throw new DeviceScheduleValidationException(
                    $"un des équipements appartient déjà à la programmation « {(autre.Name is { Length: > 0 } n ? n : autre.Id)} ».");

            await store.SaveAsync(Collection, new ConfigurationDocument(programmation.Id, JsonSerializer.Serialize(programmation, ThermostatService.Json)), ct);
            _programmations[programmation.Id] = programmation;
            await engine.ConfigureAsync(_programmations.Values.ToList(), ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return programmation;
    }

    /// <summary>Active ou désactive. Null si inconnue.</summary>
    public async Task<DeviceSchedule?> SetEnabledAsync(string id, bool actif, CancellationToken ct = default) =>
        Get(id) is { } p ? await SaveAsync(p with { Enabled = actif }, ct) : null;

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (!_programmations.ContainsKey(id)) return false;
            await store.DeleteAsync(Collection, id, ct);
            _programmations.Remove(id);
            await engine.ConfigureAsync(_programmations.Values.ToList(), ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdentifiantValide();
}
