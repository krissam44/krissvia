using System.Globalization;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>
/// Contraste des couleurs poussées vers une platine (repris de DomoMud). Les couleurs de mode et de statut sont celles
/// du tableau de bord, pensées pour un fond très sombre : repeindre le fond d'une platine les rend illisibles sans que
/// rien ne le signale (le mode Off, #546e7a, tombe à 2,5:1 sur #1B332E, sous le seuil de 3:1 d'un texte d'interface).
/// C'est donc à l'affichage de s'adapter à son support, pas à la donnée.
/// </summary>
public static class CouleurOpenHasp
{
    /// <summary>Luminance relative sRGB (WCAG), de 0 (noir) à 1 (blanc).</summary>
    public static double Luminance(string hex)
    {
        var (r, g, b) = Composantes(hex);
        return 0.2126 * Canal(r) + 0.7152 * Canal(g) + 0.0722 * Canal(b);

        static double Canal(int v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>Rapport de contraste WCAG entre deux couleurs, de 1:1 à 21:1.</summary>
    public static double Contraste(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    /// <summary>
    /// Rapproche <paramref name="couleur"/> du blanc (fond sombre) ou du noir (fond clair) juste assez pour atteindre
    /// <paramref name="cible"/> — jamais d'un coup jusqu'au blanc, qui effacerait le code couleur des modes. Une valeur
    /// qui n'est pas #RRGGBB passe telle quelle : une couleur fausse vaut mieux qu'une exception qui perd tout le
    /// rafraîchissement de la platine. Ne s'applique qu'aux TEXTES : éclaircir un aplat délaverait le même code.
    /// </summary>
    public static string Lisible(string couleur, string fond, double cible = 3.0)
    {
        if (cible <= 0 || !EstHex(couleur) || !EstHex(fond)) return couleur;
        if (Contraste(couleur, fond) >= cible) return couleur;

        var extreme = Luminance(fond) < 0.18 ? "#FFFFFF" : "#000000";
        var (r, g, b) = Composantes(couleur);
        var (er, eg, eb) = Composantes(extreme);
        for (var i = 1; i <= 20; i++)
        {
            var t = i / 20.0;
            var essai = Hex((int)Math.Round(r + (er - r) * t), (int)Math.Round(g + (eg - g) * t), (int)Math.Round(b + (eb - b) * t));
            if (Contraste(essai, fond) >= cible) return essai;
        }

        return extreme;
    }

    /// <summary>
    /// La couleur rapprochée du blanc de <paramref name="part"/> (0 à 1) : le panneau de la consigne, un ton au-dessus du
    /// fond quel qu'il soit, comme le vert clair sur vert sombre de DomoMud. Une valeur qui n'est pas #RRGGBB passe telle quelle.
    /// </summary>
    public static string Eclaircir(string couleur, double part)
    {
        if (!EstHex(couleur)) return couleur;
        var (r, g, b) = Composantes(couleur);
        int Vers(int c) => (int)Math.Round(c + (255 - c) * Math.Clamp(part, 0, 1));
        return Hex(Vers(r), Vers(g), Vers(b));
    }

    private static bool EstHex(string? s) =>
        s is { Length: 7 } && s[0] == '#' && int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    private static (int R, int G, int B) Composantes(string hex) =>
        !EstHex(hex)
            ? (0, 0, 0)
            : (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Hex(int r, int g, int b) => $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}";
}
