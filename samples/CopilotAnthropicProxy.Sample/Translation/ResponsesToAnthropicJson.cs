using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites a non-streaming OpenAI Responses reply into an Anthropic Message.
///     <c>ResponsesToAnthropicSse</c> does the same job for streaming replies and shares
///     <see cref="DeriveStopReason"/> so the two cannot drift apart.
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
    ///     string). Copilot is a live upstream, not a fixture, so a malformed or unexpected reply must
    ///     surface as this one documented type rather than leaking the underlying
    ///     <see cref="JsonException"/> or <see cref="InvalidOperationException"/> — callers (Task 9's
    ///     endpoint) branch on that to answer 400 instead of 500.
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
            ["content"] = BuildContent(response["output"] as JsonArray),
            ["stop_reason"] = DeriveStopReason(response),
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["input_tokens"]?.GetValue<int>() ?? 0,
                ["output_tokens"] = usage?["output_tokens"]?.GetValue<int>() ?? 0,
            },
        };
    }

    /// <summary>
    ///     Derives Anthropic's <c>stop_reason</c> from a Responses reply, in priority order:
    ///     a function call outranks truncation, truncation outranks a normal finish.
    ///
    ///     Any unrecognised <c>incomplete_details</c> shape falls through to <c>end_turn</c>. That
    ///     field is not modelled anywhere under src/OpenAiResponsesProvider, so its live shape is
    ///     confirmed by the live smoke test rather than by a fixture.
    /// </summary>
    public static string DeriveStopReason(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (
            response["output"] is JsonArray output
            && output.OfType<JsonObject>().Any(item => item["type"]?.GetValue<string>() == "function_call")
        )
        {
            return "tool_use";
        }

        if ((response["incomplete_details"] as JsonObject)?["reason"]?.GetValue<string>() == "max_output_tokens")
        {
            return "max_tokens";
        }

        return "end_turn";
    }

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
                // Other Responses item types (web_search_call, code_interpreter_call, mcp_call, ...)
                // are not surfaced by this sample proxy; skip them rather than fail the whole reply.
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
                            ["input"] = ParseArguments(item["arguments"]?.GetValue<string>()),
                        }
                    );
                    break;
            }
        }

        return content;
    }

    /// <summary>
    ///     Parses a function call's arguments, which Responses sends as a JSON STRING while Anthropic
    ///     expects an object. Malformed or empty arguments become an empty object rather than an error:
    ///     a client can recover from a tool call with no arguments, but not from a broken envelope.
    /// </summary>
    private static JsonNode ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
