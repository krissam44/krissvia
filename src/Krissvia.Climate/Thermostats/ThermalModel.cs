using System.Text.Json.Serialization;

namespace Krissvia.Climate.Thermostats;

/// <summary>Type de chauffage : fixe la vitesse de réponse qu'on peut demander à la régulation.</summary>
public enum HeatingType
{
    /// <summary>Radiateur électrique : réagit en quelques minutes.</summary>
    Electric,

    /// <summary>Radiateurs à eau : le circuit met du temps à chauffer et à refroidir.</summary>
    Water,

    /// <summary>Plancher chauffant : la dalle stocke la chaleur pendant des heures.</summary>
    Floor,
}

/// <summary>
/// Un intervalle observé (30 min, typiquement) : ambiante au début et à la fin, part du temps où le radiateur chauffait,
/// écart moyen intérieur − extérieur.
/// </summary>
public readonly record struct ThermalInterval(double Hours, double IndoorStart, double IndoorEnd, double Duty, double IndoorOutdoorGap);

/// <summary>
/// Le modèle d'une pièce : <c>dT/dt = g·u − k·(T − Text) + c</c>. <see cref="Gain"/> (g, °C/h à pleine puissance),
/// <see cref="LossRate"/> (k, par heure ; la constante de temps vaut 1/k), <see cref="FreeGain"/> (c, °C/h : soleil,
/// occupants, voisins). <see cref="Refusal"/> dit pourquoi on ne peut pas lui faire confiance ; null s'il est utilisable.
/// </summary>
public sealed record ThermalIdentification(
    double Gain, double LossRate, double FreeGain, double R2, int Intervals, double ObservedHours, string? Refusal)
{
    // Calculés, donc hors du JSON : une constante de temps infinie (pas de déperditions) ne s'écrit pas en JSON.
    [JsonIgnore] public bool Usable => Refusal is null;
    [JsonIgnore] public double TimeConstantHours => LossRate > 0 ? 1 / LossRate : double.PositiveInfinity;
}

/// <summary>Les réglages TPI qu'on déduit d'un modèle, et ce qu'il faut en savoir avant de les appliquer.</summary>
public sealed record TpiProposal(double Kp, double Ki, double Kint, IReadOnlyList<string> Notes);

/// <summary>
/// Auto-TPI par modèle thermique (conception du 25/07/2026 pour DomoMud), en fonctions pures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pourquoi pas l'auto-TPI de DomoMud</b> (C10 : Kp et Ki poussés par petits pas selon l'erreur moyenne) : ici le TPI a
/// une intégrale, qui corrige déjà le biais permanent ; deux correcteurs du même biais se battraient. On identifie plutôt
/// la pièce, et le réglage en découle.
/// </para>
/// <para>
/// <b>Le modèle est linéaire en (g, k, c)</b> : sur chaque intervalle, <c>ΔT/Δt = g·u − k·(T − Text) + c</c> ; une
/// régression aux moindres carrés suffit. Puis, le TPI calculant <c>Kp·e + Ki·(cible − Text) + Kint·∫e</c> :
/// </para>
/// <list type="bullet">
/// <item><b>Ki = k/g</b> exactement : c'est la puissance qu'il faut, à l'équilibre, par degré d'écart avec l'extérieur.
/// Avec lui, il ne reste plus de biais permanent pour l'intégrale.</item>
/// <item><b>Kp = 1/(g·λ)</b> (réglage IMC) : λ est le temps de réponse qu'on demande, et c'est là que le type de chauffage
/// entre — un plancher ne se pilote pas comme un radiateur électrique.</item>
/// <item><b>Kint = Kp·k</b> (temps d'intégration = constante de temps de la pièce) : modéré, puisque Ki fait l'essentiel.</item>
/// </list>
/// <para>
/// Tout est borné (bornes C10 pour Kp et Ki). Le modèle n'est proposé que s'il a de quoi être cru : assez d'intervalles,
/// une chauffe qui a varié (sinon g et c ne se séparent pas), un écart intérieur − extérieur qui a varié (sinon k et c ne
/// se séparent pas), des valeurs physiques (g et k positifs), et un R² suffisant.
/// </para>
/// </remarks>
public static class ThermalModel
{
    /// <summary>24 h d'intervalles de 30 min.</summary>
    public const int MinIntervals = 48;

    public const double MinR2 = 0.5;

    /// <summary>Écart-type minimal de la part de chauffe : un radiateur toujours éteint ne dit rien de sa puissance.</summary>
    public const double MinDutySpread = 0.1;

    /// <summary>Amplitude minimale de l'écart intérieur − extérieur, en °C.</summary>
    public const double MinGapRange = 2.0;

    public const double KpMin = 0.2, KpMax = 1.5, KiMax = 0.10, KintMax = 0.10;

    /// <summary>Temps de réponse demandé à la régulation, en heures.</summary>
    public static double Lambda(HeatingType type) => type switch
    {
        HeatingType.Water => 2.0,
        HeatingType.Floor => 4.0,
        _ => 1.0,
    };

    public static ThermalIdentification Identify(IReadOnlyList<ThermalInterval> intervalles)
    {
        var ok = intervalles.Where(i => i.Hours > 0 && double.IsFinite(i.IndoorStart) && double.IsFinite(i.IndoorEnd)
                                        && double.IsFinite(i.Duty) && double.IsFinite(i.IndoorOutdoorGap)).ToList();
        var heures = ok.Sum(i => i.Hours);
        ThermalIdentification Refus(string motif) => new(0, 0, 0, 0, ok.Count, heures, motif);

        if (ok.Count < MinIntervals) return Refus($"{ok.Count} intervalle(s) exploitable(s) sur {MinIntervals} nécessaires : laisser l'historique se remplir");
        if (EcartType(ok.Select(i => i.Duty)) < MinDutySpread) return Refus("la chauffe n'a presque pas varié : impossible d'en mesurer l'effet");
        if (ok.Max(i => i.IndoorOutdoorGap) - ok.Min(i => i.IndoorOutdoorGap) < MinGapRange)
            return Refus("l'écart avec l'extérieur n'a presque pas varié : impossible d'en mesurer les déperditions");

        // y = g·u − k·gap + c : moindres carrés sur (u, −gap, 1), par les équations normales (3×3).
        var a = new double[3, 3];
        var b = new double[3];
        foreach (var i in ok)
        {
            double[] x = [i.Duty, -i.IndoorOutdoorGap, 1];
            var y = (i.IndoorEnd - i.IndoorStart) / i.Hours;
            for (var r = 0; r < 3; r++)
            {
                b[r] += x[r] * y;
                for (var c = 0; c < 3; c++) a[r, c] += x[r] * x[c];
            }
        }

        if (Resoudre(a, b) is not { } s) return Refus("données dégénérées (variables liées entre elles)");
        var (g, k, libre) = (s[0], s[1], s[2]);

        var moyenne = ok.Average(i => (i.IndoorEnd - i.IndoorStart) / i.Hours);
        double residus = 0, total = 0;
        foreach (var i in ok)
        {
            var y = (i.IndoorEnd - i.IndoorStart) / i.Hours;
            var prevu = g * i.Duty - k * i.IndoorOutdoorGap + libre;
            residus += (y - prevu) * (y - prevu);
            total += (y - moyenne) * (y - moyenne);
        }

        var r2 = total > 0 ? 1 - residus / total : 0;
        var modele = new ThermalIdentification(g, k, libre, r2, ok.Count, heures, null);
        if (g <= 0) return modele with { Refusal = "gain de chauffe nul ou négatif : le radiateur suivi chauffe-t-il cette pièce ?" };
        if (k <= 0) return modele with { Refusal = "déperditions nulles ou négatives : sonde extérieure mal placée (au soleil, contre un mur chaud) ?" };
        if (r2 < MinR2) return modele with { Refusal = $"modèle peu fiable (R² {r2:0.00} < {MinR2:0.0}) : sonde bruitée, fenêtres ouvertes, autre chauffage ?" };
        return modele;
    }

    /// <summary>Les réglages TPI que le modèle donne pour ce type de chauffage, bornés, avec ce qui a été borné.</summary>
    public static TpiProposal Derive(ThermalIdentification m, HeatingType type)
    {
        if (!m.Usable) throw new ArgumentException($"modèle inutilisable : {m.Refusal}");
        var notes = new List<string>();

        double Borner(double v, double min, double max, string nom, string pourquoi)
        {
            if (v >= min && v <= max) return v;
            notes.Add($"{nom} ramené de {v:0.###} à {Math.Clamp(v, min, max):0.###} : {pourquoi}");
            return Math.Clamp(v, min, max);
        }

        var kp = Borner(1 / (m.Gain * Lambda(type)), KpMin, KpMax, "Kp", "borne de sécurité");
        var ki = Borner(m.LossRate / m.Gain, 0, KiMax, "Ki",
            "le radiateur semble sous-dimensionné pour la pièce (il ne tiendrait pas la consigne par grand froid)");
        var kint = Borner(kp * m.LossRate, 0, KintMax, "Kint", "borne de sécurité");
        return new TpiProposal(Math.Round(kp, 3), Math.Round(ki, 4), Math.Round(kint, 4), notes);
    }

    private static double EcartType(IEnumerable<double> valeurs)
    {
        var liste = valeurs.ToList();
        var m = liste.Average();
        return Math.Sqrt(liste.Sum(v => (v - m) * (v - m)) / liste.Count);
    }

    /// <summary>Gauss avec pivot partiel ; null si le système est singulier.</summary>
    private static double[]? Resoudre(double[,] a, double[] b)
    {
        const int n = 3;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(m[r, col]));
            if (Math.Abs(m[pivot, col]) < 1e-12) return null;
            if (pivot != col)
            {
                for (var c = 0; c < n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (v[col], v[pivot]) = (v[pivot], v[col]);
            }

            for (var r = col + 1; r < n; r++)
            {
                var f = m[r, col] / m[col, col];
                for (var c = col; c < n; c++) m[r, c] -= f * m[col, c];
                v[r] -= f * v[col];
            }
        }

        var x = new double[n];
        for (var r = n - 1; r >= 0; r--)
        {
            var s = v[r];
            for (var c = r + 1; c < n; c++) s -= m[r, c] * x[c];
            x[r] = s / m[r, r];
        }

        return x;
    }
}
