using Krissvia.Plugin.Teleinfo;
using Krissvia.Sdk;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné : mot de passe du broker, jeton),
// puis variables d'environnement, puis ligne de commande. Racine = dossier de l'exécutable.
// Sortie en UTF-8 : le superviseur du cœur lit ce journal (page Services), accents compris.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var mqtt = builder.Configuration.GetSection("Mqtt").Get<MqttOptions>() ?? new MqttOptions();
mqtt.ClientIdPrefix = "krissvia-tic";
var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "teleinfo";
coeur.PluginVersion = typeof(TeleinfoPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

var tic = builder.Configuration.GetSection("Teleinfo");
var topic = tic["Topic"] is { Length: > 0 } t ? t : "energy/linky";
var deviceId = tic["DeviceId"] is { Length: > 0 } d ? d : "linky";
var nom = tic["Name"] is { Length: > 0 } n ? n : "Linky";
var silence = TimeSpan.FromSeconds(tic.GetValue("SilenceSeconds", 60));

if (string.IsNullOrWhiteSpace(mqtt.Host) || coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : Mqtt:Host et Krissvia:Url sont obligatoires (voir appsettings.json).");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(mqtt);
builder.Services.AddSingleton<IMqttLink, MqttNetLink>();
builder.Services.AddSingleton(new TeleinfoTranslator(topic, deviceId, nom, silence));
builder.Services.AddSingleton<TeleinfoPlugin>();
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<TeleinfoPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();

await builder.Build().RunAsync();
return 0;

/// <summary>Le broker, le cœur et la surveillance du silence, chacun sur sa boucle.</summary>
internal sealed class PluginWorker(TeleinfoPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunMqttAsync(stoppingToken), coeur.RunAsync(stoppingToken), plugin.SurveillerAsync(stoppingToken));
}
