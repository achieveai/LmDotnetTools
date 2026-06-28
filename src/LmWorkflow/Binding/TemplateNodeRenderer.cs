using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>
///     Deep-renders a JSON template (for example a terminal node's <c>resultTemplate</c>) against a
///     <see cref="BindingContext"/>, composing a concrete result from workflow state. Objects and arrays are
///     rebuilt with rendered children; string leaves are rendered per the rules below.
/// </summary>
/// <remarks>
///     <para>String-leaf rules:</para>
///     <list type="bullet">
///         <item>
///             A leaf that is <b>exactly</b> a single binding (<c>{{ path }}</c>, optionally surrounded by
///             whitespace) is replaced by the <i>actual</i> resolved <see cref="JsonNode"/> — preserving its
///             type (object/array/number/boolean), not a stringified form. An absent path renders to JSON
///             <c>null</c>.
///         </item>
///         <item>
///             A leaf with embedded or partial bindings (<c>"v{{state.n}}"</c>) is rendered to a string via
///             <see cref="TemplateRenderer.Render"/> (an absent binding becomes an empty substring there).
///         </item>
///         <item>A leaf with no binding (or a non-string leaf) passes through unchanged.</item>
///     </list>
/// </remarks>
public static partial class TemplateNodeRenderer
{
    /// <summary>
    ///     Deep-renders <paramref name="template"/> against <paramref name="context"/>. Returns a freshly
    ///     built node (never aliasing live state); a <c>null</c> template yields <c>null</c>.
    /// </summary>
    public static JsonNode? Render(JsonNode? template, BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return template switch
        {
            null => null,
            JsonObject obj => RenderObject(obj, context),
            JsonArray array => RenderArray(array, context),
            JsonValue value => RenderValue(value, context),
            _ => template.DeepClone(),
        };
    }

    private static JsonObject RenderObject(JsonObject template, BindingContext context)
    {
        var result = new JsonObject();
        foreach (var (key, child) in template)
        {
            result[key] = Render(child, context);
        }

        return result;
    }

    private static JsonArray RenderArray(JsonArray template, BindingContext context)
    {
        var result = new JsonArray();
        foreach (var child in template)
        {
            result.Add(Render(child, context));
        }

        return result;
    }

    private static JsonNode? RenderValue(JsonValue template, BindingContext context)
    {
        if (template.GetValueKind() != JsonValueKind.String)
        {
            return template.DeepClone();
        }

        var text = template.GetValue<string>();

        // A whole-binding leaf substitutes the resolved node verbatim (type-preserving); absent -> null.
        if (WholeBindingPattern().Match(text) is { Success: true } whole)
        {
            return context.Resolve(whole.Groups[1].Value)?.DeepClone();
        }

        // Embedded/partial bindings render to an interpolated string; a plain literal passes through.
        return text.Contains("{{", StringComparison.Ordinal)
            ? JsonValue.Create(TemplateRenderer.Render(text, context))
            : template.DeepClone();
    }

    [GeneratedRegex(@"^\s*\{\{\s*([^{}]+?)\s*\}\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeBindingPattern();
}
