using Krissvia.Sdk;
using Microsoft.Extensions.Hosting;

namespace Krissvia.Plugin.MqttDiscovery;

/// <summary>Fait tourner les deux connexions, broker et cœur, chacune avec sa propre reconnexion.</summary>
internal sealed class PluginWorker(MqttDiscoveryPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunMqttAsync(stoppingToken), coeur.RunAsync(stoppingToken));
}
