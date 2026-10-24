using Krissvia.Plugin.Ipp;
using Krissvia.Plugin.Ipp.Decouverte;
using Krissvia.Plugin.Ipp.Protocole;
using Krissvia.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné), puis variables d'environnement
// (réglages de la page Services), puis ligne de commande. Racine = dossier de l'exécutable.
// Sortie en UTF-8 : le superviseur du cœur lit ce journal (page Services), accents compris.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "ipp";
coeur.PluginVersion = typeof(IppPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
if (coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : Krissvia:Url.");
    return 2;
}

var section = builder.Configuration.GetSection("Ipp");
var manuelles = new List<Uri>();
foreach (var saisie in builder.Configuration.GetSection("Printers").GetChildren().Select(c => c["Url"]))
{
    if (IppAdresses.TryNormaliser(saisie, out var url)) manuelles.Add(url);
    else Console.Error.WriteLine($"Adresse d'imprimante ignorée : « {saisie} » (attendu : http://IP:631/ipp/print, ipp://…, ou l'adresse IP).");
}

var options = new OptionsIpp(manuelles, section.GetValue("Discovery", true),
    TimeSpan.FromSeconds(Math.Clamp(section.GetValue("PollSeconds", 300), 30, 3600)),
    Math.Clamp(section.GetValue("LowLevelPercent", 10), 1, 50));
if (manuelles.Count == 0 && !options.Decouverte)
    Console.Error.WriteLine("Ni découverte ni imprimante saisie : rien à relever (page Services).");

// Les imprimantes déjà lues, pour les déclarer même éteintes au prochain lancement : dossier de données posé par le
// cœur, ou à côté de l'exécutable s'il est lancé à la main.
var fichier = Path.Combine(coeur.DataDirectory is { Length: > 0 } donnees ? donnees : AppContext.BaseDirectory, "imprimantes.json");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIppClient, IppHttpClient>();
builder.Services.AddSingleton<IDecouverteIpp, DecouverteMdns>();
builder.Services.AddSingleton(sp => new IppPlugin(sp.GetRequiredService<IIppClient>(), sp.GetRequiredService<IDecouverteIpp>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<IppPlugin>>(), options, new MemoireImprimantes(fichier)));
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<IppPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();
// Lancé par la page Services : s'arrêter avec le cœur (Krissvia:ParentPid).
builder.Services.AddParentProcessWatch(coeur);

await builder.Build().RunAsync();
return 0;

/// <summary>Les relevés des imprimantes et la connexion au cœur, chacun sur sa boucle.</summary>
internal sealed class PluginWorker(IppPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunAsync(stoppingToken), coeur.RunAsync(stoppingToken));
}
