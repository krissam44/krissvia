using Krissvia.Plugin.Proxmox;
using Krissvia.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration : appsettings.json, puis appsettings.Local.json (jamais versionné : secret du jeton), puis variables
// d'environnement (réglages de la page Services), puis ligne de commande. Racine = dossier de l'exécutable.
// Sortie en UTF-8 : le superviseur du cœur lit ce journal (page Services), accents compris.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var coeur = builder.Configuration.GetSection("Krissvia").Get<CoreClientOptions>() ?? new CoreClientOptions();
if (string.IsNullOrEmpty(coeur.PluginId)) coeur.PluginId = "proxmox";
coeur.PluginVersion = typeof(ProxmoxPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

var pve = builder.Configuration.GetSection("Proxmox");
var hote = pve["Host"];
var jeton = pve["TokenId"];
var secret = pve["TokenSecret"];
var periode = TimeSpan.FromSeconds(Math.Max(10, pve.GetValue("PollSeconds", 30)));
// Surveillance et relance de chaque machine se règlent depuis sa carte ; le plugin les garde dans son dossier de
// données (posé par le cœur), ou à côté de l'exécutable s'il est lancé à la main.
var fichierPreferences = Path.Combine(coeur.DataDirectory is { Length: > 0 } donnees ? donnees : AppContext.BaseDirectory, "preferences.json");

if (string.IsNullOrWhiteSpace(hote) || string.IsNullOrWhiteSpace(jeton) || string.IsNullOrEmpty(secret) || coeur.Url is null)
{
    Console.Error.WriteLine("Configuration incomplète : adresse, identifiant et secret du jeton Proxmox (page Services), et Krissvia:Url.");
    return 2;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProxmoxApi>(_ => new ProxmoxHttpClient(hote, pve.GetValue("Port", 8006), jeton, secret, pve.GetValue("VerifyTls", false)));
builder.Services.AddSingleton(sp => new ProxmoxPlugin(sp.GetRequiredService<IProxmoxApi>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ProxmoxPlugin>>(), periode, new PreferenceStore(fichierPreferences)));
builder.Services.AddSingleton(sp =>
{
    var plugin = sp.GetRequiredService<ProxmoxPlugin>();
    var client = new CoreClient(coeur, plugin, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CoreClient>>());
    plugin.Coeur = client;
    return client;
});
builder.Services.AddHostedService<PluginWorker>();
// Lancé par la page Services : s'arrêter avec le cœur (Krissvia:ParentPid).
builder.Services.AddParentProcessWatch(coeur);

await builder.Build().RunAsync();
return 0;

/// <summary>Les relevés de Proxmox et la connexion au cœur, chacun sur sa boucle.</summary>
internal sealed class PluginWorker(ProxmoxPlugin plugin, CoreClient coeur) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(plugin.RunAsync(stoppingToken), coeur.RunAsync(stoppingToken));
}
