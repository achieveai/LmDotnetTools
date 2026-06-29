using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     A structured condition AST used by conditional-node branches. A node is exactly one shape:
///     a leaf (<see cref="Op"/> + <see cref="Path"/> + optional <see cref="Value"/>) or a composite
///     (<see cref="All"/>, <see cref="Any"/>, or <see cref="Not"/>). The validator can sanity-check
///     that exactly one shape is used.
/// </summary>
/// <remarks>
///     Conditions are parsed by <see cref="ConditionJsonConverter"/> rather than the default
///     serializer so that an unrecognized operator string does not throw during deserialization;
///     instead it is preserved in <see cref="UnknownOp"/> for the validator to report.
/// </remarks>
public sealed record Condition
{
    /// <summary>The comparison operator for a leaf condition; <c>null</c> for composites.</summary>
    public ConditionOp? Op { get; init; }

    /// <summary>A dotted state/inputs path the leaf condition reads (for example <c>state.count</c>).</summary>
    public string? Path { get; init; }

    /// <summary>The comparison value for a leaf condition, kept as an arbitrary JSON node.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>Child conditions that must ALL be true (logical AND composite).</summary>
    public IReadOnlyList<Condition>? All { get; init; }

    /// <summary>Child conditions where ANY may be true (logical OR composite).</summary>
    public IReadOnlyList<Condition>? Any { get; init; }

    /// <summary>A single child condition that is negated (logical NOT composite).</summary>
    public Condition? Not { get; init; }

    /// <summary>
    ///     The raw operator string when it did not map to a known <see cref="ConditionOp"/>. This is
    ///     populated by the converter and inspected by the validator to emit an "unknown condition op"
    ///     error. It is never set when <see cref="Op"/> is set.
    /// </summary>
    [JsonIgnore]
    internal string? UnknownOp { get; init; }
}
