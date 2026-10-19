using System.Globalization;
using System.Text.Json;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.Alerts;

/// <summary>Ce que l'analyse a fait pour un lieu : levé, rétabli, rien, ou rien à conclure.</summary>
public sealed record FrostCheck(string EntityId, string Place, double? Minimum, string Outcome);

/// <summary>
/// Alerte gel (DomoMud) : pour chaque lieu du plugin Météo, le minimum prévu sur l'horizon ; à ce seuil ou en dessous,
/// une alerte « Risque de gel à … », rétablie quand le minimum remonte. Une alerte par lieu : un appartement inoccupé
/// et la maison ne gèlent pas les mêmes nuits.
/// </summary>
/// <remarks>
/// Une prévision indisponible ou trop vieille ne conclut rien, dans un sens comme dans l'autre : ni alerte sur une
/// donnée morte, ni « gel écarté » faute de données. La quarantaine vaut un horizon complet, contre le clignotement.
/// </remarks>
public sealed class FrostWatch(IEntityRegistry registry, IEntityStateReader states, AlertService alerts, TimeProvider clock)
{
    public const string KeyPrefix = "gel:";

    public async Task<IReadOnlyList<FrostCheck>> CheckAsync(FrostAlertSettings reglages, CancellationToken ct = default)
    {
        var resultats = new List<FrostCheck>();
        if (!reglages.Enabled) return resultats;

        var maintenant = clock.GetUtcNow();
        var horizon = TimeSpan.FromHours(reglages.HorizonHours);
        var lieux = registry.Loaded.Where(e => e is { Kind: EntityKind.Sensor, DeviceClass: DeviceClasses.Weather, Enabled: true, Missing: false });

        foreach (var lieu in lieux)
        {
            var etat = states.Get(lieu.EntityId);
            var nom = Nom(lieu, etat);
            if (etat is not { Available: true } || maintenant - etat.MeasuredAt > OutdoorForecast.MaxAge
                || OutdoorForecast.MinimumAhead(etat.Value, maintenant, horizon) is not { } mini)
            {
                resultats.Add(new FrostCheck(lieu.EntityId, nom, null, "prévision indisponible"));
                continue;
            }

            var cle = KeyPrefix + lieu.EntityId;
            if (mini <= reglages.ThresholdCelsius)
            {
                var issue = await alerts.RaiseAsync(cle, new Notification(
                    $"Risque de gel à {nom}",
                    $"Minimum prévu {Degres(mini)} dans les {reglages.HorizonHours} h (seuil {Degres(reglages.ThresholdCelsius)}). "
                    + "Pensez aux points sensibles si le logement est inoccupé.",
                    Priority: 4, Tags: ["snowflake"]), quarantaine: horizon, ct);
                resultats.Add(new FrostCheck(lieu.EntityId, nom, mini, issue.ToString()));
            }
            else
            {
                var fin = await alerts.ResolveAsync(cle, new Notification(
                    $"Risque de gel écarté à {nom}",
                    $"Minimum prévu remonté à {Degres(mini)} sur les {reglages.HorizonHours} h.",
                    Priority: 2, Tags: ["sunny"]), ct);
                resultats.Add(new FrostCheck(lieu.EntityId, nom, mini, fin ? "Resolved" : "Ok"));
            }
        }

        return resultats;
    }

    private static string Nom(RegisteredEntity lieu, EntityStateSnapshot? etat) =>
        etat?.Value is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : lieu.DisplayName;

    private static string Degres(double v) => v.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) + " °C";
}
