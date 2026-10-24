using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost.Services;

/// <summary>
/// La sortie d'un plugin, relue pour le journal du cœur (journalctl) : sans elle, un plugin qui n'arrive pas au broker ne
/// le disait que sur sa fiche de la page Services — rien à lire en SSH. Le format console de .NET écrit deux lignes par
/// message (« warn: Catégorie[id] » puis le texte, indenté) : l'en-tête donne le niveau du texte qui suit, et n'est pas
/// répété. Une ligne libre (plugin Python, trace d'exception) passe en information.
/// </summary>
public sealed class PluginJournal
{
    private LogLevel? _niveau;

    /// <summary>Ce qu'il faut écrire pour cette ligne, ou null (en-tête retenu, ligne vide).</summary>
    public (LogLevel Level, string Text)? Read(string ligne)
    {
        if (string.IsNullOrWhiteSpace(ligne)) return null;

        if (Entete(ligne) is { } niveau)
        {
            _niveau = niveau;
            return null;
        }

        var texte = ligne.Trim();
        if (char.IsWhiteSpace(ligne[0]) && _niveau is { } n) return (n, texte);

        _niveau = null;   // une ligne non indentée clôt le message en cours
        return (LogLevel.Information, texte);
    }

    private static LogLevel? Entete(string ligne)
    {
        if (ligne.Length < 6 || ligne[4] != ':' || !ligne.TrimEnd().EndsWith(']')) return null;
        return ligne[..4] switch
        {
            "trce" => LogLevel.Trace,
            "dbug" => LogLevel.Debug,
            "info" => LogLevel.Information,
            "warn" => LogLevel.Warning,
            "fail" => LogLevel.Error,
            "crit" => LogLevel.Critical,
            _ => null,
        };
    }
}
