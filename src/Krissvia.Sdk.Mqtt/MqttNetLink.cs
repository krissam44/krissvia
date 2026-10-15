using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Krissvia.Sdk.Mqtt;

public sealed class MqttOptions
{
    /// <summary>Adresse IP de préférence : la résolution DNS échoue parfois en silence sous systemd (vécu sur DomoMud).</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Préfixe de l'identifiant client, pour reconnaître le plugin dans les journaux du broker.</summary>
    public string ClientIdPrefix { get; set; } = "krissvia";
}

/// <summary>Connexion MQTTnet avec reconnexion. Pas de session persistante : à chaque connexion, on relit les messages retenus.</summary>
public sealed partial class MqttNetLink(MqttOptions options, TimeProvider clock, ILogger<MqttNetLink> logger) : IMqttLink
{
    private static readonly TimeSpan[] Attentes =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private IMqttClient? _client;

    public async Task RunAsync(string abonnement, IMqttHandler handler, CancellationToken ct)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        _client = client;

        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await handler.MessageAsync(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? "");
            }
            catch (Exception ex)
            {
                MessageEnEchec(logger, e.ApplicationMessage.Topic, ex);
            }
        };

        var tentative = 0;
        while (!ct.IsCancellationRequested)
        {
            var coupure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task SurCoupure(MqttClientDisconnectedEventArgs _)
            {
                coupure.TrySetResult();
                return Task.CompletedTask;
            }

            try
            {
                var connexion = new MqttClientOptionsBuilder()
                    .WithTcpServer(options.Host, options.Port)
                    .WithClientId($"{options.ClientIdPrefix}-{Guid.NewGuid():N}"[..23])
                    .WithCleanSession()
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
                if (!string.IsNullOrEmpty(options.Username)) connexion.WithCredentials(options.Username, options.Password);

                await client.ConnectAsync(connexion.Build(), ct);
                client.DisconnectedAsync += SurCoupure;
                await client.SubscribeAsync(
                    new MqttTopicFilterBuilder().WithTopic(abonnement).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
                    ct);

                Connecte(logger, options.Host, options.Port, abonnement);
                tentative = 0;
                await handler.ConnecteAsync();

                await coupure.Task.WaitAsync(ct);
                Coupe(logger);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Injoignable(logger, options.Host, options.Port, ex.Message);
            }
            finally
            {
                client.DisconnectedAsync -= SurCoupure;
            }

            await handler.DeconnecteAsync();
            try
            {
                await Task.Delay(Attentes[Math.Min(tentative++, Attentes.Length - 1)], clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (client.IsConnected)
        {
            try
            {
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }
            catch (Exception)
            {
                // Arrêt au mieux.
            }
        }

        _client = null;
    }

    public async Task<bool> PublishAsync(string topic, string charge, CancellationToken ct)
    {
        if (_client is not { IsConnected: true } client) return false;

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(charge)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var resultat = await client.PublishAsync(message, ct);
            return resultat.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublicationImpossible(logger, topic, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecté au broker {Host}:{Port}, abonné à {Abonnement}.")]
    private static partial void Connecte(ILogger logger, string host, int port, string abonnement);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connexion au broker perdue.")]
    private static partial void Coupe(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker {Host}:{Port} injoignable : {Error}.")]
    private static partial void Injoignable(ILogger logger, string host, int port, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Traitement du message {Topic} en échec.")]
    private static partial void MessageEnEchec(ILogger logger, string topic, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publication sur {Topic} impossible : {Error}.")]
    private static partial void PublicationImpossible(ILogger logger, string topic, string error);
}
