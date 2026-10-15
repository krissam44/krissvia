using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Core.Configuration;

namespace Krissvia.PluginHost.Services;

/// <summary>Ce que le superviseur transmet à un plugin au lancement, en plus de l'adresse du cœur et du jeton.</summary>
public interface IPluginSettingsProvider
{
    IReadOnlyDictionary<string, string> EnvironmentFor(string pluginId);
}

/// <summary>
/// Réglages des plugins saisis dans la page Services : enregistrés en base (collection <c>plugin_settings</c>) et
/// transmis au plugin à chaque lancement, prioritaires sur son <c>appsettings.Local.json</c>.
/// </summary>
/// <remarks>
/// Les secrets (mot de passe du broker) sont en clair dans la base, comme ceux de DomoMud : la base reste en mode
/// 600, et ses sauvegardes aussi.
/// </remarks>
public sealed class PluginSettingsService(IConfigurationStore store) : IPluginSettingsProvider
{
    public const string Collection = "plugin_settings";

    private readonly ConcurrentDictionary<string, JsonObject> _enregistres = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var document in await store.ListAsync(Collection, ct))
        {
            try
            {
                if (JsonNode.Parse(document.Json) is JsonObject valeur) _enregistres[document.Id] = valeur;
            }
            catch (JsonException)
            {
                // Illisible : le plugin retombe sur son propre fichier, la page proposera les valeurs par défaut.
            }
        }
    }

    /// <summary>Les réglages à afficher : ceux enregistrés, complétés par les valeurs par défaut de la fiche.</summary>
    public JsonObject Get(PluginManifest plugin)
    {
        var schema = SettingsSchema.Parse(plugin.Settings);
        var enregistre = _enregistres.TryGetValue(plugin.Id, out var v) ? (JsonObject)v.DeepClone() : null;
        return schema is null ? enregistre ?? [] : SettingsSchema.Complete(schema, enregistre);
    }

    /// <summary>Vrai si des réglages ont déjà été saisis dans la page pour ce plugin.</summary>
    public bool HasSaved(string pluginId) => _enregistres.ContainsKey(pluginId);

    /// <summary>Enregistre après validation. Renvoie les erreurs ; vide si c'est enregistré.</summary>
    public async Task<IReadOnlyList<string>> SaveAsync(PluginManifest plugin, JsonObject valeur, CancellationToken ct = default)
    {
        if (SettingsSchema.Parse(plugin.Settings) is not { } schema) return ["ce plugin ne déclare aucun réglage."];

        var complet = SettingsSchema.Complete(schema, valeur);
        var erreurs = SettingsSchema.Validate(schema, complet);
        if (erreurs.Count > 0) return erreurs;

        await store.SaveAsync(Collection, new ConfigurationDocument(plugin.Id, complet.ToJsonString()), ct);
        _enregistres[plugin.Id] = complet;
        return [];
    }

    public IReadOnlyDictionary<string, string> EnvironmentFor(string pluginId) =>
        _enregistres.TryGetValue(pluginId, out var v) ? SettingsSchema.ToEnvironment(v) : new Dictionary<string, string>();
}
