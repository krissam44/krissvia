using Krissvia.Plugin.Shelly;
using Krissvia.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné), puis variables
// d'environnement, puis ligne de commande. Racine = dossier de l'exécutable.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "shelly";
coeur.PluginVersion = typeof(ShellyPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

var appareils = builder.Configuration.GetSection("Shelly:Devices").Get<List<ShellyDeviceConfig>>() ?? [];
var periode = TimeSpan.FromSeconds(Math.Max(1, builder.Configuration.GetValue("Shelly:PollSeconds", 2)));

if (appareils.Count == 0 || appareils.Any(a => string.IsNullOrWhiteSpace(a.Host)) || coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : Shelly:Devices (avec Host) et Krissvia:Url sont obligatoires (voir appsettings.json).");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IShellyHttp>(_ => new ShellyHttp(new HttpClient { Timeout = TimeSpan.FromSeconds(3) }));
builder.Services.AddSingleton(sp => new ShellyPlugin(appareils, sp.GetRequiredService<IShellyHttp>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ShellyPlugin>>(), periode));
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<ShellyPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();

await builder.Build().RunAsync();
return 0;

/// <summary>L'interrogation des appareils et la connexion au cœur, chacune sur sa boucle.</summary>
internal sealed class PluginWorker(ShellyPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunAsync(stoppingToken), coeur.RunAsync(stoppingToken));
}
