using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.Thermostats;

/// <summary>
/// Publie chaque thermostat comme une entité <c>climate</c> du cœur (intégration <c>coeur.thermostats</c>) et
/// exécute les ordres qu'elle reçoit. Les widgets, l'API et le flux en direct voient ainsi un thermostat comme toute
/// autre entité (WIDGETS.md §10).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Déclaration : un appareil et une entité par thermostat, identifiés par l'identifiant du thermostat. Un
/// thermostat supprimé laisse une entité « manquante », comme un appareil retiré d'un plugin.</item>
/// <item>Le nom de l'entité est celui du thermostat : c'est sa configuration qui le fixe.</item>
/// <item>État : un objet, publié seulement s'il a changé, après chaque passage du moteur. Rien tant que le moteur
/// n'a pas tourné : « jamais reçu » vaut mieux qu'un état inventé.</item>
/// <item>Ordres : <c>set_mode</c>, <c>set_target</c>, <c>adjust_target</c>, par les mêmes méthodes que l'écran
/// Thermostats. Un refus lève une exception, que le bus transforme en échec motivé.</item>
/// </list>
/// </remarks>
public sealed class ThermostatEntities(
    ThermostatService service,
    ThermostatEngine engine,
    IEntityRegistry registry,
    IEntityStateReader reader,
    IEntityStateWriter writer) : ICoreCommandHandler
{
    public const string IntegrationId = CoreIntegrations.Thermostats;
    public const double TargetStep = 0.5;

    private static readonly IReadOnlyList<string> Commandes =
        [StandardCommands.SetMode, StandardCommands.SetTarget, StandardCommands.AdjustTarget, StandardCommands.SetSchedule];

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly Dictionary<string, string> _entites = new(StringComparer.Ordinal);    // thermostat → entité
    private readonly Dictionary<string, string> _publies = new(StringComparer.Ordinal);    // entité → dernier état publié
    private string? _derniereDeclaration;

    /// <summary>Déclare les thermostats s'ils ont changé, puis publie les états qui ont changé.</summary>
    public async Task PublishAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var thermostats = service.List();
            await DeclarerAsync(thermostats, ct);

            foreach (var thermostat in thermostats)
            {
                if (!_entites.TryGetValue(thermostat.Id, out var entityId)) continue;
                if (engine.GetSnapshot(thermostat.Id) is not { } instantane) continue;

                var etat = Etat(thermostat, instantane);
                var json = etat.GetRawText();
                if (_publies.GetValueOrDefault(entityId) == json) continue;

                writer.Update(entityId, etat);
                _publies[entityId] = json;
            }
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<CommandOutcome> HandleAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct)
    {
        var id = entity.UniqueId;
        var args = command.Args is { ValueKind: JsonValueKind.Object } a ? a : default;
        var resultat = command.Command switch
        {
            StandardCommands.SetMode => await service.SetModeAsync(id, LireMode(args), ct),
            StandardCommands.SetTarget => await service.SetTargetAsync(id, LireConsigne(args), ct),
            StandardCommands.AdjustTarget => await service.AdjustTargetAsync(id, LirePas(args), ct),
            StandardCommands.SetSchedule => await service.SetScheduleActiveAsync(id, LireActif(args), ct),
            _ => throw new ArgumentException($"commande « {command.Command} » inconnue d'un thermostat"),
        };

        return resultat is null ? throw new ArgumentException($"thermostat « {id} » inconnu") : CommandOutcome.Accepted;
    }

    private async Task DeclarerAsync(IReadOnlyList<ThermostatSettings> thermostats, CancellationToken ct)
    {
        var appareils = thermostats.Select(Descripteur).ToList();
        var declaration = JsonSerializer.Serialize(appareils, ThermostatService.Json);
        if (declaration == _derniereDeclaration) return;

        await registry.RegisterAsync(IntegrationId, appareils, ct);
        _entites.Clear();
        foreach (var thermostat in thermostats)
        {
            if (await registry.FindByUniqueIdAsync(IntegrationId, thermostat.Id, ct) is not { } entite) continue;
            _entites[thermostat.Id] = entite.EntityId;
            if (entite.DisplayName != Nom(thermostat)) await registry.RenameAsync(entite.EntityId, Nom(thermostat), ct);
        }

        _derniereDeclaration = declaration;
    }

    private static string Nom(ThermostatSettings t) => t.Name is { Length: > 0 } n ? n : t.Id;

    private static DeviceDescriptor Descripteur(ThermostatSettings t) => new()
    {
        UniqueId = t.Id,
        Name = Nom(t),
        Model = "Thermostat",
        Entities =
        [
            new EntityDescriptor
            {
                UniqueId = t.Id,
                Kind = EntityKind.Climate,
                Unit = Units.Celsius,
                Commands = Commandes,
                Extra = new Dictionary<string, JsonElement>
                {
                    ["modes"] = JsonSerializer.SerializeToElement(
                        ThermostatLabels.Modes.Select(m => new { id = ModeId(m), label = ThermostatLabels.Mode(m) }), ThermostatService.Json),
                    ["min_target"] = JsonSerializer.SerializeToElement(ThermostatService.MinTarget),
                    ["max_target"] = JsonSerializer.SerializeToElement(ThermostatService.MaxTarget),
                    ["target_step"] = JsonSerializer.SerializeToElement(TargetStep),
                    ["radiators"] = JsonSerializer.SerializeToElement(t.SwitchEntityIds.Count),
                    // Écran de réglage du thermostat, pour le bouton « paramètres » d'un widget.
                    ["configuration"] = JsonSerializer.SerializeToElement($"/thermostats/{t.Id}"),
                },
            },
        ],
    };

    private JsonElement Etat(ThermostatSettings t, ThermostatSnapshot s)
    {
        var etat = new JsonObject
        {
            ["temperature"] = s.Temperature,
            ["humidity"] = Humidite(t.TemperatureEntityId),
            ["outdoor_temperature"] = s.OutdoorTemperature,
            // Moyenne prévue sur 2 h, quand l'anticipation météo est active et la prévision utilisable.
            ["outdoor_forecast"] = s.OutdoorForecast,
            ["target"] = s.Target,
            ["target_adjusted"] = t.TargetOverride is not null,
            ["mode"] = ModeId(s.Mode),
            ["schedule_active"] = t.ScheduleActive,
            ["heating"] = s.Heating,
            // Part de chauffe du cycle TPI (0 à 1) et ce qui sert à la montrer, comme la carte de DomoMud.
            ["power"] = Math.Round(s.Power, 3),
            ["cycle_seconds"] = t.Tpi.CycleSeconds,
            ["coasting_high"] = t.Tpi.CoastingHigh,
            ["coasting_low"] = t.Tpi.CoastingLow,
            ["window_open"] = s.InhibitionReasons.Contains(InhibitionReasons.FenetreOuverte),
            ["status"] = JsonSerializer.SerializeToNode(s.Status, ThermostatService.Json),
            ["status_label"] = ThermostatLabels.Status(s.Status),
            // En mode d'absence (le thermostat suit l'absence de la maison et elle est vide).
            ["away"] = s.Away,
        };
        return JsonSerializer.SerializeToElement(etat);
    }

    /// <summary>L'humidité mesurée par l'appareil de la sonde intérieure, s'il en publie une.</summary>
    private double? Humidite(string sondeId)
    {
        var sonde = registry.Loaded.FirstOrDefault(e => e.EntityId == sondeId);
        var humidite = sonde is null ? null
            : registry.Loaded.FirstOrDefault(e => e.DeviceId == sonde.DeviceId && e.Kind == EntityKind.Sensor && e.DeviceClass == DeviceClasses.Humidity);
        return humidite is not null && reader.Get(humidite.EntityId) is { } etat && etat.TryGetNumber(out var v) ? v : null;
    }

    private static string ModeId(ThermostatMode mode) => JsonSerializer.Serialize(mode, ThermostatService.Json).Trim('"');

    private static ThermostatMode LireMode(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
        {
            foreach (var mode in ThermostatLabels.Modes)
                if (ModeId(mode) == m.GetString()) return mode;
        }

        throw new ArgumentException($"attendu : {{\"mode\": {string.Join(" | ", ThermostatLabels.Modes.Select(x => $"\"{ModeId(x)}\""))}}}");
    }

    private static double? LireConsigne(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("temperature", out var t)
            ? t.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number => t.GetDouble(),
                _ => throw new ArgumentException("attendu : {\"temperature\": 19.5} ou {\"temperature\": null}"),
            }
            : throw new ArgumentException("attendu : {\"temperature\": 19.5} ou {\"temperature\": null}");

    private static bool LireActif(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("active", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? a.GetBoolean()
            : throw new ArgumentException("attendu : {\"active\": true}");

    private static double LirePas(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble()
            : throw new ArgumentException("attendu : {\"delta\": 0.5}");
}
