using Krissvia.Core.Scheduling;

namespace Krissvia.Climate.Thermostats;

/// <summary>
/// Pente de chauffe d'une pièce : minutes pour gagner 1 °C à pleine puissance, et la température extérieure moyenne
/// pendant qu'on l'a mesurée (sans elle, la pente serait un nombre hors contexte, inutilisable pour la corriger
/// selon la météo à venir).
/// </summary>
public sealed record HeatingSlope(double MinutesPerDegree, double? OutdoorReference);

/// <summary>Le prochain changement de mode du planning.</summary>
public readonly record struct NextSlot(DateTime Start, ThermostatMode Mode);

/// <summary>
/// Préchauffage anticipé (optimum start) de DomoMud, en fonctions pures : chauffer avant un créneau plus chaud pour
/// que sa consigne soit atteinte À L'HEURE du créneau, et non une heure après.
/// </summary>
/// <remarks>
/// L'avance vaut (consigne à venir − ambiante) × pente. La pente s'apprend en observant les montées à pleine
/// puissance, et se corrige selon la météo PRÉVUE pendant la montée : un matin de gel, il faut partir plus tôt.
/// </remarks>
public static class Preheat
{
    /// <summary>Pente avant tout apprentissage : 30 min/°C, un radiateur électrique typique.</summary>
    public const double DefaultMinutesPerDegree = 30.0;

    /// <summary>Avance maximale : 3 h. Garde-fou contre une pente aberrante ou une pièce très froide.</summary>
    public const double MaxMinutes = 180.0;

    /// <summary>
    /// Le premier changement de mode dans les <paramref name="horizonMinutes"/> à venir (par défaut
    /// <see cref="MaxMinutes"/>), minute par minute.
    /// </summary>
    public static NextSlot? NextChange(WeeklySchedule planning, DateTime heureLocale, int horizonMinutes = (int)MaxMinutes)
    {
        var actuel = planning.ModeAt(heureLocale);
        for (var m = 1; m <= horizonMinutes; m++)
        {
            var t = heureLocale.AddMinutes(m);
            if (planning.ModeAt(t) is var mode && mode != actuel) return new NextSlot(t, mode);
        }

        return null;
    }

    /// <summary>
    /// La consigne à viser dès maintenant pour préparer le prochain créneau, ou null si rien n'est à préparer : pas
    /// de planning, consigne ajustée à la main, créneau suivant Off ou moins chaud, pièce déjà au niveau, ou encore
    /// trop tôt.
    /// </summary>
    /// <param name="penteFenetre">
    /// La pente à appliquer, selon la consigne à venir et la durée qui reste avant le créneau (le moteur y met la
    /// correction météo).
    /// </param>
    public static double? Target(ThermostatSettings t, double ambiante, DateTime heureLocale, double cibleActuelle,
        Func<double, TimeSpan, double> penteFenetre)
    {
        if (!t.PreheatEnabled || !t.ScheduleActive || t.TargetOverride is not null) return null;
        if (NextChange(t.Schedule, heureLocale) is not { } prochain || prochain.Mode == ThermostatMode.Off) return null;

        var cibleFuture = t.TargetFor(prochain.Mode);
        if (cibleFuture <= cibleActuelle + 0.1 || ambiante >= cibleFuture - 0.1) return null;

        var avant = prochain.Start - heureLocale;
        var requises = Math.Min((cibleFuture - ambiante) * penteFenetre(cibleFuture, avant), MaxMinutes);
        return avant.TotalMinutes > requises ? null : cibleFuture;
    }

    /// <summary>
    /// Remontée avant un retour d'absence programmé : la consigne qui s'appliquera À L'ARRIVÉE (mode du planning à
    /// cette heure-là, ou mode manuel), dès qu'il est temps de partir pour l'atteindre. Null si rien à faire :
    /// préchauffage désactivé, arrivée en Off, pièce déjà au niveau, retour passé, ou encore trop tôt.
    /// </summary>
    /// <remarks>
    /// Pas de plafond <see cref="MaxMinutes"/> : remonter du hors-gel au confort demande légitimement bien plus que
    /// les 3 h prévues pour un créneau. C'est tout l'intérêt d'un retour annoncé à l'avance.
    /// </remarks>
    public static double? ReturnTarget(ThermostatSettings t, double ambiante, DateTime heureLocale, DateTime retourLocal,
        Func<double, TimeSpan, double> penteFenetre)
    {
        if (!t.PreheatEnabled || retourLocal <= heureLocale) return null;
        var mode = t.ModeAt(retourLocal);
        if (mode == ThermostatMode.Off) return null;

        var cible = t.TargetFor(mode);
        if (ambiante >= cible - 0.1) return null;

        var avant = retourLocal - heureLocale;
        var requises = (cible - ambiante) * penteFenetre(cible, avant);
        return avant.TotalMinutes > requises ? null : cible;
    }

    /// <summary>
    /// La pente corrigée par la température extérieure prévue pendant la montée (méthode de DomoMud). Aucune
    /// constante arbitraire : <c>Ki × (consigne − extérieur)</c> est la part de puissance qui sert à TENIR la
    /// consigne ; le reste est le surplus qui sert à MONTER, et la durée est inversement proportionnelle au surplus.
    /// La correction s'ajuste donc d'elle-même à l'isolation de la pièce. Bornée à [0,5 ; 3].
    /// </summary>
    public static double CorrectedMinutesPerDegree(HeatingSlope pente, double ki, double cible, double? exterieurPrevu)
    {
        if (pente.OutdoorReference is not { } reference || exterieurPrevu is not { } prevu || ki <= 0)
            return pente.MinutesPerDegree;

        // Au-delà de 0,85, le surplus deviendrait quasi nul et le rapport exploserait.
        double Tenue(double exterieur) => Math.Clamp(ki * (cible - exterieur), 0, 0.85);

        var surplusPrevu = 1 - Tenue(prevu);
        var facteur = Math.Clamp((1 - Tenue(reference)) / surplusPrevu, 0.5, 3.0);
        return pente.MinutesPerDegree * facteur;
    }

    /// <summary>
    /// Une montée observée (<paramref name="minutes"/> pour <paramref name="degres"/> gagnés à pleine puissance),
    /// fusionnée dans la pente apprise par moyenne glissante (70 % l'ancienne, 30 % la nouvelle). La mesure est
    /// bornée à 5-240 min/°C ; la référence extérieure suit la même moyenne, pour décrire les mêmes conditions.
    /// </summary>
    public static HeatingSlope Learn(HeatingSlope? ancienne, double minutes, double degres, double? exterieur)
    {
        var mesuree = Math.Clamp(minutes / degres, 5.0, 240.0);
        var pente = ancienne is null ? mesuree : 0.7 * ancienne.MinutesPerDegree + 0.3 * mesuree;
        var reference = exterieur is not { } x ? ancienne?.OutdoorReference
            : ancienne?.OutdoorReference is { } r ? 0.7 * r + 0.3 * x
            : x;
        return new HeatingSlope(Math.Round(pente, 1), reference is { } v ? Math.Round(v, 1) : null);
    }
}

/// <summary>Où le moteur garde la pente apprise de chaque thermostat, pour qu'elle survive à un redémarrage.</summary>
public interface IHeatingSlopeStore
{
    Task<IReadOnlyDictionary<string, HeatingSlope>> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(string thermostatId, HeatingSlope pente, CancellationToken ct = default);
}

/// <summary>En mémoire seulement : les tests, et un moteur construit sans magasin.</summary>
public sealed class MemoryHeatingSlopeStore : IHeatingSlopeStore
{
    public Dictionary<string, HeatingSlope> Slopes { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, HeatingSlope>> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, HeatingSlope>>(new Dictionary<string, HeatingSlope>(Slopes, StringComparer.Ordinal));

    public Task SaveAsync(string thermostatId, HeatingSlope pente, CancellationToken ct = default)
    {
        Slopes[thermostatId] = pente;
        return Task.CompletedTask;
    }
}
