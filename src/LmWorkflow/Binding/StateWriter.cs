using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>
///     Applies a task's <see cref="WriteSpec"/> to the workflow <c>state</c> channel. The source value is
///     either the whole validated task output or a direct subpath of it (<see cref="WriteSpec.From"/>); the
///     destination is the <see cref="WriteSpec.To"/> path under <c>state.</c>; and the merge strategy is
///     <see cref="WriteSpec.Mode"/>. All nodes are deep-cloned before insertion so a node is never parented
///     twice.
/// </summary>
public static class StateWriter
{
    private const string StatePrefix = "state.";

    /// <summary>
    ///     Writes <paramref name="validatedOutput"/> (or the <see cref="WriteSpec.From"/> subpath of it) into
    ///     <paramref name="state"/> at <see cref="WriteSpec.To"/> using <see cref="WriteSpec.Mode"/>.
    /// </summary>
    /// <param name="state">The mutable state object to write into.</param>
    /// <param name="spec">The write specification.</param>
    /// <param name="validatedOutput">The task output (already schema-validated), or <c>null</c>.</param>
    /// <exception cref="ArgumentException"><see cref="WriteSpec.To"/> does not start with <c>state.</c>.</exception>
    /// <exception cref="NotSupportedException">
    ///     The mode is <see cref="WriteMode.Upsert"/> (out of V1 scope), or the destination path contains an
    ///     array-index segment (only property destinations are supported in V1).
    /// </exception>
    public static void Apply(JsonObject state, WriteSpec spec, JsonNode? validatedOutput)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(spec);

        var source = ResolveSource(spec.From, validatedOutput);
        var destination = ParseDestination(spec.To);
        var parent = GetOrCreateParent(state, destination);
        var key = RequireProperty(destination[^1]);

        switch (spec.Mode)
        {
            case WriteMode.Set:
                parent[key] = Clone(source);
                break;

            case WriteMode.Append:
                ApplyAppend(parent, key, source);
                break;

            case WriteMode.Merge:
                ApplyMerge(parent, key, source);
                break;

            case WriteMode.Upsert:
                throw new NotSupportedException("WriteMode.Upsert is not supported in V1.");

            default:
                throw new NotSupportedException($"Unsupported write mode '{spec.Mode}'.");
        }
    }

    private static JsonNode? ResolveSource(string? from, JsonNode? validatedOutput)
    {
        if (string.IsNullOrEmpty(from))
        {
            return validatedOutput;
        }

        var segments = JsonPath.Parse(from);
        return segments is null ? null : JsonPath.Navigate(validatedOutput, segments);
    }

    private static IReadOnlyList<PathSegment> ParseDestination(string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        if (!to.StartsWith(StatePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"WriteSpec.To must start with '{StatePrefix}' but was '{to}'.",
                nameof(to)
            );
        }

        var segments = JsonPath.Parse(to[StatePrefix.Length..]);
        if (segments is null || segments.Count == 0)
        {
            throw new ArgumentException(
                $"WriteSpec.To '{to}' does not address a state destination.",
                nameof(to)
            );
        }

        return segments;
    }

    /// <summary>
    ///     Walks to the parent container of the final destination segment, creating intermediate
    ///     <see cref="JsonObject"/>s for any missing (or non-object) property segments.
    /// </summary>
    private static JsonObject GetOrCreateParent(
        JsonObject root,
        IReadOnlyList<PathSegment> destination
    )
    {
        var current = root;
        for (var i = 0; i < destination.Count - 1; i++)
        {
            var name = RequireProperty(destination[i]);
            if (current[name] is JsonObject child)
            {
                current = child;
            }
            else
            {
                var created = new JsonObject();
                current[name] = created;
                current = created;
            }
        }

        return current;
    }

    /// <summary>
    ///     Appends <paramref name="source"/> to the array at <paramref name="key"/>: an absent destination
    ///     becomes a singleton array; an existing array is concatenated (when the source is an array) or has
    ///     the source pushed as one element; a non-array destination is first wrapped in an array.
    /// </summary>
    private static void ApplyAppend(JsonObject parent, string key, JsonNode? source)
    {
        if (parent[key] is JsonArray existingArray)
        {
            AppendInto(existingArray, source);
            return;
        }

        var array = new JsonArray();
        if (parent[key] is { } existing)
        {
            array.Add(Clone(existing));
        }

        AppendInto(array, source);
        parent[key] = array;
    }

    private static void AppendInto(JsonArray array, JsonNode? source)
    {
        if (source is JsonArray sourceArray)
        {
            foreach (var element in sourceArray)
            {
                array.Add(Clone(element));
            }
        }
        else
        {
            array.Add(Clone(source));
        }
    }

    /// <summary>
    ///     Shallow-merges <paramref name="source"/> (which must be an object) into the object at
    ///     <paramref name="key"/>, overwriting overlapping top-level keys and keeping the rest. A non-object
    ///     destination is replaced by a fresh object.
    /// </summary>
    private static void ApplyMerge(JsonObject parent, string key, JsonNode? source)
    {
        if (source is not JsonObject sourceObject)
        {
            throw new InvalidOperationException(
                "WriteMode.Merge requires the source value to be a JSON object."
            );
        }

        if (parent[key] is JsonObject target)
        {
            CopyInto(target, sourceObject);
            return;
        }

        var created = new JsonObject();
        CopyInto(created, sourceObject);
        parent[key] = created;
    }

    private static void CopyInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = Clone(value);
        }
    }

    private static string RequireProperty(PathSegment segment)
    {
        if (segment.IsIndex)
        {
            throw new NotSupportedException(
                "Array-index destination segments are not supported in V1 state writes."
            );
        }

        return segment.Name!;
    }

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();
}
