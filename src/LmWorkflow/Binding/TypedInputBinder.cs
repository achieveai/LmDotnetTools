using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>Copies typed JSON values from declared roots; absent data is an error, distinct from JSON null.</summary>
public static class TypedInputBinder
{
    private static readonly Regex PathPattern = new(
        @"^(inputs|state)(?:\.[A-Za-z_][A-Za-z0-9_-]*|\[(?:0|[1-9][0-9]*)\])*$",
        RegexOptions.CultureInvariant
    );

    public static JsonNode? Resolve(JsonNode? binding, BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (binding is JsonObject obj)
        {
            if (obj.ContainsKey("from"))
            {
                if (obj.Count != 1 || obj["from"] is not JsonValue from || !from.TryGetValue<string>(out var path))
                    throw new InvalidOperationException("A reference binding must contain only a string 'from'.");
                return ResolvePath(path, context)?.DeepClone();
            }
            if (obj.ContainsKey("literal"))
            {
                if (obj.Count != 1)
                    throw new InvalidOperationException("A literal binding must contain only 'literal'.");
                return obj["literal"]?.DeepClone();
            }
            var result = new JsonObject();
            foreach (var (name, child) in obj)
                result[name] = Resolve(child, context);
            return result;
        }
        if (binding is JsonArray array)
            return new JsonArray([.. array.Select(child => Resolve(child, context))]);
        return binding?.DeepClone();
    }

    internal static IReadOnlyList<PathSegment> ParsePath(string path)
    {
        if (!PathPattern.IsMatch(path))
            throw new InvalidOperationException($"Unsupported typed binding path '{path}'.");
        return JsonPath.Parse(path)!;
    }

    private static JsonNode? ResolvePath(string path, BindingContext context)
    {
        var segments = ParsePath(path);
        JsonNode? node = segments[0].Name == "inputs" ? context.Inputs : context.State;
        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.IsIndex && node is JsonArray array && segment.Index!.Value < array.Count)
                node = array[segment.Index.Value];
            else if (
                !segment.IsIndex
                && node is JsonObject obj
                && obj.TryGetPropertyValue(segment.Name!, out var child)
            )
                node = child;
            else
                throw new InvalidOperationException($"Required binding '{path}' is missing.");
        }
        return node;
    }
}
