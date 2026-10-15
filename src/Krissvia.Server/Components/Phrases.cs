namespace Krissvia.Server.Components;

internal static class Phrases
{
    /// <summary>Les refus des services sont des fragments (« mot de passe actuel faux. ») : on en fait une phrase.</summary>
    public static string Majuscule(string texte) => texte.Length == 0 ? texte : char.ToUpperInvariant(texte[0]) + texte[1..];
}
