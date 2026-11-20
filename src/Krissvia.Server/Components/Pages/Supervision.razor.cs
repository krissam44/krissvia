using System.Globalization;
using Krissvia.Core.Notifications;
using Krissvia.Core.Supervision;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// La supervision d'un coup d'œil : une pastille par famille, puis des onglets — ce qui est à traiter, tout l'état
/// famille par famille (repliée), les alertes en cours, les réglages de ces familles d'alertes.
/// </summary>
public partial class Supervision : IDisposable
{
    private const string Vert = "#66bb6a", Orange = "#ffa726", Rouge = "#ef5350", Gris = "#9e9e9e";
    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    [Inject] private SupervisionWatch Surveillance { get; set; } = default!;
    [Inject] private AlertService Alertes { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private sealed class Formulaire
    {
        public bool Plugins, Internet, IpPublique, Capteurs, Piles, Disque, Presence, Delestage, Problemes, References;
        public int PluginsMin, InternetMin, CapteursMin, PilesPct, DisqueMo, ReferencesMin;
    }

    private Formulaire _f = new();

    private SupervisionReport? Rapport => Surveillance.Last;

    /// <summary>
    /// Une famille de points, telle que la page la range. Le constat en compte des dizaines (une machine virtuelle
    /// Proxmox, un capteur, une pile : un point chacun) : tout montrer d'un bloc noyait les quelques pannes.
    /// </summary>
    private sealed record Famille(string Nom, string Icone, SupervisionCategory[] Categories, string Vide);

    private static readonly Famille[] Familles =
    [
        new("Plugins", Icons.Material.Filled.Extension, [SupervisionCategory.Plugin], "Aucun plugin activé (page Services)."),
        new("Connexions", Icons.Material.Filled.Public, [SupervisionCategory.Internet], "Aucune entité de connectivité (box, platines, ponts)."),
        new("Système", Icons.Material.Filled.Storage, [SupervisionCategory.Presence, SupervisionCategory.Disk], "Ni détection de présence ni contrôle du disque."),
        new("Signalés par les plugins", Icons.Material.Filled.ReportProblem, [SupervisionCategory.Problem], "Aucun plugin ne signale de problèmes (sous-tension, machine arrêtée…)."),
        new("Capteurs", Icons.Material.Filled.Sensors, [SupervisionCategory.Sensor], "Aucun capteur surveillé pour l'instant."),
        new("Piles", Icons.Material.Filled.BatteryStd, [SupervisionCategory.Battery], "Aucune pile surveillée."),
        new("Références", Icons.Material.Filled.Link, [SupervisionCategory.Reference], "Aucune entité visée par un thermostat, une programmation, le délestage, l'alarme ou un plugin."),
    ];

    private static Famille FamilleDe(SupervisionCategory c) => Familles.First(f => f.Categories.Contains(c));

    private List<SupervisionStatus> Points(Famille f) => Rapport?.Items.Where(i => f.Categories.Contains(i.Item.Category)).ToList() ?? [];

    /// <summary>Ce qui est en panne, alerte partie d'abord, dans l'ordre des familles.</summary>
    private List<SupervisionStatus> Pannes => Rapport?.Items
        .Where(i => i.Item.Problem)
        .OrderByDescending(i => i.Alerting)
        .ThenBy(i => Array.IndexOf(Familles, FamilleDe(i.Item.Category)))
        .ThenBy(i => i.Item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList() ?? [];

    private int _onglet;
    private readonly HashSet<string> _ouvertes = [];

    private string Verdict
    {
        get
        {
            var pannes = Pannes;
            if (pannes.Count == 0) return "Tout fonctionne";
            var alertes = pannes.Count(p => p.Alerting);
            return alertes == pannes.Count ? $"{pannes.Count} point(s) en panne"
                 : alertes == 0 ? $"{pannes.Count} point(s) en panne, alerte pas encore partie"
                 : $"{pannes.Count} point(s) en panne, dont {alertes} signalé(s)";
        }
    }

    /// <summary>Une pastille de l'en-tête : « 10 OK » ou « 6 en panne / 63 ».</summary>
    private static string Pastille(List<SupervisionStatus> points)
    {
        var pannes = points.Count(p => p.Item.Problem);
        return pannes == 0 ? $"{points.Count} OK" : $"{pannes} en panne / {points.Count}";
    }

    private static string Compte(List<SupervisionStatus> points)
    {
        if (points.Count == 0) return "rien de surveillé";
        var pannes = points.Count(p => p.Item.Problem);
        return pannes == 0 ? $"{points.Count} : tout va bien" : $"{pannes} en panne sur {points.Count}";
    }

    /// <summary>Clic sur une pastille : l'onglet « Tout l'état », avec cette famille dépliée.</summary>
    private void Ouvrir(Famille f)
    {
        _ouvertes.Add(f.Nom);
        _onglet = 1;
    }

    private void Basculer(string famille, bool ouverte)
    {
        if (ouverte) _ouvertes.Add(famille);
        else _ouvertes.Remove(famille);
    }

    protected override async Task OnInitializedAsync()
    {
        var r = await Alertes.GetSettingsAsync();
        _f = new Formulaire
        {
            Plugins = r.Plugins.Enabled, PluginsMin = r.Plugins.GraceMinutes,
            Internet = r.Internet.Enabled, InternetMin = r.Internet.GraceMinutes,
            IpPublique = r.PublicIp.Enabled,
            Capteurs = r.Sensors.Enabled, CapteursMin = r.Sensors.SilentMinutes,
            Piles = r.Battery.Enabled, PilesPct = r.Battery.ThresholdPercent,
            Disque = r.Disk.Enabled, DisqueMo = r.Disk.MinFreeMegabytes,
            Presence = r.Presence.Enabled,
            Delestage = r.LoadShedding.Enabled,
            Problemes = r.Problems.Enabled,
            References = r.References.Enabled, ReferencesMin = r.References.GraceMinutes,
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
                PublicIp = new PublicIpAlertSettings { Enabled = _f.IpPublique },
                Sensors = new SensorAlertSettings { Enabled = _f.Capteurs, SilentMinutes = _f.CapteursMin },
                Battery = new BatteryAlertSettings { Enabled = _f.Piles, ThresholdPercent = _f.PilesPct },
                Disk = new DiskAlertSettings { Enabled = _f.Disque, MinFreeMegabytes = _f.DisqueMo },
                Presence = new PresenceAlertSettings { Enabled = _f.Presence },
                LoadShedding = new LoadSheddingAlertSettings { Enabled = _f.Delestage },
                Problems = new ProblemAlertSettings { Enabled = _f.Problemes },
                References = new GraceAlertSettings { Enabled = _f.References, GraceMinutes = _f.ReferencesMin },
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
        SupervisionCategory.Problem => Icons.Material.Filled.ReportProblem,
        SupervisionCategory.Reference => Icons.Material.Filled.LinkOff,
        _ => Icons.Material.Filled.Sensors,
    };

    /// <summary>
    /// L'icône d'un point : celle de sa catégorie, sauf un problème signalé qui n'est pas là — trente triangles
    /// d'avertissement verts pour trente machines qui vont bien se lisaient comme trente alertes.
    /// </summary>
    private static string Icone(SupervisionStatus s) =>
        s.Item is { Category: SupervisionCategory.Problem, Problem: false } ? Icons.Material.Filled.CheckCircle : Icone(s.Item.Category);

    /// <summary>Vert : en service. Orange : en panne, délai de grâce pas encore écoulé. Rouge : en panne, alerte partie.</summary>
    private static string Couleur(SupervisionStatus s) => !s.Item.Problem ? Vert : s.Alerting ? Rouge : Orange;

    private string Detail(SupervisionStatus s) => s switch
    {
        { Item.Problem: false } => s.Item.Detail,
        { Alerting: true, Since: { } d } => $"{s.Item.Detail} depuis {Heure(d)}",
        { Since: { } d } => $"{s.Item.Detail} depuis {Heure(d)} (alerte au bout de {s.Item.GraceMinutes} min)",
        _ => s.Item.Detail,
    };

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
