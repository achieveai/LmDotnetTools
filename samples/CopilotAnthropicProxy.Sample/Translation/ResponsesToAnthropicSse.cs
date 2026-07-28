using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites an OpenAI Responses SSE stream into an Anthropic Messages SSE stream, one upstream
///     event at a time.
///
///     Anthropic's stream is a block cursor — message_start, then per content block a
///     content_block_start / deltas / content_block_stop triple at a monotonically increasing index,
///     then message_delta with the stop reason and usage, then message_stop. Responses emits its
///     output items in order, so tracking ONE open block is sufficient.
///
///     There is deliberately no Finish(): if the upstream stream ends without a terminal event this
///     class emits nothing more, leaving the client to observe the truncation. Capping a failed stream
///     with a synthetic message_stop would turn an upstream error into a silently empty success.
/// </summary>
public sealed class ResponsesToAnthropicSse
{
    private readonly string _fallbackMessageId;
    private readonly string _fallbackModel;

    private bool _started;
    private bool _finished;
    private int _nextIndex;
    private bool _blockOpen;

    /// <summary>
    ///     <paramref name="messageId"/> and <paramref name="model"/> are used when the upstream stream
    ///     does not announce its own — Anthropic requires both in message_start.
    /// </summary>
    public ResponsesToAnthropicSse(string messageId, string model)
    {
        _fallbackMessageId = messageId;
        _fallbackModel = model;
    }

    /// <summary>
    ///     Feeds one Responses SSE <c>data:</c> payload and returns the Anthropic frames it produces
    ///     (often none). Unparseable or unrecognised payloads produce no frames rather than an error —
    ///     a stream must not die because the upstream added an event type we have not seen.
    /// </summary>
    public IReadOnlyList<string> Next(string responsesEventJson)
    {
        if (_finished || string.IsNullOrWhiteSpace(responsesEventJson))
        {
            return [];
        }

        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(responsesEventJson) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }

        if (evt is null || Text(evt["type"]) is not { } type)
        {
            return [];
        }

        var frames = new List<string>();
        var response = evt["response"] as JsonObject;
        var item = evt["item"] as JsonObject;

        switch (type)
        {
            // IDE0010 requires an explicit arm here. Every other response.* event — the *.done
            // twins of the deltas below, response.in_progress, response.output_item.added for item
            // types this sample does not surface — is deliberately silent.
            default:
                break;

            case "response.created":
                Start(frames, response);
                break;

            case "response.content_part.added"
                when Text((evt["part"] as JsonObject)?["type"]) == "output_text":
                Start(frames, response);
                OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                break;

            case "response.output_text.delta":
                Start(frames, response);
                if (!_blockOpen)
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                }

                Delta(frames, "text_delta", "text", Text(evt["delta"]) ?? "");
                break;

            case "response.reasoning_summary_part.added":
                Start(frames, response);
                OpenBlock(frames, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                break;

            case "response.reasoning_summary_text.delta":
                Start(frames, response);
                if (!_blockOpen)
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                }

                Delta(frames, "thinking_delta", "thinking", Text(evt["delta"]) ?? "");
                break;

            case "response.output_item.added" when Text(item?["type"]) == "function_call":
                Start(frames, response);
                OpenBlock(
                    frames,
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = Text(item?["call_id"]) ?? "",
                        ["name"] = Text(item?["name"]) ?? "",
                        ["input"] = new JsonObject(),
                    }
                );
                break;

            // No block is opened here if one is not already open: unlike a text delta, a tool_use
            // block cannot be invented after the fact — its id and name only ever arrive on
            // response.output_item.added.
            case "response.function_call_arguments.delta":
                Start(frames, response);
                Delta(frames, "input_json_delta", "partial_json", Text(evt["delta"]) ?? "");
                break;

            case "response.content_part.done":
            case "response.output_item.done":
            case "response.reasoning_summary_part.done":
                CloseBlock(frames);
                break;

            case "response.completed":
            case "response.incomplete":
                Start(frames, response);
                CloseBlock(frames);
                Terminate(frames, response);
                break;
        }

        return frames;
    }

    /// <summary>Emits message_start once, taking the id and model from the upstream response if it offered them.</summary>
    private void Start(List<string> frames, JsonObject? response)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        var message = new JsonObject
        {
            ["id"] = Text(response?["id"]) ?? _fallbackMessageId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = Text(response?["model"]) ?? _fallbackModel,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,

            // Responses does not report token counts until its terminal event; the real figures
            // arrive in message_delta.
            ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
        };

        frames.Add(Frame("message_start", new JsonObject { ["type"] = "message_start", ["message"] = message }));
    }

    private void OpenBlock(List<string> frames, JsonObject contentBlock)
    {
        CloseBlock(frames);
        _blockOpen = true;

        frames.Add(
            Frame(
                "content_block_start",
                new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = _nextIndex,
                    ["content_block"] = contentBlock,
                }
            )
        );
    }

    private void Delta(List<string> frames, string deltaType, string field, string value)
    {
        if (!_blockOpen || value.Length == 0)
        {
            return;
        }

        frames.Add(
            Frame(
                "content_block_delta",
                new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _nextIndex,
                    ["delta"] = new JsonObject { ["type"] = deltaType, [field] = value },
                }
            )
        );
    }

    private void CloseBlock(List<string> frames)
    {
        if (!_blockOpen)
        {
            return;
        }

        frames.Add(
            Frame("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = _nextIndex })
        );
        _blockOpen = false;
        _nextIndex++;
    }

    private void Terminate(List<string> frames, JsonObject? response)
    {
        _finished = true;

        var usage = response?["usage"] as JsonObject;

        frames.Add(
            Frame(
                "message_delta",
                new JsonObject
                {
                    ["type"] = "message_delta",
                    ["delta"] = new JsonObject
                    {
                        ["stop_reason"] = response is null
                            ? "end_turn"
                            : ResponsesToAnthropicJson.DeriveStopReason(response),
                        ["stop_sequence"] = null,
                    },
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = TokenCount(usage?["input_tokens"]),
                        ["output_tokens"] = TokenCount(usage?["output_tokens"]),
                    },
                }
            )
        );

        frames.Add(Frame("message_stop", new JsonObject { ["type"] = "message_stop" }));
    }

    /// <summary>
    ///     Reads a JSON string, or null if <paramref name="node"/> is absent or carries another kind.
    ///     Every read of a live upstream frame goes through this rather than <c>GetValue&lt;string&gt;()</c>:
    ///     the reads happen outside the parse try/catch, and an <see cref="InvalidOperationException"/>
    ///     escaping mid-stream would reach the client as an unexplained truncation with no error frame.
    /// </summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Reads a token count, defaulting to 0 when it is absent or not a JSON integer.</summary>
    private static int TokenCount(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<int>(out var count) ? count : 0;

    private static string Frame(string eventName, JsonObject payload) =>
        $"event: {eventName}\ndata: {payload.ToJsonString()}\n\n";
}
