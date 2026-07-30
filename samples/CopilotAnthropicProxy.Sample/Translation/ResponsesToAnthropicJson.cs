using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites a non-streaming OpenAI Responses reply into an Anthropic Message.
///     <see cref="ResponsesToAnthropicSse"/> does the same job for streaming replies and shares
///     <see cref="DescribeLifecycleFailure"/>, <see cref="DeriveStopReason"/>,
///     <see cref="DescribeUpstreamFailure"/>, <see cref="TryParseArguments"/> and
///     <see cref="TokenCount"/> so the two cannot drift apart.
/// </summary>
public static class ResponsesToAnthropicJson
{
    /// <summary>
    ///     Translates a Responses reply body. <paramref name="fallbackModel"/> is reported when the
    ///     reply omits <c>model</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="responsesJson"/> is not well-formed JSON, is not a JSON object, declares a
    ///     lifecycle state that is not a finished turn (see <see cref="DescribeLifecycleFailure"/>), or
    ///     carries a field whose JSON kind this translator does not expect — at the top level
    ///     (<c>id</c> present but not a string, <c>output</c> present but not an array) or anywhere
    ///     inside <c>output</c> (a non-object item, a non-array <c>content</c>, a recognised content
    ///     part missing its payload). Copilot is a live upstream, not a fixture, so a malformed or
    ///     unexpected reply must surface as this one documented type rather than leaking the underlying
    ///     <see cref="JsonException"/> or <see cref="InvalidOperationException"/>. The client's request
    ///     was accepted and well formed, so the caller answers 502 <c>api_error</c> — the failure is
    ///     upstream, not the client's.
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

            // Before anything is read out of the body: a reply that declares its own failure must never
            // be reshaped into a successful Anthropic turn, however well formed the rest of it is.
            if (DescribeLifecycleFailure(response) is { } failure)
            {
                throw new InvalidOperationException(failure);
            }

            return BuildMessage(response, fallbackModel).ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Responses reply is malformed.", nameof(responsesJson), ex);
        }
    }

    /// <summary>
    ///     Describes why a Responses body does not represent a finished, successful turn, or null when
    ///     it does. Shared with <see cref="ResponsesToAnthropicSse"/> so a buffered and a streamed reply
    ///     cannot disagree about whether the upstream succeeded.
    ///
    ///     The Responses <c>status</c> enum has exactly six members: <c>completed</c>, <c>incomplete</c>,
    ///     <c>failed</c>, <c>cancelled</c>, <c>in_progress</c> and <c>queued</c>. Only the first two are
    ///     finished turns this proxy can present as an Anthropic message — <c>incomplete</c> included,
    ///     because <see cref="DeriveStopReason"/> reports it as <c>max_tokens</c> or <c>refusal</c>.
    ///     <c>failed</c> and <c>cancelled</c> are reported as upstream failures; the two non-terminal
    ///     states, and any status a future spec adds, are reported as an unfinished turn. An absent
    ///     <c>status</c> stays legitimate: it is optional on the wire and its absence asserts nothing.
    ///
    ///     A non-null top-level <c>error</c> is treated as a failure on its own. The spec leaves it null
    ///     unless the response failed, so its presence contradicts any status claiming success — and
    ///     believing the status over the error is precisely how an HTTP-2xx failure became an empty
    ///     successful turn.
    /// </summary>
    public static string? DescribeLifecycleFailure(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var error = response["error"];
        var status = response["status"];

        if (IsStringValue(status, "failed") || IsStringValue(status, "cancelled") || error is not null)
        {
            return DescribeUpstreamFailure(error as JsonObject);
        }

        if (status is null || IsStringValue(status, "completed") || IsStringValue(status, "incomplete"))
        {
            return null;
        }

        return "The upstream Copilot reply did not report a finished turn.";
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
    ///     Derives Anthropic's <c>stop_reason</c> from a Responses reply, in priority order: a refusal —
    ///     whether reported as a classifier intervention or as official refusal output — outranks
    ///     everything, then a function call, then truncation, then a normal finish.
    ///
    ///     Refusals are checked first on purpose. A refused turn can still carry the partial output the
    ///     model produced before it declined, including a half-formed function call; reporting that as
    ///     <c>tool_use</c> would invite the client to execute it. Anthropic documents its own
    ///     <c>refusal</c> stop reason the same way: treat any partial output as incomplete.
    ///
    ///     The two representations are genuinely distinct. <c>incomplete_details.reason ==
    ///     "content_filter"</c> accompanies status <c>incomplete</c> — the turn was cut short. An
    ///     official <c>refusal</c> content part accompanies status <c>completed</c> — the model
    ///     answered, and its answer was a refusal. Either one must reach the client as
    ///     <c>stop_reason: "refusal"</c>.
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
        if (IsStringValue(incompleteReason, "content_filter") || CarriesRefusalOutput(response))
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
    ///     True when any message item carries an official <c>refusal</c> content part. Kind-safe for the
    ///     same reason <see cref="DeriveStopReason"/> is: the streamed path calls it outside a try/catch.
    /// </summary>
    private static bool CarriesRefusalOutput(JsonObject response) =>
        response["output"] is JsonArray output
        && output
            .OfType<JsonObject>()
            .Where(item => IsStringValue(item["type"], "message"))
            .SelectMany(item => (item["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Any(part => IsStringValue(part["type"], "refusal"));

    /// <summary>
    ///     Describes an upstream failure without relaying its text. A provider's error message can echo
    ///     the prompt, tool arguments, or account details back at whoever is listening, so only the
    ///     machine-readable <c>code</c> is passed through, and only when it looks like a code rather
    ///     than a sentence. Shared with <see cref="ResponsesToAnthropicSse"/> so a buffered failure and
    ///     a streamed one are described identically.
    /// </summary>
    public static string DescribeUpstreamFailure(JsonObject? error)
    {
        const string fallback = "The upstream Copilot API reported a failure.";

        return Text(error?["code"]) is { } code && IsErrorCode(code) ? $"{fallback} Upstream code: {code}." : fallback;
    }

    /// <summary>
    ///     True for a short token-shaped identifier such as <c>server_error</c> or
    ///     <c>rate_limit_exceeded</c>. Anything longer or containing whitespace or punctuation is
    ///     treated as free text and withheld.
    /// </summary>
    private static bool IsErrorCode(string code) =>
        code.Length is > 0 and <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

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
    private static bool IsStringValue(JsonNode? node, string value) => Text(node) == value;

    /// <summary>Reads a JSON string, or null when <paramref name="node"/> is absent or another kind.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    ///     Maps Responses output items onto Anthropic content blocks, in order. An empty result is a
    ///     legitimate answer — a truncated or reasoning-only turn genuinely produced no content — so
    ///     nothing is invented to fill the gap.
    ///
    ///     Every element of <c>output</c>, and every part of a recognised item's payload, is validated
    ///     rather than filtered. Skipping a corrupt element would report an unreadable upstream reply as
    ///     a successful turn that happened to say less — the same defect the <c>output</c> container
    ///     check above removes, one level deeper. Item and part TYPES this sample does not surface are a
    ///     different matter and are still skipped: the Responses catalogue grows, and an unknown type is
    ///     an upstream that is newer than this proxy, not an upstream that is broken.
    /// </summary>
    private static JsonArray BuildContent(JsonArray? output)
    {
        var content = new JsonArray();
        if (output is null)
        {
            return content;
        }

        foreach (var item in RequireObjects(output, "output item"))
        {
            switch (RequireString(item["type"], "output item's type"))
            {
                // IDE0010 requires an explicit arm here. Other Responses item types (web_search_call,
                // code_interpreter_call, mcp_call, ...) are intentionally not surfaced by this sample.
                default:
                    break;

                case "message":
                    AppendMessageContent(content, item);
                    break;

                case "reasoning":
                    AppendReasoningSummary(content, item);
                    break;

                case "function_call":
                    content.Add(
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = RequireString(item["call_id"], "function call's call_id"),
                            ["name"] = RequireString(item["name"], "function call's name"),
                            ["input"] = ParseArguments(item["arguments"]),
                        }
                    );
                    break;
            }
        }

        return content;
    }

    /// <summary>
    ///     Appends the Anthropic blocks for one <c>message</c> output item.
    ///
    ///     A <c>refusal</c> part becomes a text block. Anthropic has no refusal content block — it
    ///     reports a decline through <c>stop_reason: "refusal"</c> (which
    ///     <see cref="DeriveStopReason"/> derives from this same part) and documents that a client must
    ///     not depend on refusal text being present. Dropping the model's own wording entirely would
    ///     leave the user with an unexplained empty turn, so it is carried as text; the stop reason, not
    ///     the block type, is what a client is told to branch on.
    /// </summary>
    private static void AppendMessageContent(JsonArray content, JsonObject item)
    {
        foreach (var part in RequireObjects(RequireArray(item["content"], "message item's content"), "content part"))
        {
            switch (RequireString(part["type"], "content part's type"))
            {
                default:
                    break;

                case "output_text":
                    content.Add(
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = RequireString(part["text"], "output_text part's text"),
                        }
                    );
                    break;

                case "refusal":
                    content.Add(
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = RequireString(part["refusal"], "refusal part's refusal"),
                        }
                    );
                    break;
            }
        }
    }

    /// <summary>
    ///     Appends the Anthropic thinking block for one <c>reasoning</c> output item. Display only: the
    ///     encrypted payload that would make reasoning replayable across turns is not carried — see the
    ///     README's Known limitations.
    /// </summary>
    private static void AppendReasoningSummary(JsonArray content, JsonObject item)
    {
        var summary = string.Join(
            "\n\n",
            RequireObjects(RequireArray(item["summary"], "reasoning item's summary"), "summary part")
                .Select(part => part["text"] is null ? "" : RequireString(part["text"], "summary part's text"))
                .Where(text => text.Length > 0)
        );

        if (summary.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = summary });
        }
    }

    /// <summary>
    ///     Returns <paramref name="node"/> as an array. Absent is legitimate — an item that carries no
    ///     parts — but present-and-not-an-array is malformed for the same reason a non-array
    ///     <c>output</c> is.
    /// </summary>
    private static JsonArray RequireArray(JsonNode? node, string what) =>
        node switch
        {
            null => [],
            JsonArray array => array,
            _ => throw new InvalidOperationException($"A Responses reply's {what} must be an array."),
        };

    /// <summary>
    ///     Enumerates <paramref name="items"/>, failing on the first element that is not a JSON object.
    ///     <c>OfType&lt;JsonObject&gt;()</c> here would silently drop the corrupt element instead.
    /// </summary>
    private static IEnumerable<JsonObject> RequireObjects(JsonArray items, string what)
    {
        foreach (var item in items)
        {
            yield return item as JsonObject
                ?? throw new InvalidOperationException($"Every {what} in a Responses reply must be a JSON object.");
        }
    }

    /// <summary>Reads a field that a recognised item or part is required to carry as a string.</summary>
    private static string RequireString(JsonNode? node, string what) =>
        Text(node) ?? throw new InvalidOperationException($"A Responses reply's {what} must be a string.");

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
        if (arguments is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new InvalidOperationException("A function call's arguments must be a JSON string.");
        }

        return TryParseArguments(text, out var parsed)
            ? parsed
            : throw new InvalidOperationException("A function call's arguments must be a non-empty JSON object.");
    }

    /// <summary>
    ///     Reads a Responses function-call argument STRING as the JSON object Anthropic's
    ///     <c>tool_use.input</c> requires. Shared with <see cref="ResponsesToAnthropicSse"/>, which
    ///     applies it to the argument text it reassembled from the stream, so the same argument string
    ///     cannot be accepted streamed and rejected buffered.
    /// </summary>
    public static bool TryParseArguments(string? arguments, [NotNullWhen(true)] out JsonObject? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        try
        {
            parsed = JsonNode.Parse(arguments) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        return parsed is not null;
    }
}
