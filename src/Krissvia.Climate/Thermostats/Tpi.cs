namespace Krissvia.Climate.Thermostats;

/// <summary>Résultat du calcul d'un cycle TPI : figé pour toute la durée du cycle.</summary>
public readonly record struct TpiCycle(double Power, int OnSeconds, double Integral);

/// <summary>
/// Calcul TPI de DomoMud, pur :
/// <c>puissance = Kp × (cible − intérieur) + Ki × (cible − extérieur) + Kint × intégrale</c>, bornée à [0 ; 1],
/// puis convertie en durée de chauffe dans le cycle avec les délais minimaux de marche et d'arrêt.
/// </summary>
public static class Tpi
{
    /// <param name="integrale">Intégrale avant ce cycle, en °C·h.</param>
    /// <param name="dureeEcoulee">Temps écoulé depuis le calcul précédent : l'erreur s'intègre au prorata.</param>
    public static TpiCycle ComputeCycle(
        TpiSettings p, double cible, double interieur, double? exterieur, double integrale, TimeSpan dureeEcoulee)
    {
        var erreur = cible - interieur;

        // Intégrale : erreur × durée, bornée (anti-emballement) ; au-dessus de la consigne on la dissipe de
        // moitié à chaque cycle, sinon elle prolonge la chauffe après le dépassement.
        if (p.Kint > 0)
        {
            var heures = Math.Clamp(dureeEcoulee.TotalSeconds, 0, p.CycleSeconds) / 3600.0;
            integrale = Math.Clamp(integrale + erreur * heures, -p.IntegralMax, p.IntegralMax);
            if (erreur < 0) integrale *= 0.5;
        }

        var puissance = p.Kp * erreur + p.Kint * integrale;
        if (exterieur is { } text && p.Ki > 0) puissance += p.Ki * (cible - text);
        puissance = Math.Clamp(puissance, 0.0, 1.0);

        var marche = (int)(puissance * p.CycleSeconds);

        if (marche > 0 && marche < p.MinOnSeconds)
        {
            // Sous la consigne : on chauffe au moins le minimum, sinon on ne l'atteindrait jamais.
            // À la consigne ou au-dessus : un micro-cycle use le relais pour rien, on l'annule.
            if (interieur < cible)
            {
                marche = p.MinOnSeconds;
                puissance = (double)p.MinOnSeconds / p.CycleSeconds;
            }
            else
            {
                marche = 0;
                puissance = 0;
            }
        }
        else if (p.CycleSeconds - marche is > 0 and var arret && arret < p.MinOffSeconds)
        {
            // Un arrêt trop court ne laisse rien refroidir : cycle plein.
            marche = p.CycleSeconds;
            puissance = 1.0;
        }

        return new TpiCycle(puissance, marche, integrale);
    }
}
