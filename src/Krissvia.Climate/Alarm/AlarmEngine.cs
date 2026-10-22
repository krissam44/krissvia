using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.Alarm;

/// <summary>Le résultat d'une commande par code.</summary>
public enum AlarmCodeOutcome
{
    Done,

    /// <summary>Code inconnu.</summary>
    WrongCode,

    /// <summary>Trop de codes faux : clavier bloqué.</summary>
    Locked,

    /// <summary>L'alarme est désactivée dans ses réglages.</summary>
    Disabled,
}

/// <param name="User">Celui dont c'est le code, quand il est bon.</param>
/// <param name="LockedSeconds">Secondes de blocage restantes, quand le clavier est bloqué.</param>
public sealed record AlarmCodeResult(AlarmCodeOutcome Outcome, string? User = null, int LockedSeconds = 0);

/// <summary>
/// La centrale d'alarme : exécute <see cref="AlarmMachine"/> et fait tout ce qu'elle ignore volontairement — prélever
/// les défauts, piloter les sirènes, notifier, journaliser, enregistrer, publier l'entité <c>sensor.maison_alarme</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Tick, commandes et détections arrivent sur des fils différents : tout passe par le même verrou. Deux
/// transitions simultanées sur une machine à états produiraient exactement la divergence qu'elle existe pour éviter.</item>
/// <item>Sirènes et notifications partent hors du verrou, dans l'ordre (une file par sorte) : une sirène qui met dix
/// secondes à répondre ne doit pas figer le décompte, et un « arrêt » ne doit jamais doubler un « marche ».</item>
/// <item>Un ordre en échec n'emporte pas les suivants : ntfy injoignable, la sirène sonne quand même. L'échec va au
/// journal.</item>
/// <item>On ne réagit qu'aux FRONTS (repos → actif) : un détecteur qui réaffirme sa détection ne redéclenche pas, et le
/// premier message après un redémarrage n'est pas un front.</item>
/// </list>
/// </remarks>
public sealed class AlarmEngine(
    AlarmService service,
    IEntityRegistry registry,
    IEntityStateReader states,
    IEntityStateWriter writer,
    ICommandBus commands,
    NotificationService notifications,
    TimeProvider clock) : ICoreCommandHandler
{
    public const string IntegrationId = CoreIntegrations.Alarm;
    public const string UniqueId = "alarme";
    public const string Origin = "alarme";

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly Lock _verrouFiles = new();
    private Task _fileSirenes = Task.CompletedTask;
    private Task _fileNotifications = Task.CompletedTask;
    private AlarmLockState _blocage;
    private string? _entite;
    private string? _publie;

    public AlarmState State { get; private set; }

    /// <summary>Le résultat du dernier contrôle des défauts : « OK », ou la liste de ce qui bloquait.</summary>
    public string LastFaults { get; private set; } = AlarmFaults.None;

    /// <summary>L'état a changé (décompte compris). Levé hors verrou.</summary>
    public event Action? Changed;

    /// <summary>Restaure l'état enregistré et publie l'entité. Au démarrage, avant le premier tick.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await service.LoadAsync(ct);
        await _verrou.WaitAsync(ct);
        try
        {
            State = AlarmMachine.AfterRestart(service.SavedState);
            await PublierAsync(ct);
        }
        finally
        {
            _verrou.Release();
        }

        if (State.Mode != AlarmMode.Disarmed) await service.AppendJournalAsync($"Redémarrage du serveur : état repris ({AlarmLabels.Mode(State.Mode)})", ct);
    }

    /// <summary>Armer ou désarmer sans code : la page Alarme, réservée aux comptes connectés. C'est l'échappatoire d'un clavier bloqué.</summary>
    public Task CommandAsync(bool armer, string qui, CancellationToken ct = default) =>
        TraiterAsync(armer ? new AlarmEvent.Arm(qui) : new AlarmEvent.Disarm(qui), ct);

    /// <summary>
    /// Armer ou désarmer avec un code (API, clavier logiciel). Le blocage est vérifié AVANT l'identification : sans lui,
    /// dix mille codes à quatre chiffres se parcourraient en quelques minutes.
    /// </summary>
    public async Task<AlarmCodeResult> CommandWithCodeAsync(bool armer, string code, CancellationToken ct = default)
    {
        if (!service.Settings.Enabled) return new AlarmCodeResult(AlarmCodeOutcome.Disabled);

        var maintenant = clock.GetUtcNow();
        if (AlarmLockout.IsLocked(_blocage, maintenant))
            return new AlarmCodeResult(AlarmCodeOutcome.Locked, LockedSeconds: AlarmLockout.SecondsLeft(_blocage, maintenant));

        if (await service.IdentifyAsync(code, ct) is not { } qui)
        {
            _blocage = AlarmLockout.Failure(_blocage, maintenant);
            var bloque = AlarmLockout.IsLocked(_blocage, maintenant);
            await service.AppendJournalAsync(bloque ? "Code faux : clavier bloqué 60 s" : "Code faux", ct);
            return bloque
                ? new AlarmCodeResult(AlarmCodeOutcome.Locked, LockedSeconds: AlarmLockout.SecondsLeft(_blocage, maintenant))
                : new AlarmCodeResult(AlarmCodeOutcome.WrongCode);
        }

        _blocage = AlarmLockout.Success();
        await CommandAsync(armer, qui, ct);
        return new AlarmCodeResult(AlarmCodeOutcome.Done, qui);
    }

    /// <summary>
    /// Les commandes de l'entité (<c>arm</c>, <c>disarm</c>, avec <c>{"code": "…"}</c>) : la carte du tableau de bord
    /// en fait un clavier logiciel. Mêmes règles que l'API — un code faux compte pour le blocage — et la raison d'un
    /// refus revient telle quelle à l'écran (le bus la prend dans le message de l'exception).
    /// </summary>
    /// <remarks>
    /// Le code voyage dans les arguments de l'ordre : le bus ne les écrit nulle part (journal du serveur, route de
    /// diagnostic), ce qui doit le rester.
    /// </remarks>
    public async Task<CommandOutcome> HandleAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct)
    {
        var armer = command.Command switch
        {
            StandardCommands.Arm => true,
            StandardCommands.Disarm => false,
            _ => throw new ArgumentException($"commande « {command.Command} » inconnue de l'alarme"),
        };
        var code = command.Args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;
        if (string.IsNullOrEmpty(code)) throw new ArgumentException("code manquant");

        var r = await CommandWithCodeAsync(armer, code, ct);
        return r.Outcome switch
        {
            AlarmCodeOutcome.WrongCode => throw new ArgumentException("code incorrect"),
            AlarmCodeOutcome.Locked => throw new ArgumentException($"trop de codes faux : clavier bloqué {r.LockedSeconds} s"),
            AlarmCodeOutcome.Disabled => throw new ArgumentException("l'alarme est désactivée"),
            _ when armer && State.Mode == AlarmMode.Disarmed => throw new ArgumentException($"armement refusé : {LastFaults}"),
            _ => CommandOutcome.Accepted,
        };
    }

    /// <summary>Secondes de blocage restantes du clavier, 0 s'il est libre.</summary>
    public int LockedSeconds => AlarmLockout.SecondsLeft(_blocage, clock.GetUtcNow());

    /// <summary>Une seconde s'est écoulée. Rien à faire hors décompte et hors sonneries.</summary>
    public Task TickAsync(CancellationToken ct = default) =>
        State.Mode is AlarmMode.ExitDelay or AlarmMode.EntryDelay || State is { Mode: AlarmMode.Triggered, Ring: > 0 }
            ? TraiterAsync(new AlarmEvent.Second(), ct)
            : Task.CompletedTask;

    /// <summary>Un changement d'état du bus : un capteur surveillé qui passe du repos à actif est une détection.</summary>
    public Task OnStateChangedAsync(EntityStateChanged e, CancellationToken ct = default)
    {
        if (service.Settings.Sensors.FirstOrDefault(c => c.EntityId == e.EntityId) is not { Role: not SensorRole.FaultOnly } capteur)
            return Task.CompletedTask;
        if (!IsRisingEdge(e)) return Task.CompletedTask;

        return TraiterAsync(new AlarmEvent.Detection(Nom(e.EntityId), capteur.Role == SensorRole.Delayed), ct);
    }

    /// <summary>
    /// Repos → actif, les deux valeurs connues. Un état précédent inconnu (premier message, entité restaurée
    /// indisponible au redémarrage) n'est pas un front : sinon chaque redémarrage déclencherait sur le premier capteur
    /// bavard.
    /// </summary>
    public static bool IsRisingEdge(EntityStateChanged e) =>
        e.Previous is { } avant && avant.TryGetBool(out var etait) && !etait && e.Current.TryGetBool(out var est) && est;

    /// <summary>Les réglages ont changé : l'alarme désactivée se désarme (sirène coupée).</summary>
    public async Task OnSettingsChangedAsync(CancellationToken ct = default)
    {
        if (!service.Settings.Enabled && State.Mode != AlarmMode.Disarmed)
            await TraiterAsync(new AlarmEvent.Disarm("désactivation"), ct, memeDesactivee: true);
    }

    /// <summary>Les défauts du moment, comme les verrait un armement.</summary>
    public IReadOnlyList<string> CurrentFaults() => AlarmFaults.Check(
        service.Settings.Sensors.Select(Capteur), clock.GetUtcNow(), service.Settings.SilenceMinutes);

    /// <summary>Attend la fin des sirènes et notifications en cours (les tests).</summary>
    public Task WhenIdleAsync()
    {
        lock (_verrouFiles) return Task.WhenAll(_fileSirenes, _fileNotifications);
    }

    private async Task TraiterAsync(AlarmEvent evenement, CancellationToken ct, bool memeDesactivee = false)
    {
        bool change;
        await _verrou.WaitAsync(ct);
        try
        {
            if (!service.Settings.Enabled && !memeDesactivee) return;

            var avant = State;
            var (suivant, ordres) = AlarmMachine.Process(State, evenement, service.Settings.Timings, CurrentFaults(), clock.GetUtcNow());
            State = suivant;

            foreach (var ordre in ordres) await ExecuterAsync(ordre, ct);

            if (suivant.Mode != avant.Mode || suivant.Sensor != avant.Sensor || suivant.TriggeredAt != avant.TriggeredAt)
                await service.SaveStateAsync(suivant, ct);
            change = suivant != avant || ordres.Any(o => o.Type == AlarmOrderType.Faults);
            if (change) await PublierAsync(ct);
        }
        finally
        {
            _verrou.Release();
        }

        if (change) Changed?.Invoke();
    }

    private async Task ExecuterAsync(AlarmOrder ordre, CancellationToken ct)
    {
        switch (ordre.Type)
        {
            case AlarmOrderType.Faults:
                LastFaults = ordre.Value;
                break;
            case AlarmOrderType.Journal:
                await service.AppendJournalAsync(ordre.Value, ct);
                break;
            case AlarmOrderType.Siren:
                var sirenes = service.Settings.Sirens;
                var marche = ordre.Value == "1";
                lock (_verrouFiles) _fileSirenes = _fileSirenes.ContinueWith(_ => PiloterSirenesAsync(sirenes, marche), TaskScheduler.Default).Unwrap();
                break;
            case AlarmOrderType.Notification:
                var texte = ordre.Value;
                lock (_verrouFiles) _fileNotifications = _fileNotifications.ContinueWith(_ => NotifierAsync(texte), TaskScheduler.Default).Unwrap();
                break;
        }
    }

    private async Task PiloterSirenesAsync(IReadOnlyList<string> sirenes, bool marche)
    {
        var commande = marche ? StandardCommands.TurnOn : StandardCommands.TurnOff;
        foreach (var id in sirenes)
        {
            CommandOutcome issue;
            try
            {
                issue = await commands.SendAsync(new EntityCommand(id, commande) { Origin = Origin, Priority = CommandPriority.Safety });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await service.AppendJournalAsync($"Sirène {Nom(id)} : {commande} en échec ({ex.Message})");
                continue;
            }

            if (issue != CommandOutcome.Accepted) await service.AppendJournalAsync($"Sirène {Nom(id)} : {commande} {issue}");
        }
    }

    private async Task NotifierAsync(string texte)
    {
        try
        {
            var envoi = await notifications.SendAsync(new Notification("Alarme", texte, 5, ["rotating_light"]));
            if (!envoi.Sent) await service.AppendJournalAsync($"Notification non envoyée ({envoi.Outcome}) : {texte}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await service.AppendJournalAsync($"Notification en échec ({ex.Message}) : {texte}");
        }
    }

    private AlarmFaults.Sensor Capteur(AlarmSensor c)
    {
        var etat = states.Get(c.EntityId);
        bool? actif = etat is not null && etat.TryGetBool(out var b) ? b : null;
        var detecteur = registry.Loaded.FirstOrDefault(e => e.EntityId == c.EntityId)?.DeviceClass == DeviceClasses.Occupancy;
        return new AlarmFaults.Sensor(Nom(c.EntityId), actif, etat?.MeasuredAt, ActiveIsFault: !detecteur);
    }

    private string Nom(string entityId) => registry.Loaded.FirstOrDefault(e => e.EntityId == entityId)?.DisplayName.Trim() ?? entityId;

    private async Task PublierAsync(CancellationToken ct)
    {
        if (_entite is null)
        {
            await registry.RegisterAsync(IntegrationId,
            [
                new DeviceDescriptor
                {
                    UniqueId = UniqueId, Name = "Maison",
                    Entities =
                    [
                        new EntityDescriptor
                        {
                            UniqueId = UniqueId, Kind = EntityKind.Sensor, Name = "Alarme", DeviceClass = DeviceClasses.Alarm,
                            Commands = [StandardCommands.Arm, StandardCommands.Disarm],
                        },
                    ],
                },
            ], ct);
            _entite = (await registry.FindByUniqueIdAsync(IntegrationId, UniqueId, ct))?.EntityId;
        }

        if (_entite is not { } id) return;
        var valeur = Valeur(State, LastFaults);
        var json = valeur.GetRawText();
        if (json == _publie) return;
        writer.Update(id, valeur);
        _publie = json;
    }

    /// <summary>La valeur de l'entité : <c>state</c> en snake_case (disarmed, exit_delay, armed, entry_delay, triggered).</summary>
    public static JsonElement Valeur(AlarmState e, string defauts) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["state"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Mode.ToString()),
        ["label"] = AlarmMachine.Label(e),
        ["countdown"] = e.Countdown,
        ["ringing"] = e.Ringing,
        ["sensor"] = e.Sensor,
        ["triggered_at"] = e.TriggeredAt?.ToString("O"),
        ["faults"] = defauts,
    });
}

/// <summary>Les libellés français des états de l'alarme.</summary>
public static class AlarmLabels
{
    public static string Mode(AlarmMode mode) => mode switch
    {
        AlarmMode.ExitDelay => "sortie en cours",
        AlarmMode.Armed => "armée",
        AlarmMode.EntryDelay => "entrée en cours",
        AlarmMode.Triggered => "déclenchée",
        _ => "désarmée",
    };
}
