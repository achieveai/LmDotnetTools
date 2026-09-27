using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>Parses the bounded authored-workflow condition grammar <c>path operator literal</c>.</summary>
public static partial class NaturalConditionParser
{
    /// <summary>
    ///     Parses one natural comparison leaf. Supported literals are Booleans, finite JSON numbers,
    ///     single-quoted strings with doubled apostrophes, and double-quoted JSON strings.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <c>null</c>.</exception>
    /// <exception cref="FormatException"><paramref name="expression"/> is outside the bounded grammar.</exception>
    public static Condition Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var text = expression.AsSpan().Trim();
        if (text.IsEmpty)
        {
            throw Invalid(expression, "the expression is empty");
        }

        var operatorStart = FindOperatorStart(text);
        if (operatorStart <= 0)
        {
            throw Invalid(expression, "a supported comparison operator is required");
        }

        var path = text[..operatorStart].Trim().ToString();
        if (!PathPattern().IsMatch(path) || JsonPath.Parse(path) is null)
        {
            throw Invalid(expression, $"'{path}' is not a valid binding path");
        }

        var remaining = text[operatorStart..];
        var (op, operatorLength) = ParseOperator(remaining, expression);
        remaining = remaining[operatorLength..].TrimStart();
        if (remaining.IsEmpty)
        {
            throw Invalid(expression, "a literal is required after the operator");
        }

        var value = ParseLiteral(remaining, expression);
        return new Condition
        {
            Op = op,
            Path = path,
            Value = value,
        };
    }

    private static int FindOperatorStart(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '=' or '!' or '<' or '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static (ConditionOp Op, int Length) ParseOperator(ReadOnlySpan<char> text, string expression)
    {
        if (text.StartsWith("==", StringComparison.Ordinal))
        {
            return (ConditionOp.Eq, 2);
        }

        if (text.StartsWith("!=", StringComparison.Ordinal))
        {
            return (ConditionOp.Ne, 2);
        }

        if (text.StartsWith("<=", StringComparison.Ordinal))
        {
            return (ConditionOp.Lte, 2);
        }

        if (text.StartsWith(">=", StringComparison.Ordinal))
        {
            return (ConditionOp.Gte, 2);
        }

        if (text[0] == '<')
        {
            return (ConditionOp.Lt, 1);
        }

        if (text[0] == '>')
        {
            return (ConditionOp.Gt, 1);
        }

        throw Invalid(expression, "the operator must be ==, !=, <, <=, >, or >=");
    }

    private static JsonNode ParseLiteral(ReadOnlySpan<char> text, string expression)
    {
        if (text[0] == '\'')
        {
            return JsonValue.Create(ParseSingleQuotedString(text, expression))!;
        }

        var literal = text.ToString().TrimEnd();
        if (literal == "true")
        {
            return JsonValue.Create(true)!;
        }

        if (literal == "false")
        {
            return JsonValue.Create(false)!;
        }

        if (literal.StartsWith('"'))
        {
            return ParseDoubleQuotedString(literal, expression);
        }

        if (!NumberPattern().IsMatch(literal))
        {
            throw Invalid(expression, "the right operand must be a Boolean, finite JSON number, or quoted string");
        }

        if (
            !double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number)
        )
        {
            throw Invalid(expression, "numeric literals must be finite");
        }

        return JsonNode.Parse(literal)!;
    }

    private static string ParseSingleQuotedString(ReadOnlySpan<char> text, string expression)
    {
        var value = new StringBuilder();
        var index = 1;
        while (index < text.Length)
        {
            if (text[index] != '\'')
            {
                _ = value.Append(text[index]);
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == '\'')
            {
                _ = value.Append('\'');
                index += 2;
                continue;
            }

            if (!text[(index + 1)..].Trim().IsEmpty)
            {
                throw Invalid(expression, "unexpected text follows the string literal");
            }

            return value.ToString();
        }

        throw Invalid(expression, "the single-quoted string is not terminated");
    }

    private static JsonNode ParseDoubleQuotedString(string literal, string expression)
    {
        try
        {
            var value = JsonNode.Parse(literal);
            if (value?.GetValueKind() != JsonValueKind.String)
            {
                throw Invalid(expression, "the right operand must be a string literal");
            }

            return value;
        }
        catch (JsonException exception)
        {
            throw Invalid(expression, "the double-quoted string has invalid JSON escaping", exception);
        }
    }

    private static FormatException Invalid(string expression, string reason, Exception? innerException = null) =>
        new($"Invalid natural condition '{expression}': {reason}.", innerException);

    [GeneratedRegex(@"^[^.\[\]\s]+(?:\.[^.\[\]\s]+|\[[0-9]+\])*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
