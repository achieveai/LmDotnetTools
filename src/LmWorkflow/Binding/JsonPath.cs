using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>
///     A single segment of a direct binding path: either an object property name (for example
///     <c>summaries</c>) or a zero-based array index (for example the <c>0</c> in <c>lint[0]</c>).
/// </summary>
internal readonly struct PathSegment
{
    private PathSegment(string? name, int? index)
    {
        Name = name;
        Index = index;
    }

    /// <summary>The property name when this segment addresses an object member; otherwise <c>null</c>.</summary>
    public string? Name { get; }

    /// <summary>The array index when this segment addresses an array element; otherwise <c>null</c>.</summary>
    public int? Index { get; }

    /// <summary>Whether this segment is an array index rather than a property name.</summary>
    public bool IsIndex => Index.HasValue;

    /// <summary>Creates a property (object member) segment.</summary>
    public static PathSegment Property(string name) => new(name, null);

    /// <summary>Creates an array-index segment.</summary>
    public static PathSegment ArrayIndex(int index) => new(null, index);
}

/// <summary>
///     Parsing and navigation for V1 direct binding paths. A path is a sequence of <c>.field</c> property
///     segments and <c>[i]</c> integer-index segments (for example <c>outputs.review.lint[0].severity</c>).
///     The richer pipeline grammar (<c>[*]</c>, <c>[?]</c>, <c>|top</c>, inline filters, ...) is out of
///     scope for V1; an unsupported or malformed segment causes parsing to fail (returns <c>null</c>) and
///     navigation to yield <c>null</c> rather than throwing.
/// </summary>
internal static class JsonPath
{
    /// <summary>
    ///     Parses a direct path into its ordered segments. Returns <c>null</c> for an empty/whitespace path
    ///     or for any malformed segment (a non-integer or unterminated index), so callers can treat the
    ///     whole path as unresolvable without throwing.
    /// </summary>
    public static IReadOnlyList<PathSegment>? Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = new List<PathSegment>();
        var buffer = new System.Text.StringBuilder();
        var inIndex = false;

        foreach (var c in path)
        {
            switch (c)
            {
                case '.':
                    if (inIndex)
                    {
                        return null; // '.' inside an index is malformed.
                    }

                    FlushName(segments, buffer);
                    break;

                case '[':
                    if (inIndex)
                    {
                        return null; // nested '[' is malformed.
                    }

                    FlushName(segments, buffer);
                    inIndex = true;
                    break;

                case ']':
                    if (!inIndex || !TryFlushIndex(segments, buffer))
                    {
                        return null; // ']' without a valid integer index is malformed.
                    }

                    inIndex = false;
                    break;

                default:
                    _ = buffer.Append(c);
                    break;
            }
        }

        if (inIndex)
        {
            return null; // Unterminated '['.
        }

        FlushName(segments, buffer);
        return segments;
    }

    /// <summary>
    ///     Navigates <paramref name="node"/> through <paramref name="segments"/> starting at
    ///     <paramref name="startIndex"/>. Navigating into a non-object/non-array, a missing key, or an
    ///     out-of-range index yields <c>null</c>.
    /// </summary>
    public static JsonNode? Navigate(
        JsonNode? node,
        IReadOnlyList<PathSegment> segments,
        int startIndex = 0
    )
    {
        ArgumentNullException.ThrowIfNull(segments);

        for (var i = startIndex; i < segments.Count; i++)
        {
            if (node is null)
            {
                return null;
            }

            var segment = segments[i];
            if (segment.IsIndex)
            {
                var index = segment.Index!.Value;
                node = node is JsonArray array && index >= 0 && index < array.Count
                    ? array[index]
                    : null;
            }
            else
            {
                node =
                    node is JsonObject obj
                    && obj.TryGetPropertyValue(segment.Name!, out var child)
                        ? child
                        : null;
            }
        }

        return node;
    }

    private static void FlushName(List<PathSegment> segments, System.Text.StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        segments.Add(PathSegment.Property(buffer.ToString()));
        _ = buffer.Clear();
    }

    private static bool TryFlushIndex(List<PathSegment> segments, System.Text.StringBuilder buffer)
    {
        if (!int.TryParse(buffer.ToString(), out var index) || index < 0)
        {
            return false;
        }

        segments.Add(PathSegment.ArrayIndex(index));
        _ = buffer.Clear();
        return true;
    }
}

/// <summary>
///     Shared rendering of a <see cref="JsonNode"/> to its plain-text form, used by template rendering and
///     by ordinal condition comparisons. A JSON string yields its raw value (no quotes); a number or
///     boolean yields its compact JSON token; <c>null</c> yields the empty string; an object or array
///     yields compact (non-indented) JSON.
/// </summary>
internal static class JsonText
{
    /// <summary>Returns the plain-text form of <paramref name="node"/> (see type summary).</summary>
    public static string ToText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => node.GetValue<string>(),
            _ => node.ToJsonString(),
        };
    }
}
