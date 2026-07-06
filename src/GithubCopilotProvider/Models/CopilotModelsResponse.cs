using System.Text.Json;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

/// <summary>
///     Low-level plumbing for the GitHub Copilot <c>GET /models</c> response shape — the response
///     unwrap, the endpoint-name spellings, and the <c>supported_endpoints</c> / string-property
///     reads that <see cref="CopilotModelCatalogParser"/> (this assembly) and the CopilotAnthropicProxy
///     sample's model resolver both need. Extracted so those two callers can't drift on the mechanics
///     while keeping their own projection/filtering local.
/// </summary>
/// <remarks>
///     <para>
///         <b>Visibility:</b> <c>public</c> because the CopilotAnthropicProxy sample lives in a separate
///         assembly and consumes these primitives too; it is otherwise internal-grade plumbing, not a
///         curated API.
///     </para>
///     <para>
///         <b>JsonElement lifetime:</b> the returned <see cref="JsonElement"/> entries are views over the
///         caller's <see cref="JsonDocument"/>. Enumerate and read them while that document is still
///         alive (inside the caller's <c>using</c> scope); they are invalid once it is disposed.
///     </para>
/// </remarks>
public static class CopilotModelsResponse
{
    /// <summary><c>POST /v1/messages</c> — the Anthropic Messages transport.</summary>
    public const string MessagesEndpoint = "/v1/messages";

    /// <summary><c>POST /responses</c> — the OpenAI Responses transport.</summary>
    public const string ResponsesEndpoint = "/responses";

    /// <summary><c>ws:/responses</c> — the WebSocket variant of the Responses transport.</summary>
    public const string ResponsesWebSocketEndpoint = "ws:/responses";

    /// <summary>
    ///     Unwraps the response to its model list and yields the object entries in upstream order.
    ///     Accepts both the <c>{ "data": [ ... ] }</c> envelope and a bare top-level array; any other
    ///     shape (missing/ non-array <c>data</c>, non-array root) yields nothing. Non-object array
    ///     entries are skipped so callers never see them.
    /// </summary>
    public static IEnumerable<JsonElement> EnumerateModelEntries(JsonElement root)
    {
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root;

        if (list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    ///     Whether the entry <em>declares</em> a <c>supported_endpoints</c> property at all, regardless of
    ///     its value kind. Callers use this to distinguish a response that carries endpoint metadata
    ///     (filter by transport) from an older/alternative shape that carries none (fall back to id-only).
    /// </summary>
    public static bool HasSupportedEndpoints(JsonElement item)
    {
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("supported_endpoints", out _);
    }

    /// <summary>
    ///     Returns the entry's <c>supported_endpoints</c> array element, or a default
    ///     (<see cref="JsonValueKind.Undefined"/>) element when the property is absent or not an array.
    /// </summary>
    public static JsonElement GetSupportedEndpoints(JsonElement item)
    {
        return item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("supported_endpoints", out var endpoints)
            && endpoints.ValueKind == JsonValueKind.Array
            ? endpoints
            : default;
    }

    /// <summary>
    ///     Whether the entry's <c>supported_endpoints</c> contains <paramref name="endpoint"/> (exact,
    ///     case-insensitive match). Non-string array values are skipped; absent/ non-array metadata is
    ///     treated as "not supported".
    /// </summary>
    public static bool SupportsEndpoint(JsonElement item, string endpoint)
    {
        var endpoints = GetSupportedEndpoints(item);
        if (endpoints.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var value in endpoints.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), endpoint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Reads a string property from a model entry, or <c>null</c> when the property is absent, not a
    ///     string, or the element is not an object.
    /// </summary>
    public static string? GetString(JsonElement item, string propertyName)
    {
        return item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(propertyName, out var el)
            && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }
}
