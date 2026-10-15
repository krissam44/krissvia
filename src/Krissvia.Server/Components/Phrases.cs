using System.Globalization;
using System.Text;

namespace Krissvia.Server.Components;

internal static class Phrases
{
    /// <summary>Les refus des services sont des fragments (« mot de passe actuel faux. ») : on en fait une phrase.</summary>
    public static string Majuscule(string texte) => texte.Length == 0 ? texte : char.ToUpperInvariant(texte[0]) + texte[1..];

    /// <summary>« Chambre RdC » → « chambre_rdc ».</summary>
    public static string Identifiant(string nom)
    {
        var sb = new StringBuilder();
        foreach (var c in nom.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var minuscule = char.ToLowerInvariant(c);
            if (minuscule is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(minuscule);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }

        var id = sb.ToString().Trim('_');
        return id.Length > 64 ? id[..64] : id;
    }
}
