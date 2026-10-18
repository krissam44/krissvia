namespace Krissvia.Widgets;

/// <summary>
/// Déclare un composant Razor comme widget Krissvia (WIDGETS.md §2.2). C'est son manifeste : les mêmes informations
/// que le <c>widget.json</c> d'un Web Component, écrites en C#.
/// </summary>
/// <example>
/// <code>
/// [KrissviaWidget("jauge-cuve", "Jauge de cuve", Cols = 2)]
/// [WidgetSlot("niveau", Kinds = ["sensor"], DeviceClasses = ["moisture"], Required = true)]
/// public partial class JaugeCuve { [Parameter] public WidgetContext Context { get; set; } = default!; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class KrissviaWidgetAttribute(string id, string name) : Attribute
{
    /// <summary>Minuscules, chiffres et « - » ; unique dans l'installation.</summary>
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string? Description { get; init; }

    public string? Version { get; init; }

    /// <summary>Largeur par défaut, en colonnes de la grille (1 à 6).</summary>
    public int Cols { get; init; } = 1;

    /// <summary>Hauteur par défaut, en lignes (1 à 4).</summary>
    public int Rows { get; init; } = 1;

    /// <summary>
    /// Options de chaque carte, en JSON Schema (le même sous-ensemble que les réglages des plugins) ; le tableau de
    /// bord en tire le formulaire. Null : pas d'options.
    /// </summary>
    public string? Settings { get; init; }
}

/// <summary>
/// Une entité attendue par le widget, avec son rôle. Le premier slot déclaré est le principal (obligatoire) : c'est
/// lui qui décide des entités auxquelles le widget est proposé.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WidgetSlotAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Familles acceptées, comme dans le contrat : <c>sensor</c>, <c>binary_sensor</c>, <c>switch</c>…</summary>
    public string[] Kinds { get; init; } = [];

    public string[] DeviceClasses { get; init; } = [];

    public string[] Usages { get; init; } = [];

    public bool Required { get; init; }

    /// <summary>Ordre du slot : les attributs n'ont pas d'ordre garanti, celui-ci le fixe (0 = principal).</summary>
    public int Order { get; init; }
}
