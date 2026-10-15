using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Plugin.Zigbee2Mqtt.Mqtt;

namespace Krissvia.Plugin.Zigbee2Mqtt.Tests;

/// <summary>
/// Broker et Zigbee2MQTT simulés : publie <c>bridge/devices</c> et les états, et répond à un <c>/set</c> comme le
/// ferait un appareil, en republiant l'état résultant.
/// </summary>
internal sealed class SimulateurZ2m : IMqttLink
{
    private readonly TaskCompletionSource _connecte = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IMqttHandler? _handler;

    public bool BrokerJoignable { get; private set; } = true;

    /// <summary>Faux : l'appareil reçoit l'ordre mais ne republie rien (appareil endormi, hors de portée).</summary>
    public bool AppareilsRepondent { get; set; } = true;

    public ConcurrentQueue<(string Topic, string Charge)> Publications { get; } = new();

    public async Task RunAsync(string abonnement, IMqttHandler handler, CancellationToken ct)
    {
        Assert.Equal("zigbee2mqtt/#", abonnement);
        _handler = handler;
        await handler.ConnecteAsync();
        _connecte.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<bool> PublishAsync(string topic, string charge, CancellationToken ct)
    {
        if (!BrokerJoignable) return false;

        Publications.Enqueue((topic, charge));
        if (AppareilsRepondent && topic.EndsWith("/set", StringComparison.Ordinal))
            await PublierAsync(topic[..^"/set".Length], charge.Replace("\"TOGGLE\"", "\"ON\""));

        return true;
    }

    public async Task PublierAsync(string topic, string charge)
    {
        await _connecte.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _handler!.MessageAsync(topic, charge);
    }

    /// <summary>Zigbee2MQTT démarre : messages retenus, puis quelques mesures.</summary>
    public async Task DemarrerAsync()
    {
        await PublierAsync("zigbee2mqtt/bridge/state", """{"state":"online"}""");
        await PublierAsync("zigbee2mqtt/bridge/devices", ReseauZigbee.Appareils());
        await PublierAsync("zigbee2mqtt/Sonde salon", JsonSerializer.Serialize(new { temperature = 19.5, humidity = 48, battery = 87 }));
        await PublierAsync("zigbee2mqtt/Prise lave-linge", """{"state":"ON","power":1850.2,"energy":412.7}""");
    }

    public async Task CouperBrokerAsync()
    {
        BrokerJoignable = false;
        await _handler!.DeconnecteAsync();
    }
}
