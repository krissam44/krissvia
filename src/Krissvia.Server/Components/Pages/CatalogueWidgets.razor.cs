using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;
using Krissvia.Server.Components.Widgets;
using Microsoft.AspNetCore.Components.Forms;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Paramètres › Widgets : le catalogue (standards, locaux, plugins, Blazor), ce qui en a été écarté, et l'installation
/// d'un widget local sans aller chercher sur le serveur le dossier « à côté de la base » (WIDGETS.md §14).
/// </summary>
public partial class CatalogueWidgets
{
    [Inject] private WidgetCatalogService Catalogue { get; set; } = default!;
    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private DashboardService Tableaux { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private WidgetCatalogSnapshot _catalogue = new([], []);
    private IReadOnlyList<CatalogWidget> _lignes = [];
    private bool _occupe;
    private string? _refus;

    /// <summary>Les vôtres d'abord : ce sont eux qu'on vient voir ici.</summary>
    private static readonly string[] OrdreOrigines = ["Installés ici", "Livrés par un plugin", "Blazor", "Standards"];

    private readonly TableGroupDefinition<CatalogWidget> _groupes = new() { Selector = w => Groupe(w.Origin) };

    protected override void OnInitialized() => Recharger();

    private void Recharger()
    {
        _catalogue = Catalogue.Load();
        _lignes = [.. _catalogue.Widgets
            .OrderBy(w => Array.IndexOf(OrdreOrigines, Groupe(w.Origin)))
            .ThenBy(w => w.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string Groupe(string origin) =>
        origin == WidgetCatalog.Local ? OrdreOrigines[0]
        : WidgetCatalog.PluginOf(origin) is not null ? OrdreOrigines[1]
        : origin == WidgetCatalog.Blazor ? OrdreOrigines[2]
        : OrdreOrigines[3];

    /// <summary>Ce qu'accepte le slot principal, en clair : « light, switch · usage spot ».</summary>
    private static string Accepte(WidgetSlot slot)
    {
        var parties = new List<string>
        {
            slot.Kinds.Count == 0 ? "toute entité" : string.Join(", ", slot.Kinds.Select(k => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(k.ToString()))),
        };
        if (slot.DeviceClasses.Count > 0) parties.Add(string.Join(", ", slot.DeviceClasses));
        if (slot.Usages.Count > 0) parties.Add($"usage {string.Join(", ", slot.Usages)}");
        return string.Join(" · ", parties);
    }

    /// <summary>« 1/4 × 2 » : la part de largeur, sans le « (4 par ligne) » du menu des cartes, puis les lignes.</summary>
    private static string Taille(WidgetSize taille) => $"{Largeurs.Libelle(taille.Cols).Split(" (")[0]} × {Hauteurs.Libelle(taille.Rows)}";

    private async Task InstallerAsync(IReadOnlyList<IBrowserFile>? choisis)
    {
        if (choisis is not { Count: > 0 }) return;
        _occupe = true;
        _refus = null;
        try
        {
            IReadOnlyList<WidgetFile> fichiers;
            if (choisis is [var seul] && seul.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                fichiers = WidgetInstaller.FromZip(await LireAsync(seul));
            }
            else
            {
                var liste = new List<WidgetFile>();
                foreach (var f in choisis) liste.Add(new WidgetFile(f.Name, (await LireAsync(f)).ToArray()));
                fichiers = liste;
            }

            var id = Catalogue.Install(fichiers);
            Recharger();
            Snackbar.Add($"Widget « {_catalogue.Find(id)?.Manifest.Name ?? id} » installé.", Severity.Success);
        }
        catch (WidgetInstallException ex)
        {
            _refus = ex.Message;
        }
        catch (IOException ex)
        {
            // Fichier trop gros pour le navigateur, ou disque plein côté serveur.
            _refus = ex.Message;
        }
        finally
        {
            _occupe = false;
        }
    }

    private static async Task<MemoryStream> LireAsync(IBrowserFile fichier)
    {
        var copie = new MemoryStream();
        await using var flux = fichier.OpenReadStream(WidgetInstaller.MaxBytes);
        await flux.CopyToAsync(copie);
        copie.Position = 0;
        return copie;
    }

    private async Task RetirerAsync(CatalogWidget widget)
    {
        var id = widget.Manifest.Id;
        var entites = (await Registre.ListAsync()).Count(e => e.Widget == id);
        var cartes = (await Tableaux.ListAsync()).SelectMany(p => p.Sections).SelectMany(s => s.Cards).Count(c => c.Widget == id);
        var suite = (entites, cartes) switch
        {
            (0, 0) => "Aucune carte ni entité ne l'utilise.",
            _ => $"{entites} entité(s) le prennent : elles reviendront au widget automatique. "
                 + $"{cartes} carte(s) l'imposent : elles afficheront « Valeur » avec un bandeau, à régler de nouveau.",
        };
        if (await Dialogues.ShowMessageBoxAsync("Retirer le widget", $"Retirer « {widget.Manifest.Name} » ? {suite}",
                yesText: "Retirer", cancelText: "Annuler") != true)
            return;

        Catalogue.Uninstall(id);
        Recharger();
        Snackbar.Add($"Widget « {widget.Manifest.Name} » retiré.", Severity.Success);
    }
}
