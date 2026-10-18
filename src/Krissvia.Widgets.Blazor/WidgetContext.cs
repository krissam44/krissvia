using System.Text.Json;

namespace Krissvia.Widgets;

/// <summary>Une entité liée à un slot, telle que le widget la voit (WIDGETS.md §4).</summary>
public sealed record WidgetEntity(
    string EntityId,
    string Name,
    string Kind,
    string? DeviceClass,
    string? Unit,
    IReadOnlyList<string> Commands,
    string? Room,
    JsonElement? Extra);

/// <summary>Dernier état d'une entité. <see cref="Available"/> faux : plugin arrêté ou appareil injoignable.</summary>
public sealed record WidgetState(JsonElement Value, bool Available, DateTimeOffset MeasuredAt)
{
    public double? Number => Available && Value.ValueKind == JsonValueKind.Number ? Value.GetDouble() : null;

    public bool? Boolean => Available && Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? Value.GetBoolean() : null;
}

/// <summary>
/// Ce qu'il est advenu d'un ordre. <see cref="Confirmed"/> : l'état a suivi. Un widget n'affiche pas « allumé »
/// parce qu'il a cliqué : il attend l'état suivant, qui arrive dans le contexte (WIDGETS.md §5).
/// </summary>
public sealed record WidgetCommandResult(string Outcome, string Verification, string? Detail = null)
{
    public bool Accepted => Outcome == "accepted";

    public bool Confirmed => Verification == "confirmed";
}

/// <summary>
/// Ce que reçoit un widget Blazor, en paramètre <c>Context</c> : les mêmes données qu'un Web Component, et l'envoi
/// d'ordres. Un nouveau contexte arrive à chaque changement d'état d'une de ses entités.
/// </summary>
public sealed class WidgetContext(
    IReadOnlyDictionary<string, WidgetEntity> entities,
    IReadOnlyDictionary<string, WidgetState?> states,
    JsonElement settings,
    Func<string, string, object?, Task<WidgetCommandResult>> command)
{
    /// <summary>Par slot ; absent si le slot facultatif n'est pas lié.</summary>
    public IReadOnlyDictionary<string, WidgetEntity> Entities { get; } = entities;

    /// <summary>Par slot ; null si l'entité n'a jamais publié (ce n'est pas zéro).</summary>
    public IReadOnlyDictionary<string, WidgetState?> States { get; } = states;

    /// <summary>Les options de la carte (objet JSON, éventuellement vide).</summary>
    public JsonElement Settings { get; } = settings;

    public WidgetState? State(string slot) => States.GetValueOrDefault(slot);

    public WidgetEntity? Entity(string slot) => Entities.GetValueOrDefault(slot);

    /// <summary>Une option numérique de la carte, ou <paramref name="defaut"/>.</summary>
    public double Setting(string name, double defaut) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : defaut;

    /// <summary>
    /// Envoie un ordre à l'entité d'un slot (et à elle seule : un widget ne vise que ses slots). Vérifié comme tous
    /// les ordres de Krissvia ; ne lève pas d'exception, le refus est dans le résultat.
    /// </summary>
    public Task<WidgetCommandResult> CommandAsync(string slot, string command, object? args = null) => commandAsync(slot, command, args);

    private readonly Func<string, string, object?, Task<WidgetCommandResult>> commandAsync = command;
}
