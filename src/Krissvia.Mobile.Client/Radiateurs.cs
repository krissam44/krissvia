namespace Krissvia.Mobile.Client;

/// <summary>
/// L'écran « qui chauffe en ce moment », tiré des thermostats déjà suivis par le flux : aucun appel de plus au
/// serveur, aucune règle refaite. « En chauffe » est le <c>heating</c> du cœur, qui suit le relais — préchauffage
/// compris —, pas une déduction du statut comme dans l'appli de DomoMud, où le drapeau mentait.
/// </summary>
public static class Radiateurs
{
    /// <summary>Les modes des actions rapides, dans cet ordre : ceux qu'on change à distance.</summary>
    public static readonly IReadOnlyList<string> ModesRapides = ["comfort", "eco", "off"];

    /// <summary>Ceux qui chauffent d'abord, puis par nom.</summary>
    public static IReadOnlyList<Thermostat> Ordonner(IEnumerable<Thermostat> thermostats) =>
        [.. thermostats.OrderByDescending(t => t.APublie && t.Chauffe).ThenBy(t => t.Nom, StringComparer.CurrentCultureIgnoreCase)];

    public static int EnChauffe(IEnumerable<Thermostat> thermostats) => thermostats.Count(t => t.APublie && t.Chauffe);

    /// <summary>
    /// Les actions rapides que ce thermostat propose vraiment, avec ses libellés : un mode que le serveur ne déclare
    /// pas n'a pas de bouton.
    /// </summary>
    public static IReadOnlyList<ModeThermostat> Actions(Thermostat thermostat) =>
        [.. ModesRapides.Select(id => thermostat.Modes.FirstOrDefault(m => m.Id == id)).OfType<ModeThermostat>()];
}
