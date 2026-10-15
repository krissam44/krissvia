using Krissvia.Plugin.Zigbee2Mqtt;
using Krissvia.Sdk.Mqtt;
using Krissvia.Plugin.Zigbee2Mqtt.Z2m;
using Krissvia.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné : mots de passe, jeton),
// puis variables d'environnement (Mqtt__Host, Krissvia__Token…), puis ligne de commande.
// Racine = dossier de l'exécutable : un service lancé depuis un autre répertoire trouve quand même ses fichiers.
// Sortie en UTF-8 : le superviseur du cœur lit ce journal (page Services), accents compris.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var mqtt = builder.Configuration.GetSection("Mqtt").Get<MqttOptions>() ?? new MqttOptions();
mqtt.ClientIdPrefix = "krissvia-z2m";
var baseTopic = builder.Configuration["Mqtt:BaseTopic"] is { Length: > 0 } b ? b : "zigbee2mqtt";
var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "zigbee2mqtt";
coeur.PluginVersion = typeof(Zigbee2MqttPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

if (string.IsNullOrWhiteSpace(mqtt.Host) || coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : Mqtt:Host et Krissvia:Url sont obligatoires (voir appsettings.json).");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(mqtt);
builder.Services.AddSingleton(coeur);
builder.Services.AddSingleton<IMqttLink, MqttNetLink>();
builder.Services.AddSingleton(sp => new Zigbee2MqttTranslator(baseTopic, sp.GetRequiredService<ILogger<Zigbee2MqttTranslator>>()));
builder.Services.AddSingleton<Zigbee2MqttPlugin>();
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<Zigbee2MqttPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();

await builder.Build().RunAsync();
return 0;
