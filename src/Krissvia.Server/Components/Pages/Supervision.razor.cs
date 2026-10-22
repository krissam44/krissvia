using System.Globalization;
using Krissvia.Core.Notifications;
using Krissvia.Core.Supervision;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// La supervision d'un coup d'œil : Internet, plugins, présence et disque ; capteurs muets et piles faibles ; les
/// alertes en cours ; les réglages de ces familles d'alertes.
/// </summary>
public partial class Supervision : IDisposable
{
    private const string Vert = "#66bb6a", Orange = "#ffa726", Rouge = "#ef5350";
    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    [Inject] private SupervisionWatch Surveillance { get; set; } = default!;
    [Inject] private AlertService Alertes { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private sealed class Formulaire
    {
        public bool Plugins, Internet, Capteurs, Piles, Disque, Presence;
        public int PluginsMin, InternetMin, CapteursMin, PilesPct, DisqueMo;
    }

    private Formulaire _f = new();

    private SupervisionReport? Rapport => Surveillance.Last;

    private List<SupervisionStatus> EnPanne => Rapport?.Items.Where(i => i.Alerting).ToList() ?? [];

    protected override async Task OnInitializedAsync()
    {
        var r = await Alertes.GetSettingsAsync();
        _f = new Formulaire
        {
            Plugins = r.Plugins.Enabled, PluginsMin = r.Plugins.GraceMinutes,
            Internet = r.Internet.Enabled, InternetMin = r.Internet.GraceMinutes,
            Capteurs = r.Sensors.Enabled, CapteursMin = r.Sensors.SilentMinutes,
            Piles = r.Battery.Enabled, PilesPct = r.Battery.ThresholdPercent,
            Disque = r.Disk.Enabled, DisqueMo = r.Disk.MinFreeMegabytes,
            Presence = r.Presence.Enabled,
        };
        Surveillance.Changed += Rafraichir;
        Alertes.Changed += Rafraichir;
    }

    private void Rafraichir() => _ = InvokeAsync(StateHasChanged);

    private async Task EnregistrerAsync()
    {
        try
        {
            // Relus au moment d'enregistrer : la page Notifications écrit dans le même document (alerte gel).
            var r = await Alertes.GetSettingsAsync();
            await Alertes.SaveSettingsAsync(r with
            {
                Plugins = new GraceAlertSettings { Enabled = _f.Plugins, GraceMinutes = _f.PluginsMin },
                Internet = new GraceAlertSettings { Enabled = _f.Internet, GraceMinutes = _f.InternetMin },
                Sensors = new SensorAlertSettings { Enabled = _f.Capteurs, SilentMinutes = _f.CapteursMin },
                Battery = new BatteryAlertSettings { Enabled = _f.Piles, ThresholdPercent = _f.PilesPct },
                Disk = new DiskAlertSettings { Enabled = _f.Disque, MinFreeMegabytes = _f.DisqueMo },
                Presence = new PresenceAlertSettings { Enabled = _f.Presence },
            });
            Snackbar.Add("Réglages enregistrés ; pris en compte au prochain contrôle (dans la minute).", Severity.Success);
        }
        catch (NotificationValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    private static string Icone(SupervisionCategory c) => c switch
    {
        SupervisionCategory.Internet => Icons.Material.Filled.Public,
        SupervisionCategory.Plugin => Icons.Material.Filled.Extension,
        SupervisionCategory.Presence => Icons.Material.Filled.Home,
        SupervisionCategory.Disk => Icons.Material.Filled.Storage,
        SupervisionCategory.Battery => Icons.Material.Filled.BatteryAlert,
        _ => Icons.Material.Filled.Sensors,
    };

    /// <summary>Vert : en service. Orange : en panne, délai de grâce pas encore écoulé. Rouge : en panne, alerte partie.</summary>
    private static string Couleur(SupervisionStatus s) => !s.Item.Problem ? Vert : s.Alerting ? Rouge : Orange;

    private string Detail(SupervisionStatus s) => s switch
    {
        { Item.Problem: false } => s.Item.Detail,
        { Alerting: true, Since: { } d } => $"{s.Item.Detail} depuis {Heure(d)}",
        { Since: { } d } => $"{s.Item.Detail} depuis {Heure(d)} (alerte au bout de {s.Item.GraceMinutes} min)",
        _ => s.Item.Detail,
    };

    private static string Bilan(SupervisionReport r)
    {
        var capteurs = r.Items.Where(i => i.Item.Category == SupervisionCategory.Sensor).ToList();
        var piles = r.Items.Where(i => i.Item.Category == SupervisionCategory.Battery).ToList();
        if (capteurs.Count == 0 && piles.Count == 0) return r.SensorsInGrace ? "" : "Aucun capteur surveillé pour l'instant.";
        var muets = capteurs.Count(i => i.Item.Problem);
        var faibles = piles.Count(i => i.Item.Problem);
        return $"{capteurs.Count} capteur(s) surveillé(s), {(muets == 0 ? "tous émettent" : $"{muets} muet(s)")} ; "
             + $"{piles.Count} pile(s), {(faibles == 0 ? "aucune faible" : $"{faibles} faible(s)")}.";
    }

    private static string Envoi(SendOutcome o) => o switch
    {
        SendOutcome.Sent => "envoyée",
        SendOutcome.Disabled => "notifications désactivées",
        SendOutcome.NotConfigured => "ntfy non réglé",
        _ => "échec",
    };

    private string Heure(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone).ToString("ddd d MMM HH:mm", Francais);

    public void Dispose()
    {
        Surveillance.Changed -= Rafraichir;
        Alertes.Changed -= Rafraichir;
    }
}
