using System.Text.Json;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.Widgets;
using Microsoft.Extensions.FileProviders;

namespace Krissvia.Server;

/// <summary>
/// Catalogue des widgets : les standards, livrés dans <c>wwwroot/widgets/standard</c>, et ceux de l'installation,
/// dans le dossier <c>widgets</c> à côté de la base (ou <c>Widgets:Directory</c>), publié sous <c>/widgets/local</c>.
/// </summary>
/// <remarks>
/// Les standards sont lus à travers les fichiers statiques du serveur et non sur le disque : en développement, ils
/// restent dans le projet au lieu d'être copiés à côté de l'exécutable.
/// </remarks>
internal sealed class WidgetCatalogService(IFileProvider webRoot, string localDirectory)
{
    public const string StandardPath = "widgets/standard";
    public const string LocalUrl = "/widgets/local";

    public string LocalDirectory => localDirectory;

    public WidgetCatalogSnapshot Load() => WidgetCatalog.Load(
    [
        new WidgetSource(WidgetCatalog.Standard, "/" + StandardPath,
            [.. webRoot.GetDirectoryContents(StandardPath).Where(f => f.IsDirectory).Select(f => new DossierStatique(webRoot, $"{StandardPath}/{f.Name}", f.Name))]),
        WidgetSource.FromDirectory(localDirectory, WidgetCatalog.Local, LocalUrl),
    ]);

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
    public static void MapWidgetApi(this RouteGroupBuilder api)
    {
        api.MapGet("/widgets", (WidgetCatalogService catalogue) =>
        {
            var instantane = catalogue.Load();
            return Json(new { widgets = instantane.Widgets.Select(Vue), errors = instantane.Errors });
        });

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
