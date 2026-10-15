using System.Globalization;
using System.Text;
using Krissvia.Contracts;

namespace Krissvia.Core.Registry;

/// <summary>Fabrique des identifiants lisibles et stables : <c>switch.prise_salon</c>.</summary>
public static class EntityIdGenerator
{
    public static string Generate(EntityKind kind, string deviceName, string? suffix, ISet<string> taken)
    {
        var candidat = $"{Domain(kind)}.{Slugify(suffix is null ? deviceName : $"{deviceName} {suffix}")}";
        if (!taken.Contains(candidat)) return candidat;

        for (var i = 2; ; i++)
        {
            var avecNumero = $"{candidat}_{i}";
            if (!taken.Contains(avecNumero)) return avecNumero;
        }
    }

    public static string Domain(EntityKind kind) => kind switch
    {
        EntityKind.Sensor => "sensor",
        EntityKind.BinarySensor => "binary_sensor",
        EntityKind.Switch => "switch",
        EntityKind.Light => "light",
        EntityKind.Cover => "cover",
        EntityKind.Event => "event",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Minuscules, sans accents, séparateurs réduits à un seul « _ ».</summary>
    public static string Slugify(string texte)
    {
        var normalise = texte
            .Replace("œ", "oe").Replace("Œ", "oe")
            .Replace("æ", "ae").Replace("Æ", "ae")
            .Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder();
        var dernierEstSeparateur = true; // évite un « _ » en tête

        foreach (var c in normalise)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
                dernierEstSeparateur = false;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(c));
                dernierEstSeparateur = false;
            }
            else if (!dernierEstSeparateur)
            {
                sb.Append('_');
                dernierEstSeparateur = true;
            }
        }

        var slug = sb.ToString().TrimEnd('_');
        return slug.Length == 0 ? "entite" : slug;
    }
}
