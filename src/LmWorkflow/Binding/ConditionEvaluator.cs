using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>
///     Evaluates a structured <see cref="Condition"/> AST against a <see cref="BindingContext"/>.
///     Composites (<see cref="Condition.All"/>/<see cref="Condition.Any"/>/<see cref="Condition.Not"/>) are
///     combined logically; a leaf resolves its <see cref="Condition.Path"/> and compares it to its
///     <see cref="Condition.Value"/> per <see cref="Condition.Op"/>.
/// </summary>
public static partial class ConditionEvaluator
{
    /// <summary>
    ///     Evaluates <paramref name="condition"/>. An empty <see cref="Condition.All"/> is vacuously
    ///     <c>true</c>; an empty <see cref="Condition.Any"/> is <c>false</c>. A leaf whose operator is
    ///     unset evaluates to <c>false</c>.
    /// </summary>
    public static bool Evaluate(Condition condition, BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);

        if (condition.All is { } all)
        {
            return all.All(child => Evaluate(child, context));
        }

        if (condition.Any is { } any)
        {
            return any.Any(child => Evaluate(child, context));
        }

        if (condition.Not is { } not)
        {
            return !Evaluate(not, context);
        }

        return EvaluateLeaf(condition, context);
    }

    private static bool EvaluateLeaf(Condition condition, BindingContext context)
    {
        if (condition.Op is not { } op)
        {
            return false;
        }

        var left = condition.Path is null ? null : context.Resolve(condition.Path);
        var right = ResolveValue(condition.Value, context);

        return op switch
        {
            ConditionOp.Eq => StructuralEquals(left, right),
            ConditionOp.Ne => !StructuralEquals(left, right),
            ConditionOp.Lt => Compare(left, right, op),
            ConditionOp.Lte => Compare(left, right, op),
            ConditionOp.Gt => Compare(left, right, op),
            ConditionOp.Gte => Compare(left, right, op),
            ConditionOp.In => EvaluateIn(left, right),
            ConditionOp.Empty => IsEmpty(left),
            ConditionOp.NonEmpty => !IsEmpty(left),
            _ => false,
        };
    }

    /// <summary>
    ///     Resolves a leaf's comparison value. When the value is a JSON string that is wholly a single
    ///     binding (<c>{{ path }}</c>), it is resolved through the context; otherwise it is used literally.
    /// </summary>
    private static JsonNode? ResolveValue(JsonNode? value, BindingContext context)
    {
        if (
            value is not null
            && value.GetValueKind() == JsonValueKind.String
            && WholeBindingPattern().Match(value.GetValue<string>()) is { Success: true } match
        )
        {
            return context.Resolve(match.Groups[1].Value);
        }

        return value;
    }

    /// <summary>
    ///     Structural equality: numbers compared numerically, strings/booleans by value, objects and arrays
    ///     deep-compared. Two absent operands are equal; a kind mismatch (other than two numbers) is not.
    /// </summary>
    private static bool StructuralEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var kindA = a.GetValueKind();
        var kindB = b.GetValueKind();

        if (kindA == JsonValueKind.Number && kindB == JsonValueKind.Number)
        {
            return TryGetNumber(a, out var na) && TryGetNumber(b, out var nb) && na.Equals(nb);
        }

        if (kindA != kindB)
        {
            return false;
        }

        return kindA switch
        {
            JsonValueKind.String => a.GetValue<string>() == b.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind.Object => ObjectEquals((JsonObject)a, (JsonObject)b),
            JsonValueKind.Array => ArrayEquals((JsonArray)a, (JsonArray)b),
            _ => false,
        };
    }

    private static bool ObjectEquals(JsonObject a, JsonObject b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, valueA) in a)
        {
            if (!b.TryGetPropertyValue(key, out var valueB) || !StructuralEquals(valueA, valueB))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ArrayEquals(JsonArray a, JsonArray b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!StructuralEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Ordered comparison. When both operands parse as numbers (including numeric strings) the
    ///     comparison is numeric; otherwise it is an ordinal comparison of their text forms. A missing
    ///     operand yields <c>false</c>.
    /// </summary>
    private static bool Compare(JsonNode? left, JsonNode? right, ConditionOp op)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var comparison =
            TryGetNumber(left, out var numericLeft) && TryGetNumber(right, out var numericRight)
                ? numericLeft.CompareTo(numericRight)
                : string.CompareOrdinal(JsonText.ToText(left), JsonText.ToText(right));

        return op switch
        {
            ConditionOp.Lt => comparison < 0,
            ConditionOp.Lte => comparison <= 0,
            ConditionOp.Gt => comparison > 0,
            ConditionOp.Gte => comparison >= 0,
            _ => false,
        };
    }

    /// <summary>
    ///     Membership test. When <paramref name="right"/> is an array, returns whether <paramref name="left"/>
    ///     is one of its elements; otherwise, when <paramref name="left"/> is an array, returns whether
    ///     <paramref name="right"/> is one of its elements. If neither operand is an array, returns
    ///     <c>false</c>.
    /// </summary>
    private static bool EvaluateIn(JsonNode? left, JsonNode? right)
    {
        if (right is JsonArray rightArray)
        {
            return rightArray.Any(element => StructuralEquals(left, element));
        }

        if (left is JsonArray leftArray)
        {
            return leftArray.Any(element => StructuralEquals(element, right));
        }

        return false;
    }

    /// <summary>Returns whether the operand is absent, an empty string, an empty array, or an empty object.</summary>
    private static bool IsEmpty(JsonNode? node)
    {
        if (node is null)
        {
            return true;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => node.GetValue<string>().Length == 0,
            JsonValueKind.Array => ((JsonArray)node).Count == 0,
            JsonValueKind.Object => ((JsonObject)node).Count == 0,
            _ => false,
        };
    }

    /// <summary>
    ///     Extracts a numeric value from <paramref name="node"/>. Works for both parsed (JsonElement-backed)
    ///     and CLR-backed <see cref="JsonValue"/> numbers by parsing the JSON token, avoiding the
    ///     <c>GetValue&lt;double&gt;()</c> sharp edge on integer-backed values. Numeric strings are accepted.
    /// </summary>
    private static bool TryGetNumber(JsonNode node, out double value)
    {
        value = 0;
        return node.GetValueKind() switch
        {
            // Serialize-then-parse rather than JsonValue.TryGetValue<double>(): on net8/net9 that method
            // returns false for an int-backed JsonValue (e.g. JsonValue.Create(5)), which would silently
            // flip a numeric comparison to an ordinal-string compare. ToJsonString() always emits the
            // numeric token, so parsing it covers both JsonElement- and CLR-backed numbers.
            JsonValueKind.Number => double.TryParse(
                node.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value
            ),
            JsonValueKind.String => double.TryParse(
                node.GetValue<string>(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value
            ),
            _ => false,
        };
    }

    [GeneratedRegex(@"^\s*\{\{\s*(.*?)\s*\}\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeBindingPattern();
}
