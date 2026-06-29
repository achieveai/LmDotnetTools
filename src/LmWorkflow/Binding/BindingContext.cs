using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>
///     The read model that backs template rendering and condition evaluation. It exposes the workflow data
///     channels (<see cref="Inputs"/>, <see cref="State"/>, <see cref="Outputs"/>, <see cref="Notes"/>),
///     the loop bookkeeping (<see cref="Visits"/>, <see cref="Step"/>), and the optional per-iteration loop
///     locals (<see cref="Item"/>, <see cref="Index"/>, <see cref="Count"/>). All channels are backed by
///     <see cref="JsonNode"/> so authored paths can address arbitrary nested shapes.
/// </summary>
/// <remarks>
///     <see cref="Resolve"/> never throws: an absent path, a navigation into a non-container, or an
///     out-of-range index all return <c>null</c>.
/// </remarks>
public sealed class BindingContext
{
    /// <summary>The workflow inputs channel, addressed as <c>inputs.&lt;...&gt;</c>.</summary>
    public JsonObject Inputs { get; init; } = [];

    /// <summary>The workflow mutable state channel, addressed as <c>state.&lt;...&gt;</c>.</summary>
    public JsonObject State { get; init; } = [];

    /// <summary>
    ///     The per-node task outputs channel, shaped as <c>{ "&lt;nodeId&gt;": { "&lt;taskId&gt;": value } }</c>
    ///     and addressed as <c>outputs.&lt;nodeId&gt;.&lt;taskId&gt;</c> (with optional trailing indices).
    /// </summary>
    public JsonObject Outputs { get; init; } = [];

    /// <summary>
    ///     The scoped notes channel, shaped as <c>{ "&lt;scope&gt;": { "&lt;key&gt;": value } }</c> and
    ///     addressed as <c>notes.&lt;scope&gt;.&lt;key&gt;</c>.
    /// </summary>
    public JsonObject Notes { get; init; } = [];

    /// <summary>Per-node visit counts, addressed as <c>visits.&lt;nodeId&gt;</c> (0 when absent).</summary>
    public IReadOnlyDictionary<string, int> Visits { get; init; } =
        new Dictionary<string, int>();

    /// <summary>The current global step counter, addressed as <c>step</c>.</summary>
    public int Step { get; init; }

    /// <summary>The current loop item (<c>item</c>), or <c>null</c> when not rendering inside a loop.</summary>
    public JsonNode? Item { get; init; }

    /// <summary>The current loop index (<c>index</c>), or <c>null</c> when not in a loop.</summary>
    public int? Index { get; init; }

    /// <summary>The loop element count (<c>count</c>), or <c>null</c> when not in a loop.</summary>
    public int? Count { get; init; }

    /// <summary>
    ///     Returns a context that shares this instance's data channels (same <see cref="JsonObject"/>
    ///     references and visit map) but carries the supplied loop locals, for rendering a single
    ///     <c>forEach</c> task iteration.
    /// </summary>
    /// <param name="item">The element bound to <c>item</c> for this iteration (may be <c>null</c>).</param>
    /// <param name="index">The zero-based iteration index bound to <c>index</c>.</param>
    /// <param name="count">The total element count bound to <c>count</c>.</param>
    public BindingContext WithLoop(JsonNode? item, int index, int count) =>
        new()
        {
            Inputs = Inputs,
            State = State,
            Outputs = Outputs,
            Notes = Notes,
            Visits = Visits,
            Step = Step,
            Item = item,
            Index = index,
            Count = count,
        };

    /// <summary>
    ///     Resolves a direct binding path against this context. Supported roots: <c>inputs</c>, <c>state</c>,
    ///     <c>outputs</c>, <c>notes</c>, <c>visits.&lt;nodeId&gt;</c>, <c>step</c>, <c>item</c>, <c>index</c>
    ///     and <c>count</c>. Returns the bound <see cref="JsonNode"/>, or <c>null</c> when the path is
    ///     absent, malformed, or out of range. Never throws.
    /// </summary>
    public JsonNode? Resolve(string path)
    {
        var segments = JsonPath.Parse(path);
        if (segments is null || segments.Count == 0 || segments[0].IsIndex)
        {
            return null;
        }

        var root = segments[0].Name;
        return root switch
        {
            "inputs" => JsonPath.Navigate(Inputs, segments, 1),
            "state" => JsonPath.Navigate(State, segments, 1),
            "outputs" => JsonPath.Navigate(Outputs, segments, 1),
            "notes" => JsonPath.Navigate(Notes, segments, 1),
            "visits" => ResolveVisits(segments),
            "step" => JsonPath.Navigate(JsonValue.Create(Step), segments, 1),
            "item" => JsonPath.Navigate(Item, segments, 1),
            "index" => Index is { } i ? JsonPath.Navigate(JsonValue.Create(i), segments, 1) : null,
            "count" => Count is { } c ? JsonPath.Navigate(JsonValue.Create(c), segments, 1) : null,
            _ => null,
        };
    }

    private JsonNode? ResolveVisits(IReadOnlyList<PathSegment> segments)
    {
        // visits.<nodeId> -> the node's visit count (0 when absent). Any deeper navigation into the
        // resulting integer naturally yields null via JsonPath.Navigate.
        if (segments.Count < 2 || segments[1].IsIndex)
        {
            return null;
        }

        var count = Visits.TryGetValue(segments[1].Name!, out var visits) ? visits : 0;
        return JsonPath.Navigate(JsonValue.Create(count), segments, 2);
    }
}
