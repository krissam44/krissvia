using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krissvia.Core.Configuration;

namespace Krissvia.Climate.Alarm;

/// <summary>Un capteur surveillé : un <c>binary_sensor</c> d'ouverture (contact) ou de mouvement (détecteur).</summary>
public sealed record AlarmSensor(string EntityId, SensorRole Role = SensorRole.Immediate);

/// <summary>Les réglages de la centrale.</summary>
public sealed record AlarmSettings
{
    public bool Enabled { get; init; }
    public int ExitDelaySeconds { get; init; } = 30;
    public int EntryDelaySeconds { get; init; } = 20;

    // La sirène sonne par sonneries séparées de pauses, puis se tait, l'état restant « déclenchée » (ARCHITECTURE.md
    // §9). Pas de « 0 = illimité » : une sirène qui hurle tout l'après-midi dans un immeuble est un problème à elle seule.

    /// <summary>Durée d'une sonnerie, 10 à 30 s.</summary>
    public int RingSeconds { get; init; } = 30;

    /// <summary>Nombre de sonneries, 1 à 10.</summary>
    public int RingCount { get; init; } = 3;

    /// <summary>Pause entre deux sonneries, 5 à 120 s.</summary>
    public int PauseSeconds { get; init; } = 30;

    /// <summary>Au-delà de ce silence un capteur est en défaut à l'armement (pile morte ?). 0 désactive le contrôle.</summary>
    public int SilenceMinutes { get; init; } = 720;

    public IReadOnlyList<AlarmSensor> Sensors { get; init; } = [];

    /// <summary>Les sirènes : des entités qu'on allume et éteint (<c>turn_on</c> / <c>turn_off</c>).</summary>
    public IReadOnlyList<string> Sirens { get; init; } = [];

    public bool NotifyArming { get; init; }
    public bool NotifyTrigger { get; init; } = true;

    [JsonIgnore]
    public AlarmTimings Timings => new(ExitDelaySeconds, EntryDelaySeconds, RingSeconds, RingCount, PauseSeconds, NotifyArming, NotifyTrigger);
}

/// <summary>Une ligne du journal de l'alarme.</summary>
public sealed record AlarmJournalEntry(DateTimeOffset At, string Text);

/// <summary>Configuration ou code refusés, avec la raison à montrer telle quelle.</summary>
public sealed class AlarmValidationException(string message) : Exception(message);

/// <summary>
/// Ce que l'alarme enregistre : ses réglages, les codes des claviers logiciels (page, API), son dernier état — un
/// redémarrage du serveur ne doit pas la désarmer en silence — et son journal.
/// </summary>
/// <remarks>
/// Collection <c>alarme</c>, un document par sujet. Les codes ne sont jamais enregistrés en clair (<see cref="AlarmCodes"/>).
/// </remarks>
public sealed class AlarmService(IConfigurationStore store, TimeProvider clock)
{
    public const string Collection = "alarme";
    public const int JournalSize = 200;

    private const string Reglages = "reglages";
    private const string DocCodes = "codes";
    private const string DocEtat = "etat";
    private const string DocJournal = "journal";

    public static JsonSerializerOptions Json { get; } = CreerOptions();

    private sealed record EtatEnregistre(AlarmMode Mode, string? Sensor, DateTimeOffset? TriggeredAt);

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private bool _charge;
    private List<AlarmCode> _codes = [];
    private List<AlarmJournalEntry> _journal = [];

    public AlarmSettings Settings { get; private set; } = new();

    /// <summary>L'état enregistré au dernier changement (tel quel : <see cref="AlarmMachine.AfterRestart"/> l'interprète).</summary>
    public AlarmState SavedState { get; private set; }

    /// <summary>Les personnes qui ont un code, dans l'ordre alphabétique.</summary>
    public IReadOnlyList<(string Id, string Name)> CodeHolders =>
        [.. _codes.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => (c.Id, c.Name))];

    /// <summary>Les dernières lignes du journal, de la plus récente à la plus ancienne.</summary>
    public IReadOnlyList<AlarmJournalEntry> Journal
    {
        get { lock (_journal) return [.. Enumerable.Reverse(_journal)]; }
    }

    /// <summary>Les réglages ou les codes ont changé. Levé hors verrou.</summary>
    public event Action? Changed;

    /// <summary>Une ligne vient d'entrer au journal.</summary>
    public event Action<AlarmJournalEntry>? Journaled;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (_charge) return;
            foreach (var d in await store.ListAsync(Collection, ct))
            {
                switch (d.Id)
                {
                    case Reglages: Settings = JsonSerializer.Deserialize<AlarmSettings>(d.Json, Json) ?? new(); break;
                    case DocCodes: _codes = JsonSerializer.Deserialize<List<AlarmCode>>(d.Json, Json) ?? []; break;
                    case DocJournal: _journal = JsonSerializer.Deserialize<List<AlarmJournalEntry>>(d.Json, Json) ?? []; break;
                    case DocEtat when JsonSerializer.Deserialize<EtatEnregistre>(d.Json, Json) is { } e:
                        SavedState = new AlarmState(e.Mode, 0, e.TriggeredAt, e.Sensor);
                        break;
                }
            }

            _charge = true;
        }
        finally
        {
            _verrou.Release();
        }
    }

    public static void Validate(AlarmSettings r)
    {
        if (r.ExitDelaySeconds is < 0 or > 600) throw new AlarmValidationException("le délai de sortie va de 0 à 600 s.");
        if (r.EntryDelaySeconds is < 0 or > 600) throw new AlarmValidationException("le délai d'entrée va de 0 à 600 s.");
        if (r.RingSeconds is < 10 or > 30) throw new AlarmValidationException("une sonnerie dure de 10 à 30 s.");
        if (r.RingCount is < 1 or > 10) throw new AlarmValidationException("de 1 à 10 sonneries.");
        if (r.PauseSeconds is < 5 or > 120) throw new AlarmValidationException("la pause entre deux sonneries va de 5 à 120 s.");
        if (r.SilenceMinutes is < 0 or > 10080) throw new AlarmValidationException("le silence toléré va de 0 à 10 080 min (7 jours).");
        if (r.Sensors.Any(c => string.IsNullOrWhiteSpace(c.EntityId))) throw new AlarmValidationException("un capteur n'a pas d'entité.");
        if (r.Sensors.GroupBy(c => c.EntityId).FirstOrDefault(g => g.Count() > 1) is { } double_)
            throw new AlarmValidationException($"« {double_.Key} » est surveillé deux fois.");
        if (r.Sirens.Any(string.IsNullOrWhiteSpace)) throw new AlarmValidationException("une sirène n'a pas d'entité.");
        if (r.Enabled && r.Sensors.Count == 0) throw new AlarmValidationException("aucun capteur : l'alarme ne surveillerait rien.");
    }

    public async Task SaveSettingsAsync(AlarmSettings reglages, CancellationToken ct = default)
    {
        Validate(reglages);
        reglages = reglages with { Sirens = [.. reglages.Sirens.Distinct(StringComparer.Ordinal)] };
        await ModifierAsync(Reglages, () => { Settings = reglages; return Settings; }, ct);
    }

    /// <summary>Donne un code à quelqu'un, ou change le sien (même nom, sans tenir compte de la casse).</summary>
    public async Task SetCodeAsync(string nom, string code, CancellationToken ct = default)
    {
        nom = nom.Trim();
        if (nom.Length == 0) throw new AlarmValidationException("indiquez le nom de la personne.");
        if (!AlarmCodes.IsAcceptable(code))
            throw new AlarmValidationException($"un code fait de {AlarmCodes.MinimumLength} à {AlarmCodes.MaximumLength} chiffres.");

        await LoadAsync(ct);
        var autres = _codes.Where(c => !string.Equals(c.Name, nom, StringComparison.CurrentCultureIgnoreCase)).ToList();
        // Deux personnes, un même code : le journal ne saurait plus qui a désarmé.
        if (await Task.Run(() => AlarmCodes.Identify(code, autres), ct) is not null)
            throw new AlarmValidationException("ce code est déjà attribué à quelqu'un d'autre.");

        var existant = _codes.FirstOrDefault(c => string.Equals(c.Name, nom, StringComparison.CurrentCultureIgnoreCase));
        var nouveau = AlarmCodes.Create(existant?.Id ?? Guid.NewGuid().ToString("N"), nom, code);
        await ModifierAsync(DocCodes, () =>
        {
            _codes = [.. _codes.Where(c => !string.Equals(c.Name, nom, StringComparison.CurrentCultureIgnoreCase)), nouveau];
            return _codes;
        }, ct);
    }

    public async Task<bool> DeleteCodeAsync(string id, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        if (_codes.All(c => c.Id != id)) return false;
        await ModifierAsync(DocCodes, () => { _codes = [.. _codes.Where(c => c.Id != id)]; return _codes; }, ct);
        return true;
    }

    /// <summary>Le nom de celui dont c'est le code, ou null. Coûteux (PBKDF2) : hors du fil appelant.</summary>
    public Task<string?> IdentifyAsync(string code, CancellationToken ct = default)
    {
        var codes = _codes;
        return Task.Run(() => AlarmCodes.Identify(code, codes), ct);
    }

    /// <summary>Enregistre l'état ; seuls le mode, le capteur et l'heure du déclenchement comptent.</summary>
    public async Task SaveStateAsync(AlarmState etat, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            SavedState = etat;
            await store.SaveAsync(Collection, new ConfigurationDocument(DocEtat,
                JsonSerializer.Serialize(new EtatEnregistre(etat.Mode, etat.Sensor, etat.TriggeredAt), Json)), ct);
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task AppendJournalAsync(string texte, CancellationToken ct = default)
    {
        var ligne = new AlarmJournalEntry(clock.GetUtcNow(), texte);
        string json;
        lock (_journal)
        {
            _journal.Add(ligne);
            if (_journal.Count > JournalSize) _journal.RemoveRange(0, _journal.Count - JournalSize);
            json = JsonSerializer.Serialize(_journal, Json);
        }

        await _verrou.WaitAsync(ct);
        try
        {
            await store.SaveAsync(Collection, new ConfigurationDocument(DocJournal, json), ct);
        }
        finally
        {
            _verrou.Release();
        }

        Journaled?.Invoke(ligne);
    }

    private async Task ModifierAsync<T>(string document, Func<T> modifier, CancellationToken ct)
    {
        await LoadAsync(ct);
        await _verrou.WaitAsync(ct);
        try
        {
            var valeur = modifier();
            await store.SaveAsync(Collection, new ConfigurationDocument(document, JsonSerializer.Serialize(valeur, Json)), ct);
        }
        finally
        {
            _verrou.Release();
        }

        Changed?.Invoke();
    }

    private static JsonSerializerOptions CreerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
