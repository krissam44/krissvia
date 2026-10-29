using Krissvia.Plugin.MqttDiscovery;
using Krissvia.Plugin.MqttDiscovery.Discovery;
using Krissvia.Sdk;
using Krissvia.Sdk.Mqtt;
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
mqtt.ClientIdPrefix = "krissvia-disco";
var prefixe = builder.Configuration["Mqtt:DiscoveryPrefix"] is { Length: > 0 } p ? p.TrimEnd('/') : "homeassistant";
var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "mqtt-discovery";
coeur.PluginVersion = typeof(MqttDiscoveryPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

// Sans broker, le plugin démarre quand même : il le signale au cœur (pastille MQTT rouge) au lieu de redémarrer en boucle.
if (coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : Krissvia:Url est obligatoire.");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(mqtt);
builder.Services.AddSingleton(coeur);
builder.Services.AddSingleton<IMqttLink, MqttNetLink>();
// Entités manuelles (section Entites des réglages) : Entites__0__Topic… transmis par le cœur au lancement.
var manuelles = builder.Configuration.GetSection("Entites").Get<List<EntiteManuelle>>() ?? [];
builder.Services.AddSingleton(sp => new DiscoveryTranslator(prefixe, manuelles, sp.GetRequiredService<ILogger<DiscoveryTranslator>>()));
builder.Services.AddSingleton<MqttDiscoveryPlugin>();
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<MqttDiscoveryPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();
// Lancé par la page Services : s'arrêter avec le cœur (Krissvia:ParentPid).
builder.Services.AddParentProcessWatch(coeur);

await builder.Build().RunAsync();
return 0;
