using Krissvia.Contracts;
using Krissvia.Sdk.Mqtt;
using Krissvia.Plugin.Zigbee2Mqtt.Z2m;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.Zigbee2Mqtt;

/// <summary>
/// Assemble les trois morceaux : le broker d'un côté, le cœur de l'autre, le traducteur au milieu. Le plugin
/// fournit des mesures et exécute des ordres ; il ne décide jamais de rien (principe 2 de l'architecture).
/// </summary>
internal sealed partial class Zigbee2MqttPlugin : ICorePlugin, IMqttHandler
{
    private readonly Zigbee2MqttTranslator _traducteur;
    private readonly IMqttLink _mqtt;
    private readonly ILogger _logger;

    public Zigbee2MqttPlugin(Zigbee2MqttTranslator traducteur, IMqttLink mqtt, ILogger<Zigbee2MqttPlugin> logger)
    {
        _traducteur = traducteur;
        _mqtt = mqtt;
        _logger = logger;
    }

    /// <summary>Renseigné au démarrage : le client a besoin du plugin, et le plugin du client pour envoyer.</summary>
    public CoreClient? Coeur { get; set; }

    public Task RunMqttAsync(CancellationToken ct) => _mqtt.RunAsync(_traducteur.Abonnement, this, ct);

    // ── Broker ───────────────────────────────────────────────────────────────

    // Le lien « mqtt » dit au cœur si le BROKER répond (pastille MQTT) ; la disponibilité dit, elle, si les entités
    // sont joignables — ce n'est pas la même chose (pont ou compteur muet, broker en ligne).
    public async Task ConnecteAsync()
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", true);
        await EnvoyerAsync(_traducteur.MqttConnecte());
    }

    public async Task DeconnecteAsync()
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false);
        await EnvoyerAsync(_traducteur.MqttDeconnecte());
    }

    public async Task ConnexionImpossibleAsync(string motif)
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false, motif);
    }

    public async Task MessageAsync(string topic, string charge)
    {
        await EnvoyerAsync(_traducteur.TraiterMessage(topic, charge));

        var lectures = _traducteur.LecturesAFaire();
        if (lectures.Count > 0) _ = Task.Run(() => DemanderAsync(lectures));
    }

    /// <summary>
    /// Hors du fil de réception (qui doit continuer à lire les réponses), et espacées : une rafale de /get sur un
    /// grand réseau encombrerait le maillage Zigbee.
    /// </summary>
    private async Task DemanderAsync(IReadOnlyList<PublicationMqtt> lectures)
    {
        foreach (var lecture in lectures)
        {
            if (!await _mqtt.PublishAsync(lecture.Topic, lecture.Charge, CancellationToken.None)) return;
            LectureDemandee(_logger, lecture.Topic, lecture.Charge);
            await Task.Delay(EspacementLectures);
        }
    }

    internal static TimeSpan EspacementLectures { get; set; } = TimeSpan.FromMilliseconds(200);

    private async Task EnvoyerAsync(IReadOnlyList<Message> messages)
    {
        if (Coeur is not { } coeur) return;
        foreach (var message in messages) await coeur.SendAsync(message);
    }

    // ── Cœur ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<Message> Snapshot() => _traducteur.Instantane();

    /// <summary>
    /// Zigbee2MQTT n'acquitte pas un <c>/set</c> : <see cref="CommandOutcome.Accepted"/> veut dire « parti vers
    /// le broker ». L'effet se constate sur l'état que l'appareil republie. Pas de file d'attente : sans broker,
    /// l'ordre échoue tout de suite (injoignable) au lieu d'être exécuté en retard — d'où un TTL sans objet ici.
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "État demandé : {Topic} {Charge}.")]
    private static partial void LectureDemandee(ILogger logger, string topic, string charge);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ordre {Command} sur « {UniqueId} » refusé : {Error}.")]
    private static partial void OrdreRefuse(ILogger logger, string command, string uniqueId, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ordre publié : {Topic} {Charge}.")]
    private static partial void OrdrePublie(ILogger logger, string topic, string charge);
}
