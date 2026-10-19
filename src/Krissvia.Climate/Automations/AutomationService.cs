using System.Text.Json;
using System.Text.RegularExpressions;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;

namespace Krissvia.Climate.Automations;

/// <summary>Automatisme refusé, avec la raison à montrer telle quelle.</summary>
public sealed class AutomationValidationException(string message) : Exception(message);

/// <summary>
/// Les automatismes : vérifiés, enregistrés (collection <c>automatismes</c>), appliqués au moteur.
/// </summary>
/// <remarks>
/// Toute référence est vérifiée au registre, avec sa famille : un déclencheur « bouton » vise une entité <c>event</c>,
/// un « changement d'état » un tout-ou-rien, une action un switch ou une lumière. Un équipement renommé ne laisse
/// donc pas un automatisme agir dans le vide en se croyant en règle (le piège des topics renommés de DomoMud).
/// </remarks>
public sealed partial class AutomationService(IConfigurationStore store, IEntityRegistry registry, ThermostatService thermostats, AutomationEngine engine)
{
    public const string Collection = "automatismes";

    private static readonly EntityKind[] ToutOuRien = [EntityKind.BinarySensor, EntityKind.Switch, EntityKind.Light];
    private static readonly EntityKind[] Pilotables = [EntityKind.Switch, EntityKind.Light];

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private SortedDictionary<string, Automation> _automatismes = new(StringComparer.Ordinal);

    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public event Action? Changed;

    public IReadOnlyList<Automation> List() => _automatismes.Values.ToList();

    public Automation? Get(string id) => _automatismes.GetValueOrDefault(id);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var charges = new SortedDictionary<string, Automation>(StringComparer.Ordinal);
            var erreurs = new List<string>();
            foreach (var document in await store.ListAsync(Collection, ct))
            {
                try
                {
                    var a = JsonSerializer.Deserialize<Automation>(document.Json, ThermostatService.Json) ?? throw new JsonException("document vide");
                    a.Validate();
                    charges[a.Id] = a;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    erreurs.Add($"{document.Id} : {ex.Message}");
                }
            }

            _automatismes = charges;
            LoadErrors = erreurs;
            engine.Configure(_automatismes.Values.ToList());
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<Automation> SaveAsync(Automation a, CancellationToken ct = default)
    {
        if (a.Id is null || !IdentifiantValide().IsMatch(a.Id))
            throw new AutomationValidationException("identifiant : minuscules, chiffres, « _ » et « - », 64 caractères au plus.");
        try
        {
            a.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new AutomationValidationException(ex.Message);
        }

        await VerifierAsync(a, ct);

        await _verrou.WaitAsync(ct);
        try
        {
            await store.SaveAsync(Collection, new ConfigurationDocument(a.Id, JsonSerializer.Serialize(a, ThermostatService.Json)), ct);
            _automatismes[a.Id] = a;
            engine.Configure(_automatismes.Values.ToList());
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return a;
    }

    /// <summary>Active ou désactive. Null si inconnu.</summary>
    public async Task<Automation?> SetEnabledAsync(string id, bool actif, CancellationToken ct = default) =>
        Get(id) is { } a ? await SaveAsync(a with { Enabled = actif }, ct) : null;

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (!_automatismes.Remove(id)) return false;
            await store.DeleteAsync(Collection, id, ct);
            engine.Configure(_automatismes.Values.ToList());
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return true;
    }

    private async Task VerifierAsync(Automation a, CancellationToken ct)
    {
        switch (a.Trigger.Type)
        {
            case TriggerType.Button:
                await EntiteAsync(a.Trigger.EntityId!, "déclencheur", ct, EntityKind.Event);
                break;
            case TriggerType.StateChange:
                await EntiteAsync(a.Trigger.EntityId!, "déclencheur", ct, ToutOuRien);
                break;
        }

        foreach (var c in a.Conditions)
        {
            switch (c.Type)
            {
                case ConditionType.ThermostatMode:
                    Thermostat(c.ThermostatId!, "condition");
                    break;
                case ConditionType.EntityActive:
                    await EntiteAsync(c.EntityId!, "condition", ct, ToutOuRien);
                    break;
                case ConditionType.Above or ConditionType.Below:
                    await EntiteAsync(c.EntityId!, "condition", ct, EntityKind.Sensor);
                    break;
            }
        }

        foreach (var action in a.Actions)
        {
            switch (action.Type)
            {
                case ActionType.TurnOn or ActionType.TurnOff or ActionType.Toggle:
                    await EntiteAsync(action.EntityId!, "action", ct, Pilotables);
                    break;
                case ActionType.ThermostatMode:
                    Thermostat(action.ThermostatId!, "action");
                    break;
            }
        }
    }

    private async Task EntiteAsync(string id, string role, CancellationToken ct, params EntityKind[] familles)
    {
        var e = await registry.GetAsync(id, ct) ?? throw new AutomationValidationException($"{role} : l'entité « {id} » n'existe pas.");
        if (!familles.Contains(e.Kind))
            throw new AutomationValidationException($"{role} : « {e.DisplayName} » est un {e.Kind}, attendu : {string.Join(" ou ", familles)}.");
    }

    private void Thermostat(string id, string role)
    {
        if (thermostats.Get(id) is null) throw new AutomationValidationException($"{role} : le thermostat « {id} » n'existe pas.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdentifiantValide();
}
