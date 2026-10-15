using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Registry;

namespace Krissvia.Climate.Thermostats;

/// <summary>Configuration refusée, avec la raison à montrer telle quelle à l'utilisateur.</summary>
public sealed class ThermostatValidationException(string message) : Exception(message);

/// <summary>
/// Configuration des thermostats : enregistrée dans la base, appliquée au moteur à chaque modification.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Toute référence est vérifiée au registre à l'enregistrement, avec sa famille : une sonde doit être un
/// <c>sensor</c>, un radiateur un <c>switch</c>. Le « Radiateur Chbre RdC » de DomoMud, qui visait un appareil
/// inexistant, est refusé dès la saisie au lieu de réguler dans le vide tout un hiver.</item>
/// <item>Choisir un mode reprend la main sur le planning et efface la consigne ajustée, comme le bouton de
/// DomoMud : l'API ne peut produire aucun état que l'utilisateur n'atteindrait pas lui-même.</item>
/// <item>Un document illisible au démarrage est écarté et signalé (<see cref="LoadErrors"/>) : un thermostat
/// abîmé ne doit pas empêcher les autres de chauffer.</item>
/// </list>
/// </remarks>
public sealed partial class ThermostatService(IConfigurationStore store, IEntityRegistry registry, ThermostatEngine engine)
{
    public const string Collection = "thermostats";
    public const double MinTarget = 5;
    public const double MaxTarget = 30;

    /// <summary>Format JSON des réglages, en base comme dans l'API : snake_case, énumérations en texte.</summary>
    public static JsonSerializerOptions Json { get; } = CreerOptions();

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private SortedDictionary<string, ThermostatSettings> _thermostats = new(StringComparer.Ordinal);

    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    /// <summary>
    /// Une configuration a changé (enregistrement, mode, consigne, planning, suppression). Levé une fois le verrou
    /// rendu : la boucle du moteur s'en sert pour recalculer tout de suite au lieu d'attendre son prochain passage.
    /// </summary>
    public event Action? Changed;

    public IReadOnlyList<ThermostatSettings> List() => _thermostats.Values.ToList();

    public ThermostatSettings? Get(string id) => _thermostats.GetValueOrDefault(id);

    /// <summary>Charge la configuration enregistrée et l'applique au moteur. Appelé au démarrage.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var charges = new SortedDictionary<string, ThermostatSettings>(StringComparer.Ordinal);
            var erreurs = new List<string>();
            foreach (var document in await store.ListAsync(Collection, ct))
            {
                try
                {
                    var t = JsonSerializer.Deserialize<ThermostatSettings>(document.Json, Json)
                            ?? throw new JsonException("document vide");
                    t.Validate();
                    charges[t.Id] = t;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    erreurs.Add($"{document.Id} : {ex.Message}");
                }
            }

            _thermostats = charges;
            LoadErrors = erreurs;
            await engine.ConfigureAsync(_thermostats.Values.ToList(), ct);
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Crée ou remplace un thermostat. Lève <see cref="ThermostatValidationException"/> si la configuration est inutilisable.</summary>
    public async Task<ThermostatSettings> SaveAsync(ThermostatSettings thermostat, CancellationToken ct = default)
    {
        if (thermostat.Id is null || !IdentifiantValide().IsMatch(thermostat.Id))
            throw new ThermostatValidationException("identifiant : minuscules, chiffres, « _ » et « - », 64 caractères au plus.");

        try
        {
            thermostat.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ThermostatValidationException(ex.Message);
        }

        VerifierConsigne(thermostat.TargetOverride);
        await VerifierEntiteAsync(thermostat.TemperatureEntityId, EntityKind.Sensor, "sonde intérieure", ct);
        foreach (var radiateur in thermostat.SwitchEntityIds)
            await VerifierEntiteAsync(radiateur, EntityKind.Switch, "radiateur", ct);
        if (thermostat.OutdoorTemperatureEntityId is { } exterieure)
            await VerifierEntiteAsync(exterieure, EntityKind.Sensor, "sonde extérieure", ct);
        if (thermostat.OpeningEntityId is { } ouverture)
            await VerifierEntiteAsync(ouverture, EntityKind.BinarySensor, "capteur d'ouverture", ct);

        return await ModifierAsync(thermostat.Id, _ => thermostat, creer: true, ct)
               ?? throw new InvalidOperationException("Enregistrement impossible.");
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (!_thermostats.ContainsKey(id)) return false;

            await store.DeleteAsync(Collection, id, ct);
            _thermostats.Remove(id);
            await engine.ConfigureAsync(_thermostats.Values.ToList(), ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Choisit un mode : le planning est désactivé et la consigne ajustée effacée. Null si inconnu.</summary>
    public Task<ThermostatSettings?> SetModeAsync(string id, ThermostatMode mode, CancellationToken ct = default) =>
        ModifierAsync(id, t => t with { ManualMode = mode, ScheduleActive = false, TargetOverride = null }, creer: false, ct);

    /// <summary>
    /// Ajuste la consigne (null : revient à celle du mode). Null si inconnu. Refusée sur un thermostat arrêté :
    /// Off est ferme, une consigne acceptée sans effet laisserait croire qu'il va chauffer.
    /// </summary>
    public Task<ThermostatSettings?> SetTargetAsync(string id, double? cible, CancellationToken ct = default)
    {
        VerifierConsigne(cible);
        return ModifierAsync(id, t =>
        {
            if (cible is not null && t.ManualMode == ThermostatMode.Off && !t.ScheduleActive)
                throw new ThermostatValidationException("thermostat arrêté (mode off) : choisir d'abord un mode, puis ajuster la consigne.");
            return t with { TargetOverride = cible is { } c ? Math.Round(c, 1) : null };
        }, creer: false, ct);
    }

    /// <summary>
    /// Remplace le planning, sans toucher au reste : ni au mode, ni à son activation, ni à la consigne ajustée. Un
    /// enregistrement complet depuis un écran ouvert depuis dix minutes écraserait un mode choisi entre-temps.
    /// Null si inconnu.
    /// </summary>
    public Task<ThermostatSettings?> SetScheduleAsync(string id, WeeklySchedule planning, CancellationToken ct = default)
    {
        try
        {
            planning.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ThermostatValidationException(ex.Message);
        }

        return ModifierAsync(id, t => t with { Schedule = planning }, creer: false, ct);
    }

    /// <summary>Active ou suspend le planning ; la consigne ajustée est effacée. Null si inconnu.</summary>
    public Task<ThermostatSettings?> SetScheduleActiveAsync(string id, bool actif, CancellationToken ct = default) =>
        ModifierAsync(id, t => t with { ScheduleActive = actif, TargetOverride = null }, creer: false, ct);

    // ── Interne ──────────────────────────────────────────────────────────────

    /// <summary>Écrit d'abord en base, puis en mémoire, puis dans le moteur : un échec d'écriture ne change rien.</summary>
    private async Task<ThermostatSettings?> ModifierAsync(
        string id, Func<ThermostatSettings, ThermostatSettings> modification, bool creer, CancellationToken ct)
    {
        ThermostatSettings nouveau;
        await _verrou.WaitAsync(ct);
        try
        {
            var actuel = _thermostats.GetValueOrDefault(id);
            if (actuel is null && !creer) return null;

            nouveau = modification(actuel!);
            await store.SaveAsync(Collection, new ConfigurationDocument(id, JsonSerializer.Serialize(nouveau, Json)), ct);
            _thermostats[id] = nouveau;
            await engine.ConfigureAsync(_thermostats.Values.ToList(), ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
        return nouveau;
    }

    private async Task VerifierEntiteAsync(string entityId, EntityKind attendu, string role, CancellationToken ct)
    {
        var entite = await registry.GetAsync(entityId, ct);
        if (entite is null)
            throw new ThermostatValidationException($"{role} : l'entité « {entityId} » n'existe pas.");
        if (entite.Kind != attendu)
            throw new ThermostatValidationException($"{role} : « {entityId} » est un {entite.Kind}, pas un {attendu}.");
    }

    private static void VerifierConsigne(double? cible)
    {
        if (cible is { } c && (double.IsNaN(c) || c < MinTarget || c > MaxTarget))
            throw new ThermostatValidationException($"consigne : entre {MinTarget} et {MaxTarget} °C.");
    }

    private static JsonSerializerOptions CreerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Accents et guillemets français écrits tels quels : ce JSON se lit en base comme dans l'API.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdentifiantValide();
}
