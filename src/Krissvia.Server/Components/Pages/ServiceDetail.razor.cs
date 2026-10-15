using System.Text.Json.Nodes;

namespace Krissvia.Server.Components.Pages;

/// <summary>Fiche d'un service : son état, son processus, ses relances et les dernières lignes de son journal.</summary>
public partial class ServiceDetail
{
    [Parameter] public string Id { get; set; } = "";

    [Inject] private PluginSupervisor Supervisor { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private PluginSettingsService Settings { get; set; } = default!;

    private ServiceSnapshot? _service;
    private PluginManifest? _fiche;
    private SettingField? _schema;
    private JsonObject? _reglages;
    private bool _dejaEnregistres;
    private IReadOnlyList<string> _erreurs = [];
    private Timer? _minuterie;

    protected override void OnParametersSet()
    {
        _service = Supervisor.GetSnapshot(Id);

        // Le formulaire n'est chargé qu'à l'ouverture : la relecture toutes les 2 s effacerait la saisie en cours.
        _fiche = Supervisor.GetManifest(Id);
        _schema = SettingsSchema.Parse(_fiche?.Settings);
        _reglages = _fiche is null ? null : Settings.Get(_fiche);
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

    private async Task RelancerAsync()
    {
        if (await Supervisor.RestartAsync(Id) is { } refus) Snackbar.Add(refus, Severity.Warning);
        else Snackbar.Add("Redémarrage demandé.", Severity.Info);
        _service = Supervisor.GetSnapshot(Id);
    }

    public void Dispose() => _minuterie?.Dispose();
}
