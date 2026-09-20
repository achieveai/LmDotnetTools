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
    public static bool Evaluate(Condition condition, BindingContext context) =>
        Evaluate(condition, context, ConditionEvaluationPolicy.Legacy);

    /// <summary>
    ///     Evaluates <paramref name="condition"/> using an explicit comparison <paramref name="policy"/>.
    ///     Strict workflow evaluation validates every leaf before applying Boolean composition so a
    ///     short-circuited branch cannot hide a missing or mistyped operand.
    /// </summary>
    public static bool Evaluate(Condition condition, BindingContext context, ConditionEvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);

        if (policy is not ConditionEvaluationPolicy.Legacy and not ConditionEvaluationPolicy.StrictWorkflow)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown condition evaluation policy.");
        }

        if (policy == ConditionEvaluationPolicy.StrictWorkflow)
        {
            ValidateStrict(condition, context);
        }

        return EvaluateCore(condition, context, policy);
    }

    private static bool EvaluateCore(Condition condition, BindingContext context, ConditionEvaluationPolicy policy)
    {
        if (condition.All is { } all)
        {
            return all.All(child => EvaluateCore(child, context, policy));
        }

        if (condition.Any is { } any)
        {
            return any.Any(child => EvaluateCore(child, context, policy));
        }

        if (condition.Not is { } not)
        {
            return !EvaluateCore(not, context, policy);
        }

        return EvaluateLeaf(condition, context, policy);
    }

    private static bool EvaluateLeaf(Condition condition, BindingContext context, ConditionEvaluationPolicy policy)
    {
        if (condition.Op is not { } op)
        {
            return false;
        }

        var left = condition.Path is null ? null : context.Resolve(condition.Path);
        var right = ResolveValue(condition.Value, context);

        return op switch
        {
            ConditionOp.Eq => Equals(left, right, policy),
            ConditionOp.Ne => !Equals(left, right, policy),
            ConditionOp.Lt => Compare(left, right, op, policy),
            ConditionOp.Lte => Compare(left, right, op, policy),
            ConditionOp.Gt => Compare(left, right, op, policy),
            ConditionOp.Gte => Compare(left, right, op, policy),
            ConditionOp.In => EvaluateIn(left, right),
            ConditionOp.Empty => IsEmpty(left),
            ConditionOp.NonEmpty => !IsEmpty(left),
            _ => false,
        };
    }

    private static void ValidateStrict(Condition condition, BindingContext context)
    {
        if (condition.All is { } all)
        {
            foreach (var child in all)
            {
                ValidateStrict(child, context);
            }

            return;
        }

        if (condition.Any is { } any)
        {
            foreach (var child in any)
            {
                ValidateStrict(child, context);
            }

            return;
        }

        if (condition.Not is { } not)
        {
            ValidateStrict(not, context);
            return;
        }

        ValidateStrictLeaf(condition, context);
    }

    private static void ValidateStrictLeaf(Condition condition, BindingContext context)
    {
        if (condition.Op is not { } op)
        {
            throw new InvalidOperationException("A strict workflow condition leaf requires an operator.");
        }

        if (string.IsNullOrWhiteSpace(condition.Path))
        {
            throw new InvalidOperationException($"Condition operator '{op}' requires a binding path.");
        }

        var left = context.Resolve(condition.Path);
        if (op is ConditionOp.Empty or ConditionOp.NonEmpty)
        {
            return;
        }

        if (left is null)
        {
            throw MissingOperand(condition.Path, op);
        }

        var valuePath = GetValueBindingPath(condition.Value);
        var right = ResolveValue(condition.Value, context) ?? throw MissingOperand(valuePath ?? "comparison value", op);
        switch (op)
        {
            case ConditionOp.Eq:
            case ConditionOp.Ne:
                ValidateEqualityTypes(condition.Path, left, right);
                break;

            case ConditionOp.Lt:
            case ConditionOp.Lte:
            case ConditionOp.Gt:
            case ConditionOp.Gte:
                ValidateNumericTypes(condition.Path, op, left, right);
                break;

            case ConditionOp.In:
                if (left is not JsonArray && right is not JsonArray)
                {
                    throw new InvalidOperationException(
                        $"Condition operator '{op}' at '{condition.Path}' requires one Array operand."
                    );
                }

                break;

            case ConditionOp.Empty:
            case ConditionOp.NonEmpty:
                break;

            default:
                throw new InvalidOperationException($"Unsupported strict workflow condition operator '{op}'.");
        }
    }

    private static void ValidateEqualityTypes(string path, JsonNode left, JsonNode right)
    {
        var leftType = GetStrictType(left);
        var rightType = GetStrictType(right);
        if (leftType != rightType)
        {
            throw new InvalidOperationException(
                $"Condition at '{path}' requires operands of the same type, but left is {leftType} and right is {rightType}."
            );
        }
    }

    private static void ValidateNumericTypes(string path, ConditionOp op, JsonNode left, JsonNode right)
    {
        if (!TryGetStrictNumber(left, out _) || !TryGetStrictNumber(right, out _))
        {
            throw new InvalidOperationException(
                $"Condition operator '{op}' at '{path}' requires Number operands, but left is {GetStrictType(left)} and right is {GetStrictType(right)}."
            );
        }
    }

    private static InvalidOperationException MissingOperand(string path, ConditionOp op) =>
        new($"Required condition operand '{path}' for operator '{op}' is missing.");

    private static string GetStrictType(JsonNode node) =>
        node.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => "Boolean",
            JsonValueKind.Number => "Number",
            JsonValueKind.String => "String",
            JsonValueKind.Array => "Array",
            JsonValueKind.Object => "Object",
            JsonValueKind.Null => "Null",
            _ => "Unsupported",
        };

    internal static string? GetValueBindingPath(JsonNode? value)
    {
        if (
            value is not null
            && value.GetValueKind() == JsonValueKind.String
            && WholeBindingPattern().Match(value.GetValue<string>()) is { Success: true } match
        )
        {
            return match.Groups[1].Value;
        }

        return null;
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
    private static bool Equals(JsonNode? left, JsonNode? right, ConditionEvaluationPolicy policy) =>
        policy == ConditionEvaluationPolicy.StrictWorkflow
            ? StrictEquals(left!, right!)
            : StructuralEquals(left, right);

    private static bool StrictEquals(JsonNode left, JsonNode right)
    {
        if (left.GetValueKind() == JsonValueKind.String)
        {
            return string.Equals(left.GetValue<string>(), right.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }

        return StructuralEquals(left, right);
    }

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
    private static bool Compare(JsonNode? left, JsonNode? right, ConditionOp op, ConditionEvaluationPolicy policy)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var bothNumeric = TryGetComparisonNumbers(left, right, policy, out var numericLeft, out var numericRight);
        var comparison = bothNumeric
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

    private static bool TryGetComparisonNumbers(
        JsonNode left,
        JsonNode right,
        ConditionEvaluationPolicy policy,
        out double numericLeft,
        out double numericRight
    )
    {
        numericLeft = 0;
        numericRight = 0;
        return policy == ConditionEvaluationPolicy.StrictWorkflow
            ? TryGetStrictNumber(left, out numericLeft) && TryGetStrictNumber(right, out numericRight)
            : TryGetNumber(left, out numericLeft) && TryGetNumber(right, out numericRight);
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

    private static bool TryGetStrictNumber(JsonNode node, out double value)
    {
        value = 0;
        return node.GetValueKind() == JsonValueKind.Number && TryGetNumber(node, out value) && double.IsFinite(value);
    }

    [GeneratedRegex(@"^\s*\{\{\s*(.*?)\s*\}\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeBindingPattern();
}
