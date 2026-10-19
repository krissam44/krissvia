using Krissvia.Plugin.Livebox;
using Krissvia.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné : mot de passe de la box), puis
// variables d'environnement (réglages de la page Services), puis ligne de commande. Racine = dossier de l'exécutable.
// Sortie en UTF-8 : le superviseur du cœur lit ce journal (page Services), accents compris.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "livebox";
coeur.PluginVersion = typeof(LiveboxPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

var box = builder.Configuration.GetSection("Livebox");
var hote = box["Host"];
var motDePasse = box["Password"];
var suivis = builder.Configuration.GetSection("Suivis").Get<List<TrackedDevice>>() ?? [];
var periode = TimeSpan.FromSeconds(Math.Max(15, box.GetValue("PollSeconds", 60)));
var grace = TimeSpan.FromMinutes(Math.Max(0, box.GetValue("GraceMinutes", 20)));
var supervision = box.GetValue("Supervision", true);
// Les appareils suivis se cochent dans la carte « Appareils du réseau » ; le plugin les garde dans son dossier de
// données (posé par le cœur), ou à côté de l'exécutable s'il est lancé à la main.
var suivisJson = Path.Combine(coeur.DataDirectory is { Length: > 0 } donnees ? donnees : AppContext.BaseDirectory, "suivis.json");

if (string.IsNullOrWhiteSpace(hote) || string.IsNullOrEmpty(motDePasse) || coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : adresse et mot de passe de la box (page Services), et Krissvia:Url.");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILiveboxApi>(_ => new SysbusClient(hote, box["Username"] is { Length: > 0 } u ? u : "admin", motDePasse));
builder.Services.AddSingleton(sp => new LiveboxPlugin(suivis, sp.GetRequiredService<ILiveboxApi>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LiveboxPlugin>>(), grace, periode, supervision,
    new TrackingStore(suivisJson)));
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<LiveboxPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();
// Lancé par la page Services : s'arrêter avec le cœur (Krissvia:ParentPid).
builder.Services.AddParentProcessWatch(coeur);

await builder.Build().RunAsync();
return 0;

/// <summary>Les relevés de la box et la connexion au cœur, chacun sur sa boucle.</summary>
internal sealed class PluginWorker(LiveboxPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunAsync(stoppingToken), coeur.RunAsync(stoppingToken));
}
