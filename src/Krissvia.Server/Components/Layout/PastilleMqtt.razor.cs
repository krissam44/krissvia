using Krissvia.PluginHost;
using Krissvia.PluginHost.Services;

namespace Krissvia.Server.Components.Layout;

/// <summary>
/// La pastille MQTT : verte quand tous les plugins MQTT activés joignent le broker, rouge si l'un n'y arrive pas (le motif
/// est dans l'infobulle), orange pendant un démarrage, grise sans plugin MQTT. Suivie par les changements de lien, et
/// relue toutes les 3 s pour les démarrages, qui ne préviennent pas.
/// </summary>
public partial class PastilleMqtt
{
    [Inject] private PluginSupervisor Superviseur { get; set; } = default!;
    [Inject] private PluginHub Hub { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;

    private MqttOverview _etat = new(MqttHealth.NoPlugin, []);
    private Timer? _minuterie;

    private Color Couleur => _etat.Health switch
    {
        MqttHealth.Ok => Color.Success,
        MqttHealth.Problem => Color.Error,
        MqttHealth.Waiting => Color.Warning,
        _ => Color.Default,
    };

    private string Icone => _etat switch
    {
        { Health: MqttHealth.Problem } => Icons.Material.Filled.Error,
        { SharedSaved: false, Health: not MqttHealth.NoPlugin } => Icons.Material.Filled.Settings,
        _ => IconeEtat,
    };

    private string IconeEtat => _etat.Health switch
    {
        MqttHealth.Ok => Icons.Material.Filled.CheckCircle,
        MqttHealth.Problem => Icons.Material.Filled.Error,
        MqttHealth.Waiting => Icons.Material.Filled.HourglassTop,
        _ => Icons.Material.Outlined.Circle,
    };

    protected override void OnInitialized()
    {
        _etat = Superviseur.MqttStatus();
        Hub.LinksChanged += Relire;
        _minuterie = new Timer(_ => Relire(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void Relire()
    {
        var nouveau = Superviseur.MqttStatus();
        if (nouveau.Health == _etat.Health && nouveau.SharedSaved == _etat.SharedSaved && nouveau.Lines.SequenceEqual(_etat.Lines)) return;
        _etat = nouveau;
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task OuvrirAsync()
    {
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true };
        var dialogue = await Dialogues.ShowAsync<BrokerMqttDialog>("Broker MQTT", options);
        await dialogue.Result;
        Relire();
    }

    public void Dispose()
    {
        Hub.LinksChanged -= Relire;
        _minuterie?.Dispose();
    }
}
