using System.Collections.Concurrent;
using Krissvia.Sdk.Mqtt;

namespace Krissvia.Plugin.MqttDiscovery.Tests;

/// <summary>
/// Un broker en mémoire qui, comme un vrai, ne livre un message QUE si le plugin s'est abonné à son topic, et rejoue les
/// messages retenus à l'abonnement. C'est ce qui prouve les abonnements dynamiques : sans eux, les états n'arrivent pas.
/// </summary>
internal sealed class SimulateurBroker : IMqttLink
{
    private readonly TaskCompletionSource _connecte = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, string> _retenus = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _abonnements = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _livraison = new(1, 1);
    private IMqttHandler? _handler;

    public ConcurrentQueue<(string Topic, string Charge)> Publications { get; } = new();

    public async Task RunAsync(string abonnement, IMqttHandler handler, CancellationToken ct)
    {
        _handler = handler;
        _abonnements[abonnement] = 0;
        await handler.ConnecteAsync();
        await RejouerAsync(abonnement);
        _connecte.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<bool> SubscribeAsync(string filtre, CancellationToken ct)
    {
        _abonnements[filtre] = 0;
        await RejouerAsync(filtre);
        return true;
    }

    public Task<bool> PublishAsync(string topic, string charge, CancellationToken ct)
    {
        Publications.Enqueue((topic, charge));
        return Task.FromResult(true);
    }

    /// <summary>Un message retenu AVANT que le plugin ne se connecte : le module tournait déjà.</summary>
    public void Retenir(string topic, string charge) => _retenus[topic] = charge;

    /// <summary>Ce que publierait un appareil : retenu ou non, livré seulement si un abonnement le couvre.</summary>
    public async Task PublierAsync(string topic, string charge, bool retenu = true)
    {
        if (retenu) _retenus[topic] = charge;
        await _connecte.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (_abonnements.Keys.Any(f => Couvre(f, topic))) await LivrerAsync(topic, charge);
    }

    private async Task RejouerAsync(string filtre)
    {
        foreach (var (topic, charge) in _retenus.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (Couvre(filtre, topic)) await LivrerAsync(topic, charge);
    }

    /// <summary>Un message à la fois, comme la boucle de réception de MQTTnet.</summary>
    private async Task LivrerAsync(string topic, string charge)
    {
        await _livraison.WaitAsync();
        try
        {
            await _handler!.MessageAsync(topic, charge);
        }
        finally
        {
            _livraison.Release();
        }
    }

    private static bool Couvre(string filtre, string topic) =>
        filtre.EndsWith("/#", StringComparison.Ordinal) ? topic.StartsWith(filtre[..^1], StringComparison.Ordinal) : filtre == topic;
}
