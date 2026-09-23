using System.Text.Json;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

/// <summary>
///     Parses the GitHub Copilot <c>GET /models</c> response into the subset of models the sample can
///     list and route: those published by Anthropic, OpenAI, xAI, Google or Microsoft <em>and</em> reachable
///     via a supported transport (<c>/v1/messages</c>, <c>/responses</c> or <c>/chat/completions</c>). Any
///     other publisher is dropped.
/// </summary>
/// <remarks>
///     Pure and side-effect free so it can be unit-tested against the captured real response fixture.
///     The response shape is <c>{ "data": [ { "id", "name", "vendor", "supported_endpoints": [...] } ] }</c>;
///     a bare top-level array is also accepted. Response unwrap and endpoint reads are shared with the
///     CopilotAnthropicProxy resolver via <see cref="CopilotModelsResponse"/>; this parser layers the
///     vendor partition, transport ranking, and adaptive-thinking projection on top.
/// </remarks>
public static class CopilotModelCatalogParser
{
    /// <summary>
    ///     Projects the Copilot <c>/models</c> JSON to the routable models, preserving
    ///     upstream order. Malformed or partial entries are skipped rather than throwing.
    /// </summary>
    public static IReadOnlyList<CopilotModelInfo> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);

        var models = new List<CopilotModelInfo>();
        foreach (var item in CopilotModelsResponse.EnumerateModelEntries(doc.RootElement))
        {
            if (TryParseModel(item, out var model))
            {
                models.Add(model);
            }
        }

        return models;
    }

    private static bool TryParseModel(JsonElement item, out CopilotModelInfo model)
    {
        model = null!;

        var id = CopilotModelsResponse.GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (!TryNormalizeVendor(CopilotModelsResponse.GetString(item, "vendor"), out var vendor))
        {
            return false;
        }

        var transport = DeriveTransport(item);
        if (transport == CopilotModelTransport.Unsupported)
        {
            return false;
        }

        var displayName = CopilotModelsResponse.GetString(item, "name");
        displayName = string.IsNullOrWhiteSpace(displayName) ? id! : displayName!;

        model = new CopilotModelInfo(id!, displayName, vendor, transport, SupportsAdaptiveThinking(item))
        {
            ReasoningEfforts = GetReasoningEfforts(item),
        };
        return true;
    }

    private static IReadOnlyList<string> GetReasoningEfforts(JsonElement item)
    {
        if (
            !item.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("supports", out var supports)
            || supports.ValueKind != JsonValueKind.Object
            || !supports.TryGetProperty("reasoning_effort", out var reasoningEffort)
            || reasoningEffort.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        return
        [
            .. reasoningEffort
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => value is not null)
                .Select(value => value!),
        ];
    }

    /// <summary>
    ///     Reads <c>capabilities.supports.adaptive_thinking</c>. Models that advertise it reject the
    ///     classic <c>thinking.type.enabled</c> budget request, so the sample gates the classic thinking
    ///     parameter on this flag being <c>false</c>/absent.
    /// </summary>
    private static bool SupportsAdaptiveThinking(JsonElement item)
    {
        return item.TryGetProperty("capabilities", out var capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("supports", out var supports)
            && supports.ValueKind == JsonValueKind.Object
            && supports.TryGetProperty("adaptive_thinking", out var adaptive)
            && adaptive.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    ///     Maps the response <c>vendor</c> to a partition. Copilot reports newer GPTs as <c>OpenAI</c>
    ///     but some hosted variants as <c>Azure OpenAI</c>; both collapse to <see cref="CopilotModelVendor.OpenAI"/>.
    ///     <c>xAI</c>, <c>Google</c> and <c>Microsoft</c> map to their own partitions. Any other publisher is
    ///     not a partition we surface.
    /// </summary>
    private static bool TryNormalizeVendor(string? vendor, out CopilotModelVendor normalized)
    {
        normalized = default;
        if (string.IsNullOrWhiteSpace(vendor))
        {
            return false;
        }

        var trimmed = vendor.Trim();
        if (trimmed.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            normalized = CopilotModelVendor.Anthropic;
            return true;
        }

        if (
            trimmed.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase)
        )
        {
            normalized = CopilotModelVendor.OpenAI;
            return true;
        }

        if (trimmed.Equals("xAI", StringComparison.OrdinalIgnoreCase))
        {
            normalized = CopilotModelVendor.XAi;
            return true;
        }

        if (trimmed.Equals("Google", StringComparison.OrdinalIgnoreCase))
        {
            normalized = CopilotModelVendor.Google;
            return true;
        }

        if (trimmed.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            normalized = CopilotModelVendor.Microsoft;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Chooses the routable transport from <c>supported_endpoints</c>. <c>/v1/messages</c> wins
    ///     (Anthropic Messages) regardless of list order, else <c>/responses</c> (or its
    ///     <c>ws:/responses</c> variant) selects the Responses transport, else <c>/chat/completions</c>
    ///     selects Chat Completions. Chat Completions ranks last because Copilot speaks a non-standard
    ///     dialect there; a model that also offers a native endpoint keeps it. Absent metadata yields
    ///     <see cref="CopilotModelTransport.Unsupported"/>.
    /// </summary>
    private static CopilotModelTransport DeriveTransport(JsonElement item)
    {
        if (CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.MessagesEndpoint))
        {
            return CopilotModelTransport.Anthropic;
        }

        if (
            CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ResponsesEndpoint)
            || CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ResponsesWebSocketEndpoint)
        )
        {
            return CopilotModelTransport.Responses;
        }

        if (CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ChatCompletionsEndpoint))
        {
            return CopilotModelTransport.ChatCompletions;
        }

        return CopilotModelTransport.Unsupported;
    }
}
