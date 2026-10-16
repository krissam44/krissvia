using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Core.Configuration;

namespace Krissvia.Server.Components.Layout;

/// <summary>Préférences d'affichage propres à un compte.</summary>
public sealed record UserPreferences(bool DarkMode = false);

/// <summary>
/// Préférences d'affichage par compte (collection <c>preferences</c>, un document par identifiant) : le mode sombre
/// suit la personne, sur tous ses navigateurs, au lieu de dépendre de celui qu'elle utilise.
/// </summary>
public sealed class PreferencesService(IConfigurationStore store)
{
    public const string Collection = "preferences";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly ConcurrentDictionary<string, UserPreferences> _connues = new(StringComparer.Ordinal);

    public async Task<UserPreferences> GetAsync(string user, CancellationToken ct = default)
    {
        if (_connues.TryGetValue(user, out var connues)) return connues;

        var preferences = new UserPreferences();
        if ((await store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == user) is { } document)
        {
            try
            {
                preferences = JsonSerializer.Deserialize<UserPreferences>(document.Json, Json) ?? preferences;
            }
            catch (JsonException)
            {
                // Des préférences illisibles valent les préférences par défaut.
            }
        }

        return _connues[user] = preferences;
    }

    public async Task SaveAsync(string user, UserPreferences preferences, CancellationToken ct = default)
    {
        await store.SaveAsync(Collection, new ConfigurationDocument(user, JsonSerializer.Serialize(preferences, Json)), ct);
        _connues[user] = preferences;
    }
}
