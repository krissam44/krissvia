using Krissvia.Contracts;
using Krissvia.Sdk;
using Krissvia.Sdk.Mqtt;

namespace Krissvia.Plugin.Teleinfo;

/// <summary>Le broker d'un côté, le cœur de l'autre, le traducteur au milieu. Le compteur ne reçoit aucun ordre.</summary>
internal sealed class TeleinfoPlugin(TeleinfoTranslator traducteur, IMqttLink mqtt, TimeProvider clock) : ICorePlugin, IMqttHandler
{
    private static readonly TimeSpan PeriodeSurveillance = TimeSpan.FromSeconds(5);

    /// <summary>Renseigné au démarrage : le client a besoin du plugin, et le plugin du client pour envoyer.</summary>
    public CoreClient? Coeur { get; set; }

    public Task RunMqttAsync(CancellationToken ct) => mqtt.RunAsync(traducteur.Abonnement, this, ct);

    /// <summary>Signale au cœur un compteur qui se tait, même si plus aucun message n'arrive pour le dire.</summary>
    public async Task SurveillerAsync(CancellationToken ct)
    {
        using var minuterie = new PeriodicTimer(PeriodeSurveillance, clock);
        try
        {
            while (await minuterie.WaitForNextTickAsync(ct)) await EnvoyerAsync(traducteur.Surveiller(clock.GetUtcNow()));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Le lien « mqtt » dit au cœur si le BROKER répond (pastille MQTT) ; la disponibilité dit, elle, si les entités
    // sont joignables — ce n'est pas la même chose (pont ou compteur muet, broker en ligne).
    public async Task ConnecteAsync()
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", true);
        await EnvoyerAsync(traducteur.MqttConnecte());
    }

    public async Task DeconnecteAsync()
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false);
        await EnvoyerAsync(traducteur.MqttDeconnecte());
    }

    public async Task ConnexionImpossibleAsync(string motif)
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false, motif);
    }

    public Task MessageAsync(string topic, string charge) =>
        EnvoyerAsync(traducteur.TraiterMessage(topic, charge, clock.GetUtcNow()));

    public IReadOnlyList<Message> Snapshot() => traducteur.Instantane();

    public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct) =>
        Task.FromResult(new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = "le compteur ne reçoit aucun ordre" });

    private async Task EnvoyerAsync(IReadOnlyList<Message> messages)
    {
        if (Coeur is not { } coeur) return;
        foreach (var message in messages) await coeur.SendAsync(message);
    }
}
