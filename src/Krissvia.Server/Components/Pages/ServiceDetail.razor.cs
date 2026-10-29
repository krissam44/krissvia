using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Routing;

namespace Krissvia.Server.Components.Pages;

/// <summary>Fiche d'un service : son état, son processus, ses relances et les dernières lignes de son journal.</summary>
public partial class ServiceDetail
{
    [Parameter] public string Id { get; set; } = "";

    [Inject] private PluginSupervisor Supervisor { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private PluginSettingsService Settings { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;

    private ServiceSnapshot? _service;
    private PluginManifest? _fiche;
    private SettingField? _schema;
    private JsonObject? _reglages;

    /// <summary>Les réglages tels qu'enregistrés (ou chargés) : ce qui diffère n'est pas encore pris en compte.</summary>
    private string? _reference;
    private bool _dejaEnregistres;
    private IReadOnlyList<string> _erreurs = [];
    private Timer? _minuterie;

    /// <summary>L'état du processus, en paires libellé / valeur (le dossier en police fixe).</summary>
    private IEnumerable<(string Libelle, string Valeur, bool Code)> Informations => _service is not { } s ? [] :
    [
        ("Version connectée", s.PluginVersion ?? "—", false),
        ("Processus", s.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—", false),
        ("Lancé le", s.StartedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "—", false),
        ("Relances", s.Restarts.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
        ("Dernière sortie", s.LastExit ?? "—", false),
        ("Dossier", s.Directory ?? "—", true),
    ];

    protected override void OnParametersSet()
    {
        _service = Supervisor.GetSnapshot(Id);

        // Le formulaire n'est chargé qu'à l'ouverture : la relecture toutes les 2 s effacerait la saisie en cours.
        _fiche = Supervisor.GetManifest(Id);
        _schema = SettingsSchema.Parse(_fiche?.Settings);
        _reglages = _fiche is null ? null : Settings.Get(_fiche);
        _reference = _reglages?.ToJsonString();
        _dejaEnregistres = Settings.HasSaved(Id);

        _minuterie ??= new Timer(_ => InvokeAsync(() =>
        {
            _service = Supervisor.GetSnapshot(Id);
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private async Task BasculerAsync(bool actif)
    {
        if (await Supervisor.SetEnabledAsync(Id, actif) is { } refus) Snackbar.Add(refus, Severity.Warning);
        _service = Supervisor.GetSnapshot(Id);
    }

    private async Task EnregistrerAsync()
    {
        if (_fiche is null || _reglages is null) return;

        _erreurs = await Settings.SaveAsync(_fiche, _reglages);
        if (_erreurs.Count > 0) return;

        _dejaEnregistres = true;
        _reference = _reglages.ToJsonString();
        if (_service?.Enabled == true)
        {
            if (await Supervisor.RestartAsync(Id) is { } refus) Snackbar.Add(refus, Severity.Warning);
            else Snackbar.Add("Réglages enregistrés, plugin redémarré.", Severity.Success);
        }
        else
        {
            Snackbar.Add("Réglages enregistrés : ils s'appliqueront au prochain lancement.", Severity.Success);
        }

        _service = Supervisor.GetSnapshot(Id);
    }

    private bool NonEnregistre => _reglages is not null && _reglages.ToJsonString() != _reference;

    /// <summary>Quitter la page perdrait la saisie : on demande, dans la page (pas de <c>confirm()</c> du navigateur).</summary>
    private async Task AvantNavigationAsync(LocationChangingContext contexte)
    {
        if (!NonEnregistre) return;
        var quitter = await Dialogues.ShowMessageBoxAsync("Modifications non enregistrées",
            "Les réglages modifiés sur cette page n'ont pas été enregistrés. Quitter la page quand même ?",
            yesText: "Quitter sans enregistrer", cancelText: "Rester");
        if (quitter != true) contexte.PreventNavigation();
    }

    private async Task RelancerAsync()
    {
        if (await Supervisor.RestartAsync(Id) is { } refus) Snackbar.Add(refus, Severity.Warning);
        else Snackbar.Add("Redémarrage demandé.", Severity.Info);
        _service = Supervisor.GetSnapshot(Id);
    }

    public void Dispose() => _minuterie?.Dispose();
}
