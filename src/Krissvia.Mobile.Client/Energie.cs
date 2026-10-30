using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>Un jour d'un compteur. Null : pas de mesure ce jour-là — pas « 0 kWh ».</summary>
public sealed record JourConso(DateOnly Jour, double? Kwh, double? Cout, double? Pointe);

/// <summary>Une case de 5 min de la puissance du jour : moyenne, et l'étendue min-max. Null : pas de mesure dans la case.</summary>
public sealed record PointPuissance(DateTimeOffset A, double? Moyenne, double? Min, double? Max);

/// <summary>
/// Un compteur : chiffres du moment (puissance, tension) et consommation, telle que le serveur la tire de son
/// historique. L'appli n'en refait pas le calcul (remises à zéro, jours locaux).
/// </summary>
public sealed record CompteurEnergie(
    string Nom,
    double? Puissance,
    string? UnitePuissance,
    double? Tension,
    double? AujourdhuiKwh,
    double? AujourdhuiCout,
    IReadOnlyList<JourConso> Jours,
    IReadOnlyList<PointPuissance>? PuissanceDuJour = null)
{
    /// <summary>De minuit à maintenant ; vide sans entité de puissance, ou contre un serveur qui ne la donne pas encore.</summary>
    public IReadOnlyList<PointPuissance> Courbe => PuissanceDuJour ?? [];

    /// <summary>La plus forte consommation de la période : l'échelle des barres.</summary>
    public double MaxKwh => Jours.Select(j => j.Kwh ?? 0).DefaultIfEmpty(0).Max();
}

/// <summary>L'écran Électricité tel que le serveur le calcule (<c>GET /api/energy</c>).</summary>
/// <param name="Prix">Prix du kWh (€) ; null : aucun coût n'est donné.</param>
public sealed record VueEnergie(double? Prix, IReadOnlyList<CompteurEnergie> Compteurs)
{
    public static VueEnergie? Lire(JsonElement json)
    {
        var dto = json.Deserialize<VueDto>(Thermostat.Json);
        if (dto is null) return null;

        return new VueEnergie(dto.Price,
        [
            .. (dto.Meters ?? []).Select(m => new CompteurEnergie(
                m.Name ?? m.IndexEntityId ?? "?", m.Power, m.PowerUnit, m.Voltage, m.TodayKwh, m.TodayCost,
                [.. (m.Days ?? []).Select(j => new JourConso(j.Day, j.Kwh, j.Cost, j.PeakPower))],
                [.. (m.TodayPower ?? []).Select(p => new PointPuissance(p.At, p.Mean, p.Min, p.Max))])),
        ]);
    }

    private sealed record VueDto(double? Price, List<CompteurDto>? Meters);

    private sealed record CompteurDto(
        string? Name, string? IndexEntityId, double? Power, string? PowerUnit, double? Voltage, double? TodayKwh, double? TodayCost,
        List<JourDto>? Days, List<PointDto>? TodayPower);

    private sealed record PointDto(DateTimeOffset At, double? Mean, double? Min, double? Max);

    private sealed record JourDto(DateOnly Day, double? Kwh, double? Cost, double? PeakPower);
}
