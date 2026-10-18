using System.Reflection;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Widgets;
using Krissvia.Widgets;
using Microsoft.AspNetCore.Components;

namespace Krissvia.Server;

/// <summary>
/// Widgets Blazor (WIDGETS.md §2.2) : les composants marqués <see cref="KrissviaWidgetAttribute"/> des bibliothèques
/// déposées à la racine du dossier <c>widgets</c> de l'installation, ou nommées dans <c>Widgets:Assemblies</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Lues une fois, au démarrage : un assemblage .NET ne se décharge pas. Ajouter ou remplacer une bibliothèque
/// demande un redémarrage (un Web Component, lui, se prend à chaud).</item>
/// <item>Confiance : c'est du code qui s'exécute <em>sur le serveur</em>, comme un plugin. Seul l'administrateur
/// dépose une bibliothèque dans ce dossier (WIDGETS.md §9).</item>
/// <item>Une bibliothèque illisible, ou un composant mal déclaré, est signalé dans le catalogue et n'empêche rien
/// d'autre de fonctionner.</item>
/// </list>
/// </remarks>
internal sealed class BlazorWidgets
{
    private readonly Dictionary<string, Type> _composants = new(StringComparer.Ordinal);
    private readonly List<WidgetManifest> _manifestes = [];
    private readonly List<string> _erreurs = [];

    public BlazorWidgets(IEnumerable<string> bibliotheques)
    {
        foreach (var chemin in bibliotheques.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Assembly assemblage;
            try
            {
                assemblage = Assembly.LoadFrom(chemin);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
            {
                _erreurs.Add($"{WidgetCatalog.Blazor}/{Path.GetFileName(chemin)} : bibliothèque illisible ({ex.Message})");
                continue;
            }

            Type[] types;
            try
            {
                types = assemblage.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Une dépendance absente : on garde les types lisibles et on le dit.
                types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
                _erreurs.Add($"{WidgetCatalog.Blazor}/{Path.GetFileName(chemin)} : types illisibles ({ex.LoaderExceptions.FirstOrDefault()?.Message})");
            }

            foreach (var type in types)
            {
                if (type.GetCustomAttribute<KrissviaWidgetAttribute>() is not { } declaration) continue;
                if (Refus(type) is { } refus)
                {
                    _erreurs.Add($"{WidgetCatalog.Blazor}/{type.FullName} : {refus}");
                    continue;
                }

                try
                {
                    _manifestes.Add(Manifeste(type, declaration));
                    _composants[declaration.Id] = type;
                }
                catch (InvalidOperationException ex)
                {
                    _erreurs.Add($"{WidgetCatalog.Blazor}/{type.FullName} : {ex.Message}");
                }
            }
        }
    }

    /// <summary>Les bibliothèques à lire : celles du dossier des widgets, puis celles de la configuration.</summary>
    public static IEnumerable<string> Bibliotheques(string dossierWidgets, IConfiguration configuration, string racine) =>
    [
        .. (Directory.Exists(dossierWidgets) ? Directory.GetFiles(dossierWidgets, "*.dll").Order(StringComparer.Ordinal) : Enumerable.Empty<string>()),
        .. (configuration.GetSection("Widgets:Assemblies").Get<string[]>() ?? []).Select(c => Path.GetFullPath(c, racine)),
    ];

    public IReadOnlyList<WidgetManifest> Manifests => _manifestes;

    public IReadOnlyList<string> Errors => _erreurs;

    public Type? Component(string widgetId) => _composants.GetValueOrDefault(widgetId);

    private static string? Refus(Type type)
    {
        if (!typeof(IComponent).IsAssignableFrom(type) || type.IsAbstract) return "ce n'est pas un composant Razor";
        var contexte = type.GetProperty("Context");
        return contexte?.PropertyType == typeof(WidgetContext) && contexte.GetCustomAttribute<ParameterAttribute>() is not null
            ? null
            : "il lui faut un paramètre [Parameter] public WidgetContext Context";
    }

    private static WidgetManifest Manifeste(Type type, KrissviaWidgetAttribute d)
    {
        JsonElement? reglages = null;
        if (d.Settings is { Length: > 0 } schema)
        {
            try
            {
                reglages = JsonSerializer.Deserialize<JsonElement>(schema);
            }
            catch (JsonException)
            {
                // Un schéma faux laisse la carte sans options plutôt que d'écarter le widget.
            }
        }

        return new WidgetManifest
        {
            Contract = WidgetCatalog.ContractVersion,
            Id = d.Id,
            Name = d.Name,
            Description = d.Description,
            Version = d.Version ?? type.Assembly.GetName().Version?.ToString(3),
            Blazor = type.FullName,
            Size = new WidgetSize(d.Cols, d.Rows),
            Settings = reglages,
            Slots =
            [
                .. type.GetCustomAttributes<WidgetSlotAttribute>()
                    .OrderBy(s => s.Order)
                    .Select(s => new WidgetSlot
                    {
                        Name = s.Name,
                        Required = s.Required,
                        Kinds = [.. s.Kinds.Select(Famille)],
                        DeviceClasses = s.DeviceClasses,
                        Usages = s.Usages,
                    }),
            ],
        };
    }

    // « binary_sensor » → BinarySensor, comme dans un widget.json.
    private static EntityKind Famille(string nom) =>
        Enum.TryParse<EntityKind>(nom.Replace("_", ""), ignoreCase: true, out var famille)
            ? famille
            : throw new InvalidOperationException($"famille inconnue « {nom} »");
}
