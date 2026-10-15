using Krissvia.Contracts;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost;

/// <summary>Messages du journal du PluginHost (générés à la compilation).</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} connecté (version {PluginVersion}, contrat {ContractVersion}).")]
    public static partial void SessionOuverte(ILogger logger, string pluginId, string pluginVersion, string contractVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} déconnecté.")]
    public static partial void SessionFermee(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} reconnecté : l'ancienne session est remplacée.")]
    public static partial void SessionRemplacee(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session refusée (plugin {PluginId}) : {Reason}, {Detail}.")]
    public static partial void SessionRefusee(ILogger logger, string pluginId, RejectReason reason, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} : arrêt annoncé ({Reason}).")]
    public static partial void ArretAnnonce(ILogger logger, string pluginId, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} muet depuis {Secondes} s : session fermée, ses entités deviennent indisponibles.")]
    public static partial void PluginMuet(ILogger logger, string pluginId, int secondes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : envoi impossible.")]
    public static partial void EnvoiImpossible(ILogger logger, string pluginId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} : message de type « {Type} » inconnu, ignoré (contrat plus récent ?).")]
    public static partial void TypeInconnu(ILogger logger, string pluginId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : trame ignorée, {Error}.")]
    public static partial void TrameInvalide(ILogger logger, string pluginId, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin {PluginId} : échec du traitement d'un message {Type}.")]
    public static partial void MessageEnEchec(ILogger logger, string pluginId, string type, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : message {Type} inattendu dans ce sens, ignoré.")]
    public static partial void MessageInattendu(ILogger logger, string pluginId, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} : {Appareils} appareil(s) déclaré(s), {Creees} entité(s) créée(s), {Manquantes} devenue(s) manquante(s).")]
    public static partial void Declaration(ILogger logger, string pluginId, int appareils, int creees, int manquantes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : état reçu pour l'entité « {UniqueId} », jamais déclarée. Ignoré (signalé une seule fois).")]
    public static partial void EntiteInconnue(ILogger logger, string pluginId, string uniqueId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : état sans valeur pour « {UniqueId} », ignoré.")]
    public static partial void ValeurAbsente(ILogger logger, string pluginId, string uniqueId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : joignabilité d'un appareil non déclaré « {UniqueId} », ignorée.")]
    public static partial void AppareilInconnu(ILogger logger, string pluginId, string uniqueId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : résultat de la commande {CommandId}, qui n'est plus attendue (délai dépassé ?).")]
    public static partial void ResultatOrphelin(ILogger logger, string pluginId, string commandId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} : configuration acceptée.")]
    public static partial void ConfigurationAcceptee(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin {PluginId} : configuration inutilisable, {Error}.")]
    public static partial void ConfigurationRefusee(ILogger logger, string pluginId, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} : {Nombre} appareil(s) découvert(s).")]
    public static partial void Decouverte(ILogger logger, string pluginId, int nombre);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} : pas de réponse à la commande {Command} sur « {UniqueId} ».")]
    public static partial void CommandeSansReponse(ILogger logger, string pluginId, string command, string uniqueId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commande {Command} vers « {EntityId} » : le plugin {PluginId} n'est pas connecté.")]
    public static partial void CommandePluginAbsent(ILogger logger, string pluginId, string entityId, string command);
}
