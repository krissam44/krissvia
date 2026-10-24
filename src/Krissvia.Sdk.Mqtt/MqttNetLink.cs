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
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            // Rien à joindre : on le dit (pastille rouge, avec quoi faire) et on attend d'être relancé avec un broker.
            SansBroker(logger);
            await SignalerAsync(handler, MotifSansBroker);
            await handler.DeconnecteAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Arrêt du plugin.
            }

            return;
        }

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

                // MQTTnet 5 ne lève pas d'exception sur un refus du broker (mot de passe faux…) : il rend le code.
                var resultat = await client.ConnectAsync(connexion.Build(), ct);
                if (resultat.ResultCode != MqttClientConnectResultCode.Success) throw new ConnexionRefuseeException(resultat.ResultCode);
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
                await SignalerAsync(handler, Motif(ex));
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

    internal const string MotifSansBroker = "aucun broker réglé (pastille MQTT de la barre du haut)";

    private static async Task SignalerAsync(IMqttHandler handler, string motif)
    {
        try
        {
            await handler.ConnexionImpossibleAsync(motif);
        }
        catch (Exception)
        {
            // Un plugin qui ne sait pas le dire ne doit pas arrêter les reconnexions.
        }
    }

    private sealed class ConnexionRefuseeException(MqttClientConnectResultCode code) : Exception($"connexion refusée par le broker ({code})")
    {
        public MqttClientConnectResultCode Code => code;
    }

    /// <summary>Le motif d'un échec, lisible : c'est lui qu'affiche l'infobulle de la pastille MQTT.</summary>
    internal static string Motif(Exception ex)
    {
        if (ex is ConnexionRefuseeException refus)
            return refus.Code switch
            {
                MqttClientConnectResultCode.BadUserNameOrPassword => "identifiant ou mot de passe refusé par le broker",
                MqttClientConnectResultCode.NotAuthorized => "connexion non autorisée par le broker",
                _ => refus.Message,
            };
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException) return "broker injoignable (adresse ou port)";
        return ex.Message;
    }

    public async Task<bool> SubscribeAsync(string filtre, CancellationToken ct)
    {
        if (_client is not { IsConnected: true } client) return false;

        try
        {
            await client.SubscribeAsync(
                new MqttTopicFilterBuilder().WithTopic(filtre).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
                ct);
            Abonne(logger, filtre);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AbonnementImpossible(logger, filtre, ex.Message);
            return false;
        }
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Abonné à {Filtre}.")]
    private static partial void Abonne(ILogger logger, string filtre);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abonnement à {Filtre} impossible : {Error}.")]
    private static partial void AbonnementImpossible(ILogger logger, string filtre, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publication sur {Topic} impossible : {Error}.")]
    private static partial void PublicationImpossible(ILogger logger, string topic, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Aucun broker MQTT réglé : à saisir dans la pastille MQTT de la barre du haut.")]
    private static partial void SansBroker(ILogger logger);
}
