using Krissvia.PluginHost.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Krissvia.Server;

/// <summary>
/// Démarre le superviseur une fois le serveur à l'écoute (les plugins lancés doivent pouvoir s'y connecter), et
/// arrête tous les plugins qu'il a lancés avec le cœur.
/// </summary>
internal sealed partial class PluginSupervisorLifetime(
    PluginSupervisor supervisor,
    IServer server,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<PluginSupervisorLifetime> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = DemarrerAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        supervisor.StopAll();
        return Task.CompletedTask;
    }

    private async Task DemarrerAsync()
    {
        try
        {
            supervisor.CoreUrl = AdresseLocale();

            var dossiers = (configuration.GetSection("Plugins:Directories").Get<string[]>() ?? ["plugins"])
                .Select(d => Path.GetFullPath(d, environment.ContentRootPath))
                .ToList();
            var (plugins, erreurs) = PluginCatalog.Scan(dossiers);
            supervisor.CatalogErrors = erreurs;
            foreach (var erreur in erreurs) FicheIllisible(logger, erreur);

            await supervisor.LoadAsync(plugins);
            Catalogue(logger, plugins.Count, string.Join(", ", dossiers));
            await supervisor.StartEnabledAsync();
        }
        catch (Exception ex)
        {
            DemarrageImpossible(logger, ex);
        }
    }

    /// <summary>
    /// L'adresse HTTP sur laquelle le cœur écoute vraiment, ramenée à 127.0.0.1 : les plugins lancés tournent sur la
    /// même machine.
    /// </summary>
    private string AdresseLocale()
    {
        var adresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var http = adresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (http is null || !Uri.TryCreate(http.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://[::]", "://localhost"), UriKind.Absolute, out var uri))
            return "ws://127.0.0.1:12795/plugins";

        return $"ws://127.0.0.1:{uri.Port}/plugins";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Services : {Nombre} plugin(s) installé(s) dans {Dossiers}.")]
    private static partial void Catalogue(ILogger logger, int nombre, string dossiers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fiche de plugin illisible : {Erreur}")]
    private static partial void FicheIllisible(ILogger logger, string erreur);

    [LoggerMessage(Level = LogLevel.Error, Message = "Démarrage des services impossible.")]
    private static partial void DemarrageImpossible(ILogger logger, Exception ex);
}
