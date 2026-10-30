using Krissvia.Core.History;
using Krissvia.Core.States;

namespace Krissvia.Server.Components.Historique;

/// <summary>Ce qu'un compteur a consommé : depuis minuit, et jour par jour sur une période.</summary>
/// <param name="Jours">Du plus ancien au plus récent ; aujourd'hui vaut <see cref="DepuisMinuit"/> quand on le connaît.</param>
public sealed record Consommation(double? DepuisMinuit, IReadOnlyList<EnergyDay> Jours);

/// <summary>
/// La consommation d'un compteur, lue dans l'historique : un seul calcul pour la carte de l'écran Électricité et pour
/// <c>GET /api/energy</c> (appli mobile).
/// </summary>
public static class LectureCompteur
{
    /// <param name="jours">Nombre de jours, aujourd'hui compris.</param>
    public static async Task<Consommation> LireAsync(
        Compteur compteur, int jours, IHistoryRepository historique, IEntityStateReader etats, TimeProvider horloge, CancellationToken ct = default)
    {
        var zone = horloge.LocalTimeZone;
        var maintenant = horloge.GetUtcNow();
        var aujourdhui = EnergyDays.Jour(maintenant, zone);
        var minuit = EnergyDays.Midnight(aujourdhui, zone);

        // Depuis minuit : l'index maintenant moins celui en vigueur à minuit (le premier point lu peut le précéder).
        var index = await historique.ReadAsync(compteur.Index.EntityId, minuit, maintenant, ct);
        var aMinuit = index.FirstOrDefault(p => p.Value is not null)?.Value;
        var actuel = Nombre(etats, compteur.Index.EntityId) ?? index.LastOrDefault(p => p.Value is not null)?.Value;
        double? depuisMinuit = aMinuit is { } a && actuel is { } b ? Math.Max(0, b - a) : null;

        // Les jours, depuis les heures consolidées (la veille du premier jour donne l'index de départ).
        var premier = aujourdhui.AddDays(-(jours - 1));
        var depuis = EnergyDays.Midnight(premier.AddDays(-1), zone);
        var heures = await historique.ReadHourlyAsync(compteur.Index.EntityId, depuis, maintenant, ct);
        var heuresPuissance = compteur.Puissance is { } p ? await historique.ReadHourlyAsync(p.EntityId, depuis, maintenant, ct) : [];
        var liste = EnergyDays.FromHours(heures, heuresPuissance, zone, premier, aujourdhui).ToList();
        // Aujourd'hui : les points bruts, plus frais que l'heure consolidée toutes les 10 min.
        if (depuisMinuit is not null) liste[^1] = liste[^1] with { Kwh = depuisMinuit };

        return new Consommation(depuisMinuit, liste);
    }

    /// <summary>La case de la courbe de puissance du jour, la même à l'écran Électricité et dans <c>GET /api/energy</c>.</summary>
    public static readonly TimeSpan PasDuJour = TimeSpan.FromMinutes(5);

    /// <summary>La grille du jour : une case de <see cref="PasDuJour"/> de minuit à maintenant.</summary>
    public static IReadOnlyList<DateTimeOffset> GrilleDuJour(TimeProvider horloge)
    {
        var maintenant = horloge.GetUtcNow();
        var minuit = EnergyDays.Midnight(EnergyDays.Jour(maintenant, horloge.LocalTimeZone), horloge.LocalTimeZone);
        return [.. Enumerable.Range(0, (int)Math.Ceiling((maintenant - minuit) / PasDuJour)).Select(i => minuit + i * PasDuJour)];
    }

    /// <summary>La puissance du jour, case par case (moyenne, min, max) ; vide pour un compteur sans entité de puissance.</summary>
    public static async Task<IReadOnlyList<CurvePoint>> PuissanceDuJourAsync(
        Compteur compteur, IReadOnlyList<DateTimeOffset> grille, IHistoryRepository historique, TimeProvider horloge, CancellationToken ct = default)
    {
        if (compteur.Puissance is not { } puissance || grille.Count == 0) return [];
        var maintenant = horloge.GetUtcNow();
        return HistoryCurve.FromPoints(await historique.ReadAsync(puissance.EntityId, grille[0], maintenant, ct), grille, PasDuJour, maintenant);
    }

    /// <summary>La valeur du moment d'une entité joignable ; null sinon (une valeur d'un appareil injoignable n'est pas une mesure).</summary>
    public static double? Nombre(IEntityStateReader etats, string entityId) =>
        etats.Get(entityId) is { Available: true } s && s.TryGetNumber(out var v) ? v : null;
}
