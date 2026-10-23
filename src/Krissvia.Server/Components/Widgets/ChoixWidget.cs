using Krissvia.Core.Registry;
using Krissvia.Core.Widgets;

namespace Krissvia.Server.Components.Widgets;

/// <summary>Le libellé d'un widget dans une liste de choix (écran Entités, carte du tableau de bord).</summary>
public static class ChoixWidget
{
    /// <summary>
    /// « Éclairage (spot) — local · usage spot » : l'origine si ce n'est pas un standard, et l'usage prévu par le widget
    /// quand il diffère de celui de l'entité — proposé quand même, le choix explicite fait foi (WIDGETS.md §14).
    /// </summary>
    public static string Libelle(CatalogWidget widget, RegisteredEntity? entite)
    {
        var precisions = new List<string>();
        if (!widget.IsStandard) precisions.Add(WidgetCatalog.PluginOf(widget.Origin) is { } plugin ? $"plugin {plugin}" : widget.Origin);
        if (entite is not null && WidgetCatalog.IsOtherUsage(widget, entite)) precisions.Add($"usage {string.Join(", ", widget.Manifest.Slots[0].Usages)}");
        return precisions.Count == 0 ? widget.Manifest.Name : $"{widget.Manifest.Name} — {string.Join(" · ", precisions)}";
    }
}
