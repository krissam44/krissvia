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

    private static JsonSerializerOptions Creer()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Les autres langages n'écrivent pas forcément « type » en premier.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
