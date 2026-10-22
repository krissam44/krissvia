using System.Globalization;
using Krissvia.Climate.Alarm;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Microsoft.AspNetCore.Components.Authorization;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// L'alarme : son état et les boutons Armer / Désarmer, le journal, le rôle de chaque capteur, les réglages et les
/// codes des claviers logiciels.
/// </summary>
public partial class Alarme : IDisposable
{
    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");
    private const string Aucun = "";

    [Inject] private AlarmService Service { get; set; } = default!;
    [Inject] private AlarmEngine Centrale { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private IEntityStateReader Etats { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState> Authentification { get; set; } = default!;

    private List<RegisteredEntity> _capteurs = [];
    private List<RegisteredEntity> _actionneurs = [];
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private bool _actif;
    private int _sortie;
    private int _entree;
    private int _sonnerie;
    private int _sonneries;
    private int _pause;
    private int _silence;
    private IReadOnlyCollection<string> _sirenes = [];
    private bool _notifArmement;
    private bool _notifDeclenchement;
    private string? _nomCode;
    private string? _code;
    private string? _erreur;

    private AlarmState Etat => Centrale.State;
    private IReadOnlyList<string> Defauts { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        var entites = await Registre.ListAsync();
        var r = Service.Settings;
        var surveilles = r.Sensors.ToDictionary(c => c.EntityId, c => c.Role.ToString(), StringComparer.Ordinal);

        // Les capteurs d'ouverture et les détecteurs ; un capteur surveillé reste listé même s'il a changé de classe.
        _capteurs = [.. entites.Where(e => e is { Kind: EntityKind.BinarySensor, Missing: false }
                                           && (e.DeviceClass is DeviceClasses.Opening or DeviceClasses.Occupancy || surveilles.ContainsKey(e.EntityId)))
                               .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
        foreach (var e in _capteurs) _roles[e.EntityId] = surveilles.GetValueOrDefault(e.EntityId, Aucun);

        _actionneurs = [.. entites.Where(e => e.Commands.Contains(StandardCommands.TurnOn) && e.Commands.Contains(StandardCommands.TurnOff) && !e.Missing)
                                  .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

        (_actif, _sortie, _entree, _silence) = (r.Enabled, r.ExitDelaySeconds, r.EntryDelaySeconds, r.SilenceMinutes);
        (_sonnerie, _sonneries, _pause) = (r.RingSeconds, r.RingCount, r.PauseSeconds);
        (_sirenes, _notifArmement, _notifDeclenchement) = (r.Sirens.ToList(), r.NotifyArming, r.NotifyTrigger);

        Defauts = Centrale.CurrentFaults();
        Centrale.Changed += Rafraichir;
        Service.Journaled += RafraichirJournal;
    }

    private void Rafraichir() => _ = InvokeAsync(() =>
    {
        Defauts = Centrale.CurrentFaults();
        StateHasChanged();
    });

    private void RafraichirJournal(AlarmJournalEntry _) => Rafraichir();

    private string Titre => Etat.Mode switch
    {
        AlarmMode.ExitDelay => $"Armement dans {Etat.Countdown} s",
        AlarmMode.Armed => "Armée",
        AlarmMode.EntryDelay => $"Entrée : {Etat.Countdown} s pour désarmer",
        AlarmMode.Triggered => "Alarme déclenchée",
        _ => "Désarmée",
    };

    private string SousTitre => Etat.Mode switch
    {
        AlarmMode.Triggered => $"{Etat.Sensor}, {Heure(Etat.TriggeredAt ?? Horloge.GetUtcNow())}{Sonnerie}",
        AlarmMode.EntryDelay => Etat.Sensor ?? "",
        AlarmMode.ExitDelay => "Sortez et refermez derrière vous : les ouvertures sont contrôlées à la fin du décompte.",
        _ => $"{Service.Settings.Sensors.Count} capteur(s) surveillé(s)",
    };

    private string Sonnerie => Etat switch
    {
        { Ring: 0 } => " — sonneries finies",
        { Ringing: true } => $" — sonnerie {Etat.Ring} sur {Service.Settings.RingCount}",
        _ => $" — pause, sonnerie {Etat.Ring + 1} dans {Etat.Countdown} s",
    };

    private string Icone => Etat.Mode switch
    {
        AlarmMode.Disarmed => Icons.Material.Filled.LockOpen,
        AlarmMode.Triggered => Icons.Material.Filled.NotificationImportant,
        AlarmMode.ExitDelay or AlarmMode.EntryDelay => Icons.Material.Filled.Timer,
        _ => Icons.Material.Filled.Lock,
    };

    private string Couleur => Etat.Mode switch
    {
        AlarmMode.Disarmed => "#66bb6a",
        AlarmMode.Triggered => "#e53935",
        AlarmMode.ExitDelay or AlarmMode.EntryDelay => "#ffa726",
        _ => "#1e88e5",
    };

    private string EtatCapteur(RegisteredEntity e)
    {
        if (Etats.Get(e.EntityId) is not { } s || !s.TryGetBool(out var actif)) return "sans nouvelles";
        var libelle = e.DeviceClass == DeviceClasses.Occupancy ? (actif ? "détection" : "calme") : (actif ? "ouvert" : "fermé");
        return $"{libelle}, {Heure(s.MeasuredAt)}";
    }

    private string Nom(string id) => _actionneurs.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id;

    private async Task CommanderAsync(bool armer)
    {
        var qui = (await Authentification).User.Identity?.Name ?? "interface";
        await Centrale.CommandAsync(armer, qui);
        if (armer && Etat.Mode == AlarmMode.Disarmed) Snackbar.Add($"Armement refusé : {Centrale.LastFaults}", Severity.Warning);
    }

    private async Task EnregistrerAsync()
    {
        _erreur = null;
        var reglages = Service.Settings with
        {
            Enabled = _actif,
            ExitDelaySeconds = _sortie,
            EntryDelaySeconds = _entree,
            RingSeconds = _sonnerie,
            RingCount = _sonneries,
            PauseSeconds = _pause,
            SilenceMinutes = _silence,
            Sirens = [.. _sirenes],
            NotifyArming = _notifArmement,
            NotifyTrigger = _notifDeclenchement,
            Sensors = [.. _roles.Where(kv => kv.Value != Aucun).Select(kv => new AlarmSensor(kv.Key, Enum.Parse<SensorRole>(kv.Value)))],
        };

        try
        {
            await Service.SaveSettingsAsync(reglages);
            Defauts = Centrale.CurrentFaults();
            Snackbar.Add("Réglages de l'alarme enregistrés.", Severity.Success);
        }
        catch (AlarmValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private async Task DonnerCodeAsync()
    {
        _erreur = null;
        try
        {
            await Service.SetCodeAsync(_nomCode ?? "", _code ?? "");
            Snackbar.Add($"Code de {_nomCode!.Trim()} enregistré.", Severity.Success);
            (_nomCode, _code) = (null, null);
        }
        catch (AlarmValidationException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
    }

    private async Task RetirerCodeAsync(string id, string nom)
    {
        if (await Service.DeleteCodeAsync(id)) Snackbar.Add($"Code de {nom} retiré.", Severity.Info);
    }

    private string Heure(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone).ToString("ddd d MMM HH:mm:ss", Francais);

    public void Dispose()
    {
        Centrale.Changed -= Rafraichir;
        Service.Journaled -= RafraichirJournal;
    }
}
