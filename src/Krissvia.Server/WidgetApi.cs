using System.Text.Json;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.Widgets;
using Krissvia.PluginHost.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Krissvia.Server;

/// <summary>
/// Catalogue des widgets : les standards, livrés dans <c>wwwroot/widgets/standard</c> ; ceux de l'installation, dans le
/// dossier <c>widgets</c> à côté de la base (ou <c>Widgets:Directory</c>), publié sous <c>/widgets/local</c> ; ceux des
/// plugins ACTIVÉS, dans le dossier <c>widgets</c> à côté de leur <c>plugin.json</c>, publiés sous
/// <c>/widgets/plugins/{id}</c>. À identifiant égal, le premier trouvé l'emporte dans cet ordre.
/// </summary>
/// <remarks>
/// Les standards sont lus à travers les fichiers statiques du serveur et non sur le disque : en développement, ils
/// restent dans le projet au lieu d'être copiés à côté de l'exécutable.
/// </remarks>
internal sealed class WidgetCatalogService(IFileProvider webRoot, string localDirectory, BlazorWidgets blazor, PluginSupervisor plugins)
{
    public const string StandardPath = "widgets/standard";
    public const string LocalUrl = "/widgets/local";

    public string LocalDirectory => localDirectory;

    private readonly Lock _installation = new();

    /// <summary>Installe (ou met à jour) un widget local ; lève <see cref="WidgetInstallException"/> avec la raison d'un refus.</summary>
    public string Install(IReadOnlyList<WidgetFile> fichiers)
    {
        // Deux installations simultanées du même widget se marcheraient dessus au moment de la bascule.
        lock (_installation) return WidgetInstaller.Install(localDirectory, fichiers, Load());
    }

    /// <summary>Retire un widget local ; faux s'il n'y en a pas (un widget standard ou de plugin ne se retire pas d'ici).</summary>
    public bool Uninstall(string id)
    {
        lock (_installation) return WidgetInstaller.Uninstall(localDirectory, id);
    }

    public WidgetCatalogSnapshot Load()
    {
        var catalogue = WidgetCatalog.Load(
        [
            new WidgetSource(WidgetCatalog.Standard, "/" + StandardPath,
                [.. webRoot.GetDirectoryContents(StandardPath).Where(f => f.IsDirectory).Select(f => new DossierStatique(webRoot, $"{StandardPath}/{f.Name}", f.Name))]),
            WidgetSource.FromDirectory(localDirectory, WidgetCatalog.Local, LocalUrl),
            .. PluginsActifs().Select(p => WidgetSource.FromDirectory(p.Dossier, WidgetCatalog.PluginOrigin(p.Id), $"{WidgetCatalog.PluginBaseUrl}/{p.Id}")),
        ], blazor.Manifests);
        return catalogue with { Errors = [.. blazor.Errors, .. catalogue.Errors] };
    }

    /// <summary>
    /// Le dossier des widgets d'un plugin, s'il est installé et ACTIVÉ : un plugin désactivé ne publie rien, ses widgets
    /// non plus. Relu à chaque appel, comme le catalogue.
    /// </summary>
    public string? PluginWidgetsDirectory(string pluginId) =>
        PluginsActifs().FirstOrDefault(p => p.Id == pluginId).Dossier;

    private IEnumerable<(string Id, string Dossier)> PluginsActifs() =>
        plugins.Snapshots()
            .Where(s => s is { Enabled: true, Installed: true, Directory: not null })
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => (s.Id, Path.Combine(s.Directory!, "widgets")));

    /// <summary>Le composant d'un widget Blazor du catalogue.</summary>
    public Type? Component(string widgetId) => blazor.Component(widgetId);

    private sealed class DossierStatique(IFileProvider fichiers, string chemin, string nom) : IWidgetFolder
    {
        public string Name => nom;

        public string? ReadText(string relativePath)
        {
            var fichier = fichiers.GetFileInfo($"{chemin}/{relativePath}");
            if (!fichier.Exists) return null;
            using var lecteur = new StreamReader(fichier.CreateReadStream());
            return lecteur.ReadToEnd();
        }

        public bool Exists(string relativePath) => fichiers.GetFileInfo($"{chemin}/{relativePath}").Exists;
    }
}

/// <summary>Catalogue (<c>/api/widgets</c>) et tableaux de bord (<c>/api/dashboards</c>).</summary>
internal static class WidgetApi
{
    /// <summary>
    /// Les fichiers des widgets des plugins (<c>/widgets/plugins/{id}/{dossier}/{fichier}</c>) : seulement sous le
    /// dossier <c>widgets</c> d'un plugin activé, jamais au-dessus. Revalidés à chaque chargement, comme les widgets
    /// locaux : un plugin mis à jour doit se voir sans vider le cache.
    /// </summary>
    public static void MapPluginWidgetFiles(this IEndpointRouteBuilder app)
    {
        var types = new FileExtensionContentTypeProvider();
        app.MapGet(WidgetCatalog.PluginBaseUrl + "/{plugin}/{**chemin}", (string plugin, string chemin, WidgetCatalogService catalogue, HttpContext contexte) =>
        {
            if (catalogue.PluginWidgetsDirectory(plugin) is not { } dossier || WidgetCatalog.SafeFile(dossier, chemin) is not { } fichier)
                return Results.NotFound();
            contexte.Response.Headers.CacheControl = "no-cache";
            return Results.File(fichier, types.TryGetContentType(fichier, out var type) ? type : "application/octet-stream");
        });
    }

    public static void MapWidgetApi(this RouteGroupBuilder api)
    {
        api.MapGet("/widgets", (WidgetCatalogService catalogue) =>
        {
            var instantane = catalogue.Load();
            return Json(new { widgets = instantane.Widgets.Select(Vue), errors = instantane.Errors });
        });

        // Installation d'un widget local depuis une archive .zip (WIDGETS.md §14) ; l'écran Paramètres › Widgets fait de même.
        api.MapPost("/widgets", async (HttpRequest requete, WidgetCatalogService catalogue, CancellationToken ct) =>
        {
            using var copie = new MemoryStream();
            await requete.Body.CopyToAsync(copie, ct);
            copie.Position = 0;
            try
            {
                var id = catalogue.Install(WidgetInstaller.FromZip(copie));
                return Results.Created($"/api/widgets/{id}", new { id });
            }
            catch (WidgetInstallException ex)
            {
                return Refus(ex.Message);
            }
        });

        api.MapDelete("/widgets/{id}", (string id, WidgetCatalogService catalogue) =>
            catalogue.Uninstall(id) ? Results.NoContent() : Results.NotFound(new { error = $"aucun widget local « {id} »" }));

        api.MapGet("/dashboards", async (DashboardService service, CancellationToken ct) =>
            Results.Json(await service.ListAsync(ct), DashboardService.Json));

        api.MapGet("/dashboards/{id}", async (string id, DashboardService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } page ? Results.Json(page, DashboardService.Json) : Inconnue(id));

        api.MapDelete("/dashboards/{id}", async (string id, DashboardService service, CancellationToken ct) =>
        {
            try
            {
                return await service.DeleteAsync(id, ct) ? Results.NoContent() : Inconnue(id);
            }
            catch (DashboardValidationException ex)
            {
                return Refus(ex.Message);
            }
        });

        api.MapPut("/dashboards/{id}", async (string id, HttpRequest requete, DashboardService service, CancellationToken ct) =>
        {
            Dashboard? corps;
            try
            {
                corps = await JsonSerializer.DeserializeAsync<Dashboard>(requete.Body, DashboardService.Json, ct);
            }
            catch (JsonException ex)
            {
                return Refus($"JSON invalide : {ex.Message}");
            }

            if (corps is null) return Refus("corps vide");
            try
            {
                return Results.Json(await service.SaveAsync(corps with { Id = id }, ct), DashboardService.Json);
            }
            catch (DashboardValidationException ex)
            {
                return Refus(ex.Message);
            }
        });
    }

    /// <summary>Ce qu'il faut au tableau de bord pour créer l'élément d'un widget.</summary>
    public static object Vue(CatalogWidget w) => new
    {
        w.Manifest.Id,
        w.Manifest.Name,
        w.Manifest.Description,
        w.Manifest.Version,
        w.Manifest.Element,
        ScriptUrl = w.ScriptUrl,
        w.Manifest.Blazor,
        w.Manifest.Slots,
        w.Manifest.Size,
        w.Manifest.Settings,
        w.Origin,
    };

    private static IResult Json(object valeur) => Results.Json(valeur, ThermostatService.Json);

    private static IResult Inconnue(string id) =>
        Results.Json(new { error = $"page « {id} » inconnue" }, ThermostatService.Json, statusCode: StatusCodes.Status404NotFound);

    private static IResult Refus(string message) =>
        Results.Json(new { error = message }, ThermostatService.Json, statusCode: StatusCodes.Status400BadRequest);
}
