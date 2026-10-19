using System.Text.Json;
using Krissvia.Core.Configuration;

namespace Krissvia.Climate.Thermostats;

/// <summary>
/// Les pentes apprises, dans leur propre collection (<c>thermostat_pentes</c>, un document par thermostat) : le
/// moteur les écrit sans toucher aux réglages, que seul l'utilisateur modifie.
/// </summary>
public sealed class HeatingSlopeStore(IConfigurationStore store) : IHeatingSlopeStore
{
    public const string Collection = "thermostat_pentes";

    public async Task<IReadOnlyDictionary<string, HeatingSlope>> LoadAsync(CancellationToken ct = default)
    {
        var pentes = new Dictionary<string, HeatingSlope>(StringComparer.Ordinal);
        foreach (var document in await store.ListAsync(Collection, ct))
        {
            try
            {
                if (JsonSerializer.Deserialize<HeatingSlope>(document.Json, ThermostatService.Json) is { MinutesPerDegree: > 0 } pente)
                    pentes[document.Id] = pente;
            }
            catch (JsonException)
            {
                // Illisible : le thermostat repart de la pente par défaut et la réapprend.
            }
        }

        return pentes;
    }

    public Task SaveAsync(string thermostatId, HeatingSlope pente, CancellationToken ct = default) =>
        store.SaveAsync(Collection, new ConfigurationDocument(thermostatId, JsonSerializer.Serialize(pente, ThermostatService.Json)), ct);
}
