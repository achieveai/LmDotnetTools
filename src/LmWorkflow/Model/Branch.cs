using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     A single branch of a <see cref="ConditionalNode"/>. The branch condition may be expressed as a
///     prose <c>when</c> string, a structured <c>when</c> object, or an explicit <see cref="Condition"/>
///     sibling. Use <see cref="StructuredCondition"/> to obtain the parsed structured form (if any).
/// </summary>
public sealed record Branch
{
    /// <summary>
    ///     The branch trigger as authored. A JSON string is prose (no structured form); a JSON object is
    ///     a structured condition that <see cref="StructuredCondition"/> parses into a <see cref="Condition"/>.
    /// </summary>
    public JsonNode? When { get; init; }

    /// <summary>An explicit structured condition that takes precedence over <see cref="When"/>.</summary>
    public Condition? Condition { get; init; }

    /// <summary>The id of the node this branch transitions to when its condition holds.</summary>
    public required string To { get; init; }

    /// <summary>
    ///     The structured condition for this branch: the explicit <see cref="Condition"/> sibling if set,
    ///     otherwise the parsed <see cref="When"/> object, otherwise <c>null</c> when <see cref="When"/>
    ///     is prose (or absent).
    /// </summary>
    [JsonIgnore]
    public Condition? StructuredCondition =>
        Condition ?? (When is JsonObject ? ConditionJsonConverter.FromNode(When) : null);
}
