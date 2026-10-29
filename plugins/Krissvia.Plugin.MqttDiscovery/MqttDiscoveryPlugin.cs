using Krissvia.Contracts;
using Krissvia.Plugin.MqttDiscovery.Discovery;
using Krissvia.Sdk;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.MqttDiscovery;

/// <summary>
/// Le broker d'un côté, le cœur de l'autre, le traducteur au milieu. Le plugin fournit des mesures et exécute des
/// ordres ; il ne décide jamais de rien (principe 2 de l'architecture).
/// </summary>
internal sealed partial class MqttDiscoveryPlugin : ICorePlugin, IMqttHandler
{
    private readonly DiscoveryTranslator _traducteur;
    private readonly IMqttLink _mqtt;
    private readonly ILogger _logger;

    /// <summary>
    /// Traduction ET envoi sous la même file : sans elle, un état parti juste après la déclaration (fil de réception)
    /// pourrait doubler la déclaration encore en route (fil du délai), et le cœur le jetterait comme d'entité inconnue.
    /// </summary>
    private readonly SemaphoreSlim _file = new(1, 1);
    private CancellationTokenSource? _attenteDeclaration;

    public MqttDiscoveryPlugin(DiscoveryTranslator traducteur, IMqttLink mqtt, ILogger<MqttDiscoveryPlugin> logger)
    {
        _traducteur = traducteur;
        _mqtt = mqtt;
        _logger = logger;
    }

    /// <summary>Renseigné au démarrage : le client a besoin du plugin, et le plugin du client pour envoyer.</summary>
    public CoreClient? Coeur { get; set; }

    /// <summary>
    /// Silence attendu après la dernière configuration avant de déclarer : les configurations retenues arrivent en rafale
    /// à la connexion, et chaque déclaration partielle ferait passer les autres entités pour disparues.
    /// </summary>
    internal static TimeSpan DelaiDeclaration { get; set; } = TimeSpan.FromSeconds(1);

    public Task RunMqttAsync(CancellationToken ct) => _mqtt.RunAsync(_traducteur.Abonnement, this, ct);

    // ── Broker ───────────────────────────────────────────────────────────────

    public async Task ConnecteAsync()
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", true);
        await TraduireEtEnvoyerAsync(_traducteur.MqttConnecte);
        // Les entités manuelles n'attendent aucune annonce ; le délai laisse aux configurations retenues le temps d'arriver.
        if (_traducteur.DeclarationEnAttente) PlanifierDeclaration();
        Abonner();
    }

    public async Task DeconnecteAsync()
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false);
        await TraduireEtEnvoyerAsync(_traducteur.MqttDeconnecte);
    }

    public async Task ConnexionImpossibleAsync(string motif)
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false, motif);
    }

    public async Task MessageAsync(string topic, string charge)
    {
        await TraduireEtEnvoyerAsync(() => _traducteur.TraiterMessage(topic, charge));
        if (_traducteur.DeclarationEnAttente) PlanifierDeclaration();
        Abonner();
    }

    /// <summary>
    /// Les topics d'état annoncés par les configurations. Hors du fil de réception : MQTTnet attend l'accusé de
    /// l'abonnement sur ce même fil.
    /// </summary>
    private void Abonner()
    {
        var topics = _traducteur.AbonnementsAFaire();
        if (topics.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var topic in topics)
            {
                if (await _mqtt.SubscribeAsync(topic, CancellationToken.None)) Abonne(_logger, topic);
                else _traducteur.AbonnementEchoue(topic);
            }
        });
    }

    private void PlanifierDeclaration()
    {
        var attente = new CancellationTokenSource();
        var precedente = Interlocked.Exchange(ref _attenteDeclaration, attente);
        precedente?.Cancel();
        precedente?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DelaiDeclaration, attente.Token);
            }
            catch (OperationCanceledException)
            {
                return;   // une autre configuration est arrivée : c'est son délai qui compte
            }

            await TraduireEtEnvoyerAsync(_traducteur.Declarer);
        });
    }

    private async Task TraduireEtEnvoyerAsync(Func<IReadOnlyList<Message>> traduire)
    {
        await _file.WaitAsync();
        try
        {
            var messages = traduire();
            if (Coeur is not { } coeur) return;
            foreach (var message in messages) await coeur.SendAsync(message);
        }
        finally
        {
            _file.Release();
        }
    }

    // ── Cœur ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<Message> Snapshot() => _traducteur.Instantane();

    /// <summary>
    /// Un appareil MQTT n'acquitte pas un ordre : <see cref="CommandOutcome.Accepted"/> veut dire « parti vers le
    /// broker », l'effet se constate sur l'état qu'il republie. Sans broker, l'ordre échoue tout de suite.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        var (publication, erreur) = _traducteur.Traduire(request);
        if (publication is null)
        {
            OrdreRefuse(_logger, request.Command, request.EntityUniqueId, erreur);
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = erreur };
        }

        if (!await _mqtt.PublishAsync(publication.Topic, publication.Charge, ct))
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Unreachable, Detail = "broker MQTT injoignable" };

        OrdrePublie(_logger, publication.Topic, publication.Charge);
        return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Accepted };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Abonné à {Topic}.")]
    private static partial void Abonne(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ordre {Command} sur « {UniqueId} » refusé : {Error}.")]
    private static partial void OrdreRefuse(ILogger logger, string command, string uniqueId, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ordre publié : {Topic} {Charge}.")]
    private static partial void OrdrePublie(ILogger logger, string topic, string charge);
}
