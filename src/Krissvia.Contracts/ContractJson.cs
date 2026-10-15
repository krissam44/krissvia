using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Krissvia.Contracts;

/// <summary>
/// Réglages JSON du contrat. Ils font partie du contrat : un plugin écrit dans un autre langage
/// parle exactement ce JSON (snake_case, enums en chaînes, champ « type » pour les messages).
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Creer();

    public static string Encode(Message message) => JsonSerializer.Serialize(message, Options);

    /// <summary>
    /// Lit une trame reçue sans jamais lever d'exception. Un type inconnu (message d'une version future du
    /// contrat) se distingue d'une trame invalide : le premier s'ignore, la seconde se signale.
    /// </summary>
    public static DecodedFrame Decode(string json)
    {
        string? type;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return DecodedFrame.Invalid("la trame n'est pas un objet JSON");

            type = document.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            return DecodedFrame.Invalid($"JSON illisible : {ex.Message}");
        }

        if (type is null) return DecodedFrame.Invalid("champ « type » absent");
        if (!TypesConnus.Contains(type)) return DecodedFrame.Unknown(type);

        try
        {
            var message = JsonSerializer.Deserialize<Message>(json, Options);
            return message is null ? DecodedFrame.Invalid("message vide") : DecodedFrame.Ok(message);
        }
        catch (JsonException ex)
        {
            return DecodedFrame.Invalid($"message « {type} » invalide : {ex.Message}");
        }
    }

    private static readonly HashSet<string> TypesConnus = typeof(Message)
        .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
        .Cast<JsonDerivedTypeAttribute>()
        .Select(a => (string)a.TypeDiscriminator!)
        .ToHashSet(StringComparer.Ordinal);

    private static JsonSerializerOptions Creer()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Les autres langages n'écrivent pas forcément « type » en premier.
            AllowOutOfOrderMetadataProperties = true,
            // Accents écrits tels quels (« cœur », pas « cœur ») : le JSON se lit à l'œil dans les journaux
            // et les outils. Sans risque ici, ces trames ne sont jamais insérées dans du HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

/// <summary>Résultat de <see cref="ContractJson.Decode"/> : un message, un type inconnu, ou une trame invalide.</summary>
public sealed record DecodedFrame
{
    public Message? Message { get; private init; }
    public string? UnknownType { get; private init; }
    public string? Error { get; private init; }

    public static DecodedFrame Ok(Message message) => new() { Message = message };
    public static DecodedFrame Unknown(string type) => new() { UnknownType = type };
    public static DecodedFrame Invalid(string error) => new() { Error = error };
}
