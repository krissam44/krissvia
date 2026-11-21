using System.Text.Json.Nodes;

namespace Krissvia.PluginHost.Services;

/// <summary>
/// Le presse-papiers des listes de réglages, le temps d'une session de l'interface : « Copier » sous une liste (la page
/// perso d'une platine, ses sondes…), « Coller » sous la même liste d'un autre élément la remplace. En mémoire, pas dans
/// celui du système : sur http://adresse-du-raspberry, le navigateur refuse le presse-papiers aux pages non sécurisées.
/// </summary>
public sealed class PressePapiersReglages
{
    private string? _signature;
    private JsonArray? _elements;

    /// <summary>
    /// Une liste ne se colle que dans une liste de même nom et de mêmes champs : des liaisons de page perso ne finissent
    /// jamais dans les sondes, ni dans la liste homonyme d'un autre plugin.
    /// </summary>
    public static string Signature(SettingField liste) => $"{liste.Key}|{string.Join(",", liste.Properties.Select(p => p.Key))}";

    /// <summary>Garde une copie de la liste : la modifier ensuite ne change pas ce qui a été copié.</summary>
    public void Copier(SettingField liste, JsonArray elements)
    {
        _signature = Signature(liste);
        _elements = (JsonArray)elements.DeepClone();
    }

    /// <summary>Le nombre d'éléments à coller dans cette liste, ou null si ce qui a été copié n'en vient pas.</summary>
    public int? Disponible(SettingField liste) => _elements is not null && _signature == Signature(liste) ? _elements.Count : null;

    /// <summary>
    /// Les éléments à coller, chacun sa copie, complété des valeurs par défaut et borné au plafond de la liste. Vide si
    /// rien de compatible n'a été copié.
    /// </summary>
    public IReadOnlyList<JsonObject> Coller(SettingField liste) => Disponible(liste) is null
        ? []
        : [.. _elements!.OfType<JsonObject>()
            .Take(liste.MaxItems ?? int.MaxValue)
            .Select(e => SettingsSchema.Complete(liste, (JsonObject)e.DeepClone()))];
}
