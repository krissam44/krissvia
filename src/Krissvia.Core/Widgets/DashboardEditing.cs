namespace Krissvia.Core.Widgets;

/// <summary>Gestes d'édition du tableau de bord qui touchent plusieurs pages à la fois (fonctions pures, testées).</summary>
public static class DashboardEditing
{
    /// <summary>Titre de la section créée sur une page qui n'en a aucune pour accueillir une carte déplacée.</summary>
    public const string MovedCardsSection = "Cartes déplacées";

    /// <summary>
    /// Déplace une carte d'une page vers une section d'une autre page, à la fin de cette section. Sans section cible
    /// (page vide, ou section disparue entre-temps), une section <see cref="MovedCardsSection"/> est ajoutée à la page.
    /// La carte garde tous ses réglages (widget, liens, taille, nom) : toutes les pages comptent en 24ᵉ.
    /// Rend les pages inchangées si la carte, la page de départ ou la page d'arrivée n'existe pas, ou si c'est la même page.
    /// </summary>
    public static IReadOnlyList<Dashboard> MoveCardToPage(
        IReadOnlyList<Dashboard> pages, string fromPageId, string cardId, string toPageId, string? toSectionId, Func<string> newSectionId)
    {
        if (fromPageId == toPageId || pages.All(p => p.Id != toPageId)) return pages;
        var depart = pages.FirstOrDefault(p => p.Id == fromPageId);
        if (depart?.Sections.SelectMany(s => s.Cards).FirstOrDefault(c => c.Id == cardId) is not { } carte) return pages;

        return [.. pages.Select(p =>
        {
            if (p.Id == fromPageId)
                return p with { Sections = [.. p.Sections.Select(s => s with { Cards = [.. s.Cards.Where(c => c.Id != cardId)] })] };
            if (p.Id != toPageId) return p;

            if (toSectionId is not null && p.Sections.Any(s => s.Id == toSectionId))
                return p with { Sections = [.. p.Sections.Select(s => s.Id == toSectionId ? s with { Cards = [.. s.Cards, carte] } : s)] };
            return p with { Sections = [.. p.Sections, new DashboardSection { Id = newSectionId(), Title = MovedCardsSection, Cards = [carte] }] };
        })];
    }
}
