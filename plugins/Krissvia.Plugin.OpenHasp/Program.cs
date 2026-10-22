using Krissvia.Plugin.OpenHasp;
using Krissvia.Sdk;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné : mot de passe du broker), puis
// variables d'environnement (réglages de la page Services : Platines__0__Noeud…), puis ligne de commande.
// Racine = dossier de l'exécutable. Sortie en UTF-8 : le superviseur du cœur lit ce journal (page Services).
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var mqtt = builder.Configuration.GetSection("Mqtt").Get<MqttOptions>() ?? new MqttOptions();
mqtt.ClientIdPrefix = "krissvia-openhasp";
var reglages = new Reglages
{
    Platines = builder.Configuration.GetSection("Platines").Get<List<Platine>>() ?? [],
    Sondes = builder.Configuration.GetSection("Sondes").Get<List<SondeAffichee>>() ?? [],
    Affichage = builder.Configuration.GetSection("Affichage").Get<Affichage>() ?? new Affichage(),
};
var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "openhasp";
coeur.PluginVersion = typeof(OpenHaspPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

if (string.IsNullOrWhiteSpace(mqtt.Host) || coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : Mqtt:Host (page Services) et Krissvia:Url sont obligatoires.");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(mqtt);
builder.Services.AddSingleton(coeur);
builder.Services.AddSingleton(reglages);
builder.Services.AddSingleton<IMqttLink, MqttNetLink>();
builder.Services.AddSingleton<OpenHaspPlugin>();
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<OpenHaspPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();
// Lancé par la page Services : s'arrêter avec le cœur (Krissvia:ParentPid).
builder.Services.AddParentProcessWatch(coeur);

await builder.Build().RunAsync();
return 0;

/// <summary>Le broker des platines et la connexion au cœur, chacun avec sa propre reconnexion.</summary>
internal sealed class PluginWorker(OpenHaspPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunAsync(stoppingToken), coeur.RunAsync(stoppingToken));
}
