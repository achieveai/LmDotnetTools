using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites a non-streaming OpenAI Responses reply into an Anthropic Message.
///     <see cref="ResponsesToAnthropicSse"/> does the same job for streaming replies and shares
///     <see cref="DeriveStopReason"/> and <see cref="TokenCount"/> so the two cannot drift apart.
/// </summary>
public static class ResponsesToAnthropicJson
{
    /// <summary>
    ///     Translates a Responses reply body. <paramref name="fallbackModel"/> is reported when the
    ///     reply omits <c>model</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="responsesJson"/> is not well-formed JSON, is not a JSON object, or its
    ///     fields carry a type this translator does not expect (e.g. <c>id</c> present but not a
    ///     string, or <c>output</c> present but not an array). Copilot is a live upstream, not a
    ///     fixture, so a malformed or unexpected reply must surface as this one documented type rather
    ///     than leaking the underlying <see cref="JsonException"/> or
    ///     <see cref="InvalidOperationException"/>. The client's request was accepted and well formed,
    ///     so the caller answers 502 <c>api_error</c> — the failure is upstream, not the client's.
    /// </exception>
    public static string Translate(string responsesJson, string fallbackModel)
    {
        ArgumentNullException.ThrowIfNull(responsesJson);
        ArgumentNullException.ThrowIfNull(fallbackModel);

        try
        {
            if (JsonNode.Parse(responsesJson) is not JsonObject response)
            {
                throw new ArgumentException("A Responses reply must be a JSON object.", nameof(responsesJson));
            }

            return BuildMessage(response, fallbackModel).ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Responses reply is malformed.", nameof(responsesJson), ex);
        }
    }

    /// <summary>Builds the Anthropic Message envelope for an already-parsed Responses reply.</summary>
    private static JsonObject BuildMessage(JsonObject response, string fallbackModel)
    {
        var usage = response["usage"] as JsonObject;

        return new JsonObject
        {
            ["id"] = response["id"]?.GetValue<string>() ?? "msg_proxy",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = response["model"]?.GetValue<string>() ?? fallbackModel,
            ["content"] = BuildContent(RequireOutput(response)),
            ["stop_reason"] = DeriveStopReason(response),
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = TokenCount(usage?["input_tokens"]),
                ["output_tokens"] = TokenCount(usage?["output_tokens"]),
            },
        };
    }

    /// <summary>
    ///     Returns the reply's <c>output</c> array. An absent <c>output</c> is a legitimate shape (a
    ///     reply that produced nothing), but an <c>output</c> that is present and is not an array is a
    ///     malformed reply: silently reading it as "absent" would turn an invalid upstream response
    ///     into a successful, empty Anthropic message and hide the failure from the client.
    /// </summary>
    private static JsonArray? RequireOutput(JsonObject response) =>
        response["output"] switch
        {
            null => null,
            JsonArray output => output,
            _ => throw new InvalidOperationException("A Responses reply's output must be an array."),
        };

    /// <summary>
    ///     Derives Anthropic's <c>stop_reason</c> from a Responses reply, in priority order:
    ///     classifier intervention outranks everything, then a function call, then truncation, then a
    ///     normal finish.
    ///
    ///     <c>content_filter</c> is checked first on purpose. A filtered turn can still carry the
    ///     partial output the model produced before the classifier fired, including a half-formed
    ///     function call; reporting that as <c>tool_use</c> would invite the client to execute it.
    ///
    ///     Any unrecognised <c>incomplete_details</c> shape falls through to <c>end_turn</c>. That
    ///     field is not modelled anywhere under src/OpenAiResponsesProvider, so its live shape is
    ///     confirmed by the live smoke test rather than by a fixture. Reads are kind-safe rather than
    ///     throwing: unlike <see cref="Translate"/>, this method is called directly by
    ///     <see cref="ResponsesToAnthropicSse"/> outside any try/catch, so a field present with an
    ///     unexpected JSON kind (e.g. <c>"type": 123</c>) must fall through to <c>end_turn</c> rather
    ///     than throw.
    /// </summary>
    public static string DeriveStopReason(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var incompleteReason = (response["incomplete_details"] as JsonObject)?["reason"];

        // The Responses spec defines exactly two reasons: max_output_tokens and content_filter.
        // Anthropic exposes "refusal" for the latter — a classifier stopped the turn, it did not end.
        if (IsStringValue(incompleteReason, "content_filter"))
        {
            return "refusal";
        }

        if (
            response["output"] is JsonArray output
            && output.OfType<JsonObject>().Any(item => IsStringValue(item["type"], "function_call"))
        )
        {
            return "tool_use";
        }

        if (IsStringValue(incompleteReason, "max_output_tokens"))
        {
            return "max_tokens";
        }

        return "end_turn";
    }

    /// <summary>
    ///     Reads a Responses token count. Shared with <see cref="ResponsesToAnthropicSse"/> so a
    ///     buffered and a streamed reply cannot report the same upstream figure differently.
    ///
    ///     Copilot has been observed sending these as JSON numbers that do not fit <c>int</c> and as
    ///     whole numbers written in floating-point form. Anything unreadable degrades to 0 rather than
    ///     failing the reply: a wrong cost figure is recoverable, a broken envelope is not.
    /// </summary>
    public static long TokenCount(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<long>(out var exact))
        {
            return exact;
        }

        if (!value.TryGetValue<double>(out var approximate) || double.IsNaN(approximate))
        {
            return 0;
        }

        return approximate switch
        {
            <= long.MinValue => long.MinValue,
            >= long.MaxValue => long.MaxValue,
            _ => (long)Math.Round(approximate),
        };
    }

    /// <summary>True when <paramref name="node"/> is a JSON string equal to <paramref name="value"/>.</summary>
    private static bool IsStringValue(JsonNode? node, string value) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == value;

    /// <summary>
    ///     Maps Responses output items onto Anthropic content blocks, in order. An empty result is a
    ///     legitimate answer — a truncated or reasoning-only turn genuinely produced no content — so
    ///     nothing is invented to fill the gap.
    /// </summary>
    private static JsonArray BuildContent(JsonArray? output)
    {
        var content = new JsonArray();
        if (output is null)
        {
            return content;
        }

        foreach (var item in output.OfType<JsonObject>())
        {
            switch (item["type"]?.GetValue<string>())
            {
                // IDE0010 requires an explicit arm here. Other Responses item types (web_search_call,
                // code_interpreter_call, mcp_call, ...) are intentionally not surfaced by this sample.
                default:
                    break;

                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (part["type"]?.GetValue<string>() == "output_text")
                        {
                            content.Add(
                                new JsonObject { ["type"] = "text", ["text"] = part["text"]?.GetValue<string>() ?? "" }
                            );
                        }
                    }

                    break;

                case "reasoning":
                    // Display only. The encrypted payload that would make reasoning replayable across
                    // turns is not carried — see the README's Known limitations.
                    var summary = string.Join(
                        "\n\n",
                        (item["summary"] as JsonArray ?? [])
                            .OfType<JsonObject>()
                            .Select(s => s["text"]?.GetValue<string>() ?? "")
                            .Where(t => t.Length > 0)
                    );

                    if (summary.Length > 0)
                    {
                        content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = summary });
                    }

                    break;

                case "function_call":
                    content.Add(
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = item["call_id"]?.GetValue<string>() ?? "",
                            ["name"] = item["name"]?.GetValue<string>() ?? "",
                            ["input"] = ParseArguments(item["arguments"]),
                        }
                    );
                    break;
            }
        }

        return content;
    }

    /// <summary>
    ///     Parses a function call's arguments, which Responses sends as a JSON STRING while Anthropic
    ///     expects an object.
    ///
    ///     Arguments that are missing, empty, not valid JSON, or not a JSON object fail the
    ///     translation. Substituting <c>{}</c> would hand the client a well-formed <c>tool_use</c>
    ///     whose input silently lost every argument the model chose — a shell command without its
    ///     command, a delete without its filter — and the client has no way to tell that apart from a
    ///     genuinely parameterless call. An explicit <c>"{}"</c> from the upstream still parses and is
    ///     still honoured; only a call whose arguments we could not read is rejected.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The arguments are absent or cannot be read as a JSON object. <see cref="Translate"/> wraps
    ///     this into the <see cref="ArgumentException"/> it documents.
    /// </exception>
    private static JsonNode ParseArguments(JsonNode? arguments)
    {
        if (
            arguments is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
        )
        {
            throw new InvalidOperationException("A function call's arguments must be a non-empty JSON string.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("A function call's arguments must be well-formed JSON.", ex);
        }

        return parsed as JsonObject
            ?? throw new InvalidOperationException("A function call's arguments must be a JSON object.");
    }
}
