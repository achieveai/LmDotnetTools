using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>
///     Renders prompt/result templates by substituting every <c>{{ expr }}</c> occurrence with the value
///     of the direct binding path <c>expr</c> resolved against a <see cref="BindingContext"/>. Whitespace
///     inside the braces is tolerated and any number of substitutions may appear in one template; text
///     outside the braces passes through verbatim.
/// </summary>
public static partial class TemplateRenderer
{
    /// <summary>
    ///     The maximum number of characters emitted for a single object/array substitution. Larger values
    ///     are truncated with a trailing marker so a runaway binding cannot blow up a prompt.
    /// </summary>
    public const int MaxBindingBytes = 8192;

    /// <summary>
    ///     Replaces each <c>{{ path }}</c> binding in <paramref name="template"/> with its resolved value:
    ///     a scalar becomes its plain string form, an object/array becomes compact (size-guarded) JSON, and
    ///     an absent/null binding becomes the empty string.
    /// </summary>
    public static string Render(string template, BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        return BindingPattern()
            .Replace(template, match => RenderBinding(match.Groups[1].Value, context));
    }

    private static string RenderBinding(string expression, BindingContext context)
    {
        var node = context.Resolve(expression);
        if (node is null)
        {
            return string.Empty;
        }

        var text = JsonText.ToText(node);
        return node.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array
            ? SizeGuard(text)
            : text;
    }

    private static string SizeGuard(string text)
    {
        if (text.Length <= MaxBindingBytes)
        {
            return text;
        }

        var truncated = text.Length - MaxBindingBytes;
        return string.Concat(text.AsSpan(0, MaxBindingBytes), $"…[truncated {truncated} bytes]");
    }

    [GeneratedRegex(@"\{\{\s*(.*?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex BindingPattern();
}
