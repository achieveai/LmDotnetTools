using System.Text;
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
///     with a synthetic message_stop would turn an upstream error into a silently empty success. A
///     failure the upstream DOES report — an <c>error</c> or <c>response.failed</c> event, a terminal
///     event whose response declares a failed lifecycle, or a tool call whose arguments could not be
///     reassembled — is the opposite case and is translated into Anthropic's terminal <c>error</c>
///     event.
/// </summary>
public sealed class ResponsesToAnthropicSse
{
    private readonly string _fallbackMessageId;
    private readonly string _fallbackModel;

    /// <summary>
    ///     The argument text reassembled so far for the open <c>tool_use</c> block, exactly as it was
    ///     forwarded to the client in <c>input_json_delta</c> frames. Anthropic's client concatenates
    ///     those fragments and parses the result, so this is the client's view of the call — which is
    ///     what has to be proven complete before the block is allowed to close.
    /// </summary>
    private readonly StringBuilder _toolArguments = new();

    private bool _started;
    private bool _finished;
    private int _nextIndex;

    /// <summary>
    ///     The <c>content_block.type</c> of the block currently open ("text" / "thinking" /
    ///     "tool_use"), or null when none is. The KIND matters, not just the fact of a block being
    ///     open: a delta must never be written into a block of a different type, which is malformed
    ///     rather than merely degraded. The upstream is not trusted to close its own blocks — the
    ///     auto-open fallbacks below exist precisely because it sometimes does not.
    /// </summary>
    private string? _openKind;

    /// <summary>How many deltas have been written into the open block. Reset when the block closes.</summary>
    private int _openBlockDeltas;

    /// <summary>
    ///     Set once the upstream has reported an official refusal, through a <c>refusal</c> content
    ///     part or a <c>response.refusal.*</c> event. The terminal event's response usually carries the
    ///     same refusal part and <see cref="ResponsesToAnthropicJson.DeriveStopReason"/> would find it,
    ///     but Copilot has been observed sending lean terminal payloads, and a refusal reported as
    ///     <c>end_turn</c> is a safety decline presented to the user as a normal answer.
    /// </summary>
    private bool _sawRefusal;

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
        var partType = Text((evt["part"] as JsonObject)?["type"]);

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

            // A refusal part opens a text block, for the same reason the buffered translator emits one:
            // Anthropic has no refusal content block and reports the decline through stop_reason.
            case "response.content_part.added" when partType is "output_text" or "refusal":
                Start(frames, response);
                _sawRefusal |= partType == "refusal";
                OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                break;

            case "response.output_text.delta":
                Start(frames, response);
                if (_openKind != "text")
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                }

                Delta(frames, "text", "text_delta", "text", Text(evt["delta"]) ?? "");
                break;

            // ResponseRefusalDeltaEvent carries its fragment under "delta", exactly like output text.
            case "response.refusal.delta":
                Start(frames, response);
                _sawRefusal = true;
                if (_openKind != "text")
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                }

                Delta(frames, "text", "text_delta", "text", Text(evt["delta"]) ?? "");
                break;

            // ResponseRefusalDoneEvent carries the COMPLETE refusal under "refusal", not "delta". It is
            // the only carrier when the upstream sent no refusal deltas at all, so it is adopted then;
            // once deltas have been forwarded, re-sending the whole text would duplicate it.
            case "response.refusal.done":
                Start(frames, response);
                _sawRefusal = true;
                if (_openKind != "text")
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                }

                if (_openBlockDeltas == 0)
                {
                    Delta(frames, "text", "text_delta", "text", Text(evt["refusal"]) ?? "");
                }

                break;

            case "response.reasoning_summary_part.added":
                Start(frames, response);
                OpenBlock(frames, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                break;

            case "response.reasoning_summary_text.delta":
                Start(frames, response);
                if (_openKind != "thinking")
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                }

                Delta(frames, "thinking", "thinking_delta", "thinking", Text(evt["delta"]) ?? "");
                break;

            // A tool call the client can neither correlate nor dispatch is not a tool call. call_id is
            // what the client echoes back in tool_result and name is what it dispatches on, and the
            // only other event carrying either is output_item.done — so an announcement missing one
            // fails the stream rather than opening a block that can never be honoured.
            case "response.output_item.added" when Text(item?["type"]) == "function_call":
                Start(frames, response);
                if (Text(item?["call_id"]) is not { Length: > 0 } callId)
                {
                    Fail(frames, "The upstream announced a tool call with no readable call id.");
                    break;
                }

                if (Text(item?["name"]) is not { Length: > 0 } toolName)
                {
                    Fail(frames, "The upstream announced a tool call with no readable name.");
                    break;
                }

                OpenBlock(
                    frames,
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = callId,
                        ["name"] = toolName,
                        ["input"] = new JsonObject(),
                    }
                );
                break;

            // No block is opened here: unlike a text delta, a tool_use block cannot be invented after
            // the fact — its id and name only ever arrive on response.output_item.added. If that event
            // was missing or unreadable there is nowhere to put these arguments, and writing them into
            // whatever block happens to be open would splice tool-call JSON into rendered assistant
            // text. Dropping them silently is equally wrong: the client would be handed a tool call
            // with no arguments, or none at all, and no way to know arguments existed. So the stream
            // fails visibly instead — and for the same reason, a fragment that cannot be READ ends the
            // stream too. Ignoring it leaves the client concatenating a hole in the middle of the
            // argument JSON, which either fails to parse or, worse, parses into a different call.
            case "response.function_call_arguments.delta":
                Start(frames, response);
                if (_openKind != "tool_use")
                {
                    Fail(frames, "The upstream sent tool call arguments for a tool call it never announced.");
                    break;
                }

                if (Text(evt["delta"]) is not { } fragment)
                {
                    Fail(frames, "The upstream sent a tool call argument fragment that could not be read.");
                    break;
                }

                _toolArguments.Append(fragment);
                Delta(frames, "tool_use", "input_json_delta", "partial_json", fragment);
                break;

            // ResponseFunctionCallArgumentsDoneEvent carries the COMPLETE argument string. It is
            // authoritative: it is what the model actually chose, and it is the only carrier when the
            // deltas never arrived or could not be read.
            case "response.function_call_arguments.done":
                Start(frames, response);
                if (_openKind != "tool_use")
                {
                    Fail(frames, "The upstream completed tool call arguments for a tool call it never announced.");
                    break;
                }

                AdoptCompleteArguments(frames, evt["arguments"]);
                break;

            case "response.content_part.done":
                _sawRefusal |= partType == "refusal";
                CloseBlock(frames);
                break;

            case "response.reasoning_summary_part.done":
                CloseBlock(frames);
                break;

            // The completed item carries the final function_call, including its arguments — the last
            // chance to reconcile what the client was streamed against what the model actually chose.
            case "response.output_item.done":
                if (_openKind == "tool_use" && !AdoptCompleteArguments(frames, item?["arguments"]))
                {
                    break;
                }

                CloseBlock(frames);
                break;

            case "response.completed":
            case "response.incomplete":
                Start(frames, response);

                // A terminal event whose own response declares a failed or unfinished lifecycle is the
                // streamed twin of the buffered check, answered the same way rather than dressed up as
                // a completed turn.
                if (response is not null && ResponsesToAnthropicJson.DescribeLifecycleFailure(response) is { } broken)
                {
                    Fail(frames, broken);
                    break;
                }

                if (!CloseBlock(frames))
                {
                    break;
                }

                Terminate(frames, response);
                break;

            // The two ways a Responses stream reports failure. "error" is a top-level semantic event
            // carrying {code, message, param}; "response.failed" carries the same under response.error.
            // Both used to land in the silent default arm, so the client saw an unexplained truncation
            // and could not tell a failure from a dropped connection.
            case "error":
                Fail(frames, ResponsesToAnthropicJson.DescribeUpstreamFailure(evt));
                break;

            case "response.failed":
                Fail(frames, ResponsesToAnthropicJson.DescribeUpstreamFailure(response?["error"] as JsonObject));
                break;
        }

        return frames;
    }

    /// <summary>Emits message_start once, taking the id and model from the upstream response if it offered them.</summary>
    private void Start(List<string> frames, JsonObject? response)
    {
        if (_started || _finished)
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

    /// <summary>
    ///     Closes any block still open and starts a new one at the next index. The kind is taken from
    ///     the block itself, so <see cref="_openKind"/> cannot drift from what was announced. Nothing is
    ///     opened when closing the previous block failed — the stream is already ending.
    /// </summary>
    private void OpenBlock(List<string> frames, JsonObject contentBlock)
    {
        if (!CloseBlock(frames))
        {
            return;
        }

        _openKind = Text(contentBlock["type"]);

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

    /// <summary>
    ///     Appends a delta to the open block, but only when that block is of
    ///     <paramref name="requiredKind"/>. A tool call's arguments spliced into rendered assistant
    ///     text, or a text delta at the index of a thinking block, is worse than a dropped delta:
    ///     a client validating delta type against the block it opened rejects the whole stream.
    /// </summary>
    private void Delta(List<string> frames, string requiredKind, string deltaType, string field, string value)
    {
        if (_openKind != requiredKind || value.Length == 0)
        {
            return;
        }

        _openBlockDeltas++;

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

    /// <summary>
    ///     Reconciles the authoritative complete argument string an upstream <c>*.done</c> event carries
    ///     against the fragments already forwarded to the client, and returns false when the stream was
    ///     failed instead.
    ///
    ///     Three cases. Absent: the event did not offer one, so the fragments remain the only source and
    ///     <see cref="CloseBlock"/> validates them. Nothing forwarded yet: the complete string IS the
    ///     call — ignoring it is how a lost delta could still produce a tool call announced to the
    ///     client with <c>input: {}</c>. Already forwarded and different: the client has been streamed
    ///     a call the model did not make and no later frame can retract it, so the stream fails.
    /// </summary>
    private bool AdoptCompleteArguments(List<string> frames, JsonNode? arguments)
    {
        if (arguments is null)
        {
            return true;
        }

        if (Text(arguments) is not { } complete)
        {
            Fail(frames, "The upstream reported completed tool call arguments that could not be read.");
            return false;
        }

        if (_toolArguments.Length == 0)
        {
            _toolArguments.Append(complete);
            Delta(frames, "tool_use", "input_json_delta", "partial_json", complete);
            return true;
        }

        if (!string.Equals(_toolArguments.ToString(), complete, StringComparison.Ordinal))
        {
            Fail(frames, "The upstream's completed tool call arguments do not match the fragments it streamed.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Closes the open block, and returns false when the stream was failed instead of closed.
    ///
    ///     A <c>tool_use</c> block may only close once the argument text streamed to the client reads
    ///     back as the JSON object Anthropic's <c>tool_use.input</c> requires — through the SAME check
    ///     the buffered translator applies, so one argument string cannot be accepted streamed and
    ///     rejected buffered. Closing it otherwise hands the client an executable call whose input
    ///     silently lost every argument the model chose, which it cannot tell apart from a
    ///     parameterless call.
    /// </summary>
    private bool CloseBlock(List<string> frames)
    {
        if (_openKind is null)
        {
            return true;
        }

        if (_openKind == "tool_use" && !ResponsesToAnthropicJson.TryParseArguments(_toolArguments.ToString(), out _))
        {
            Fail(frames, "The upstream completed a tool call whose arguments could not be reassembled.");
            return false;
        }

        EmitBlockStop(frames);
        return true;
    }

    /// <summary>
    ///     Emits content_block_stop unconditionally and resets the per-block state. Used by
    ///     <see cref="CloseBlock"/> once its checks pass, and by <see cref="Fail"/>, which must not
    ///     re-run those checks: the stream is already ending in an error frame.
    /// </summary>
    private void EmitBlockStop(List<string> frames)
    {
        if (_openKind is null)
        {
            return;
        }

        frames.Add(
            Frame("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = _nextIndex })
        );
        _openKind = null;
        _openBlockDeltas = 0;
        _toolArguments.Clear();
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
                    ["delta"] = new JsonObject { ["stop_reason"] = StopReason(response), ["stop_sequence"] = null },
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
    ///     The stop reason for the terminal message_delta. A refusal seen ON THE STREAM outranks the
    ///     terminal payload: that payload is what the buffered translator would read and the two must
    ///     not disagree, but a lean terminal payload that omits the refusal part must not downgrade a
    ///     decline this translator already watched arrive.
    /// </summary>
    private string StopReason(JsonObject? response)
    {
        if (_sawRefusal)
        {
            return "refusal";
        }

        return response is null ? "end_turn" : ResponsesToAnthropicJson.DeriveStopReason(response);
    }

    /// <summary>
    ///     Ends the stream with Anthropic's terminal <c>error</c> event. No message_delta and no
    ///     message_stop follow: the turn did not complete, and claiming it did is exactly the silent
    ///     failure this arm exists to remove. Any open block is closed first so the client's block
    ///     cursor is not left dangling.
    /// </summary>
    private void Fail(List<string> frames, string message)
    {
        EmitBlockStop(frames);
        _finished = true;

        frames.Add(
            Frame(
                "error",
                new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
                }
            )
        );
    }

    /// <summary>
    ///     Reads a JSON string, or null if <paramref name="node"/> is absent or carries another kind.
    ///     Every read of a live upstream frame goes through this rather than <c>GetValue&lt;string&gt;()</c>:
    ///     the reads happen outside the parse try/catch, and an <see cref="InvalidOperationException"/>
    ///     escaping mid-stream would reach the client as an unexplained truncation with no error frame.
    /// </summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    ///     Reads a token count through the same policy the buffered translator uses, so a streamed and
    ///     a buffered reply cannot report the same upstream figure differently.
    /// </summary>
    private static long TokenCount(JsonNode? node) => ResponsesToAnthropicJson.TokenCount(node);

    private static string Frame(string eventName, JsonObject payload) =>
        $"event: {eventName}\ndata: {payload.ToJsonString()}\n\n";
}
