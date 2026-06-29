using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     Tolerant converter for <see cref="Condition"/>. Reads the condition AST from a JSON object and,
///     critically, does NOT throw when the <c>op</c> string is not a known <see cref="ConditionOp"/> —
///     the raw value is preserved in <see cref="Condition.UnknownOp"/> so the validator can report it
///     while still collecting every other error.
/// </summary>
public sealed class ConditionJsonConverter : JsonConverter<Condition>
{
    /// <inheritdoc />
    public override Condition? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        return FromNode(JsonNode.Parse(ref reader));
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        Condition value,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        if (value.Op is { } op)
        {
            writer.WriteString("op", ConditionOpNames.ToWire(op));
        }
        else if (value.UnknownOp is { } unknown)
        {
            writer.WriteString("op", unknown);
        }

        if (value.Path is { } path)
        {
            writer.WriteString("path", path);
        }

        if (value.Value is { } conditionValue)
        {
            writer.WritePropertyName("value");
            conditionValue.WriteTo(writer);
        }

        WriteChildren(writer, "all", value.All, options);
        WriteChildren(writer, "any", value.Any, options);

        if (value.Not is { } not)
        {
            writer.WritePropertyName("not");
            Write(writer, not, options);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    ///     Builds a <see cref="Condition"/> from an arbitrary JSON node. Used both by the converter and
    ///     by <see cref="Branch.StructuredCondition"/> when a branch expresses its condition via the
    ///     <c>when</c> object.
    /// </summary>
    internal static Condition? FromNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject obj)
        {
            throw new JsonException("A structured condition must be a JSON object.");
        }

        ConditionOp? op = null;
        string? unknownOp = null;
        if (TryGetString(obj, "op", out var opString))
        {
            if (ConditionOpNames.TryParse(opString, out var parsed))
            {
                op = parsed;
            }
            else
            {
                unknownOp = opString;
            }
        }

        return new Condition
        {
            Op = op,
            UnknownOp = unknownOp,
            Path = TryGetString(obj, "path", out var path) ? path : null,
            Value = obj.TryGetPropertyValue("value", out var value) ? value?.DeepClone() : null,
            All = ReadChildren(obj, "all"),
            Any = ReadChildren(obj, "any"),
            Not = obj.TryGetPropertyValue("not", out var not) ? FromNode(not) : null,
        };
    }

    private static IReadOnlyList<Condition>? ReadChildren(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return null;
        }

        var children = new List<Condition>(array.Count);
        foreach (var item in array)
        {
            if (FromNode(item) is { } child)
            {
                children.Add(child);
            }
        }

        return children;
    }

    private static void WriteChildren(
        Utf8JsonWriter writer,
        string key,
        IReadOnlyList<Condition>? children,
        JsonSerializerOptions options
    )
    {
        if (children is null)
        {
            return;
        }

        writer.WritePropertyName(key);
        writer.WriteStartArray();
        foreach (var child in children)
        {
            JsonSerializer.Serialize(writer, child, options);
        }

        writer.WriteEndArray();
    }

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        if (
            obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var parsed)
        )
        {
            value = parsed;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>
///     Maps <see cref="ConditionOp"/> values to and from their wire (camelCase) form. Kept explicit so
///     parsing rejects unknown operator strings rather than coercing numeric or arbitrary values.
/// </summary>
internal static class ConditionOpNames
{
    private static readonly IReadOnlyDictionary<string, ConditionOp> ByWire = new Dictionary<
        string,
        ConditionOp
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = ConditionOp.Eq,
        ["ne"] = ConditionOp.Ne,
        ["lt"] = ConditionOp.Lt,
        ["lte"] = ConditionOp.Lte,
        ["gt"] = ConditionOp.Gt,
        ["gte"] = ConditionOp.Gte,
        ["in"] = ConditionOp.In,
        ["empty"] = ConditionOp.Empty,
        ["nonEmpty"] = ConditionOp.NonEmpty,
    };

    /// <summary>Attempts to parse a wire operator string into a known <see cref="ConditionOp"/>.</summary>
    public static bool TryParse(string? wire, out ConditionOp op)
    {
        if (wire is not null && ByWire.TryGetValue(wire, out op))
        {
            return true;
        }

        op = default;
        return false;
    }

    /// <summary>Returns the camelCase wire form of a <see cref="ConditionOp"/>.</summary>
    public static string ToWire(ConditionOp op)
    {
        var name = op.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
