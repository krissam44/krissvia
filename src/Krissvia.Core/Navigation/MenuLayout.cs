namespace Krissvia.Core.Navigation;

/// <summary>Réglage d'une entrée du menu latéral : sa place (l'ordre de la liste) et son masquage.</summary>
public sealed record MenuItemSetting(string Key, bool Hidden = false);

/// <summary>
/// Range les entrées du menu latéral selon la personnalisation enregistrée. Les entrées sont désignées par des
/// clés STABLES : un libellé ou une adresse qui change ne perd pas la personnalisation.
/// </summary>
/// <remarks>
/// Seules les entrées <em>disponibles</em> s'affichent et se réordonnent : celle d'un service désactivé disparaît,
/// mais garde sa place enregistrée et la retrouve quand le service est réactivé.
/// </remarks>
public static class MenuLayout
{
    /// <summary>
    /// Les entrées disponibles (dans l'ordre du catalogue), rangées selon les réglages : d'abord celles déjà réglées,
    /// dans l'ordre enregistré, puis les nouvelles, visibles, dans l'ordre du catalogue — une page ajoutée à
    /// Krissvia apparaît ainsi en fin de menu sans que personne ait à la montrer.
    /// </summary>
    public static IReadOnlyList<MenuItemSetting> Arrange(IReadOnlyList<string> available, IReadOnlyList<MenuItemSetting> saved)
    {
        var disponibles = new HashSet<string>(available, StringComparer.Ordinal);
        var vues = new HashSet<string>(StringComparer.Ordinal);
        var resultat = new List<MenuItemSetting>();

        foreach (var reglage in saved)
        {
            if (disponibles.Contains(reglage.Key) && vues.Add(reglage.Key)) resultat.Add(reglage);
        }

        foreach (var cle in available)
        {
            if (vues.Add(cle)) resultat.Add(new MenuItemSetting(cle));
        }

        return resultat;
    }

    /// <summary>
    /// Les réglages à enregistrer après une personnalisation, qui n'a porté que sur les entrées disponibles. Celles
    /// qui ne le sont pas gardent leur position ; les entrées éditées remplissent, dans leur nouvel ordre, les places
    /// qu'elles occupaient, et les nouvelles vont à la fin.
    /// </summary>
    public static IReadOnlyList<MenuItemSetting> Merge(IReadOnlyList<MenuItemSetting> saved, IReadOnlyList<MenuItemSetting> edited)
    {
        var editees = edited.DistinctBy(e => e.Key, StringComparer.Ordinal).ToList();
        var cles = new HashSet<string>(editees.Select(e => e.Key), StringComparer.Ordinal);
        var resultat = new List<MenuItemSetting>();
        var suivante = 0;

        foreach (var reglage in saved.DistinctBy(s => s.Key, StringComparer.Ordinal))
        {
            resultat.Add(cles.Contains(reglage.Key) ? editees[suivante++] : reglage);
        }

        resultat.AddRange(editees.Skip(suivante));
        return resultat;
    }
}
