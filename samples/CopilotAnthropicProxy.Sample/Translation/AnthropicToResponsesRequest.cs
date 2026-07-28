using System.Text.Json.Nodes;

/// <summary>
///     Rewrites an Anthropic Messages request into an OpenAI Responses request.
///
///     Builds a NEW object from an explicit allowlist rather than patching the inbound body. That is
///     deliberate: Claude Code sends a great deal that Copilot's Responses endpoint rejects outright —
///     body-level <c>betas</c> (~29 of them), <c>cache_control</c> with <c>ttl</c>/<c>scope</c>
///     sub-fields, <c>metadata.user_id</c>, and server tools such as <c>web_search_20250305</c>. An
///     allowlist drops all of it by construction; a patch-in-place would have to chase each one.
/// </summary>
public static class AnthropicToResponsesRequest
{
    /// <summary>
    ///     The smallest <c>max_output_tokens</c> the Responses API accepts.
    ///
    ///     Claude Code's FIRST request against a model it has not used is a validation probe with
    ///     <c>max_tokens: 1</c> and <c>maxRetries: 0</c>. Passed through literally it is a 400, and
    ///     Claude Code concludes the model is unusable — so every GPT model would appear broken before
    ///     the user ever got a turn. Clamping up costs nothing: the probe only checks that a
    ///     well-formed response comes back.
    /// </summary>
    public const int MinimumOutputTokens = 16;

    /// <summary>Translates an Anthropic request body. Throws <see cref="ArgumentException"/> if it is not a JSON object.</summary>
    public static string Translate(string anthropicJson)
    {
        ArgumentNullException.ThrowIfNull(anthropicJson);

        return JsonNode.Parse(anthropicJson) is JsonObject source
            ? Translate(source).ToJsonString()
            : throw new ArgumentException("An Anthropic request body must be a JSON object.", nameof(anthropicJson));
    }

    /// <summary>Translates a parsed Anthropic request body.</summary>
    public static JsonObject Translate(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new JsonObject
        {
            ["model"] = source["model"]?.DeepClone(),
            ["stream"] = (source["stream"] ?? JsonValue.Create(false)).DeepClone(),

            // The proxy holds no server-side state and the client resends full history every turn,
            // so opt out of the Responses store (whose default is true).
            ["store"] = false,
            ["input"] = BuildInput(source["messages"] as JsonArray),
        };

        var instructions = FlattenText(source["system"]);
        if (instructions.Length > 0)
        {
            target["instructions"] = instructions;
        }

        // Anthropic requires max_tokens; Responses treats max_output_tokens as optional. Omit rather
        // than invent a cap when the client did not send one.
        if (source["max_tokens"]?.GetValue<int>() is { } maxTokens)
        {
            target["max_output_tokens"] = Math.Max(maxTokens, MinimumOutputTokens);
        }

        // Copilot accepts both on a Responses-only model, so they are copied rather than dropped.
        // Probed live on 2026-07-27 because OpenAI documents its own reasoning models as REJECTING a
        // non-default temperature, which would have made this a 400 on every request that set one:
        // POST /responses {model: gpt-5.4-nano, temperature: 0.7, top_p: 0.5} answered 200 and echoed
        // "temperature": 0.7, "top_p": 0.5 — the values were honoured, not clamped back to the
        // defaults (1 and 0.98) that the same endpoint reports when neither field is sent.
        foreach (var passthrough in new[] { "temperature", "top_p" })
        {
            if (source[passthrough] is { } value)
            {
                target[passthrough] = value.DeepClone();
            }
        }

        // Ask for reasoning summaries on every translated request. Without this the Responses stream
        // carries no reasoning_summary events at all, so ResponsesToAnthropicStream has nothing to turn
        // into a thinking block and a GPT model driven from Claude Code never appears to think.
        //
        // Sent unconditionally, and only after probing whether that is safe. On 2026-07-27 all nine
        // /responses models this account serves answered 200 to {"summary":"auto"} with no effort field,
        // on both the streaming and non-streaming path; each echoed it back normalised to
        // "summary":"detailed" alongside a per-model default effort. So no served model rejects it.
        //
        // It is not uniformly PRODUCTIVE on its own, though, and not even deterministically so. Across
        // two sweeps of the same nine models with the same prompt, the models Copilot defaults to
        // "effort":"none" emitted no summary events either time, while the ones defaulting to "medium"
        // varied run to run — a different single model produced summaries in each sweep. An effort is
        // what moves a summary from impossible to usual (still not guaranteed on any one turn), and
        // BuildReasoning takes that from the client rather than imposing one, so a turn the user never
        // asked to think about costs exactly what it costs today.
        target["reasoning"] = BuildReasoning(source["thinking"]);

        // tool_choice is only considered when a non-empty tools array actually made it into the
        // request — the Responses API rejects tool_choice when tools is absent, and BuildToolChoice
        // additionally checks a "tool" choice's name against what survived BuildTools' filter.
        var tools = BuildTools(source["tools"] as JsonArray);
        if (tools is { Count: > 0 })
        {
            target["tools"] = tools;

            if (BuildToolChoice(source["tool_choice"], tools) is { } toolChoice)
            {
                target["tool_choice"] = toolChoice;
            }
        }

        return target;
    }

    /// <summary>
    ///     Builds the Responses <c>reasoning</c> field from Anthropic's top-level <c>thinking</c>.
    ///
    ///     <c>summary: "auto"</c> is unconditional — without it Copilot sends no reasoning summary
    ///     events at all, so no <c>thinking</c> block can ever be produced. Every served model accepts
    ///     it; none rejects it.
    ///
    ///     <c>effort</c> is NOT unconditional, and is never invented. Copilot gives each model a default
    ///     effort and several default to <c>"none"</c>, which means the turn does not reason and there
    ///     is nothing to summarise — so summaries alone are unreliable. The fix is to ask for effort,
    ///     but choosing a global one would change how hard every model thinks and what every request
    ///     costs. Anthropic clients already say how hard to think: extended thinking arrives as
    ///     <c>thinking: {"type":"enabled","budget_tokens":N}</c>. So the client's own budget is mapped
    ///     onto the coarse effort Responses accepts, and a request that never enabled thinking gets no
    ///     <c>effort</c> — today's behaviour and today's cost, exactly.
    /// </summary>
    private static JsonObject BuildReasoning(JsonNode? thinking)
    {
        var reasoning = new JsonObject { ["summary"] = "auto" };

        if (
            thinking is not JsonObject request
            || request["type"] is not JsonValue kind
            || !kind.TryGetValue<string>(out var type)
            || type != "enabled"
        )
        {
            return reasoning;
        }

        // The thresholds separate the tiers Claude Code itself sends, which sit near 4k, 10k and 32k
        // tokens — each lands in a different bucket with room to spare, so they are not arbitrary.
        // An enabled request with no budget means "think", and medium is the neutral reading of that.
        reasoning["effort"] =
            request["budget_tokens"] is JsonValue budget && budget.TryGetValue<int>(out var budgetTokens)
                ? budgetTokens switch
                {
                    < 8192 => "low",
                    < 24576 => "medium",
                    _ => "high",
                }
                : "medium";

        return reasoning;
    }

    /// <summary>
    ///     Flattens an Anthropic text value into a plain string. Accepts a bare string, a single block,
    ///     or an array of blocks; non-text blocks are ignored. Claude Code always sends the array form
    ///     for <c>system</c>, but the string form is legal and older clients use it.
    /// </summary>
    private static string FlattenText(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                return text;
            case JsonObject block:
                return block["type"]?.GetValue<string>() == "text" ? block["text"]?.GetValue<string>() ?? "" : "";
            case JsonArray blocks:
                var parts = blocks
                    .OfType<JsonObject>()
                    .Where(b => b["type"]?.GetValue<string>() == "text")
                    .Select(b => b["text"]?.GetValue<string>() ?? "")
                    .Where(t => t.Length > 0);
                return string.Join("\n\n", parts);
            default:
                return "";
        }
    }

    /// <summary>
    ///     Turns Anthropic's messages into a Responses <c>input</c> array.
    ///
    ///     The shapes differ structurally: Anthropic nests tool calls and tool results INSIDE message
    ///     content, while Responses makes them top-level items. So a message's text and image blocks
    ///     accumulate into one message item, and any tool block flushes that item and appends its own.
    ///     Order is preserved throughout — Responses pairs a function_call with its output by
    ///     <c>call_id</c>, but a sane ordering keeps transcripts readable and matches what Codex sends.
    /// </summary>
    private static JsonArray BuildInput(JsonArray? messages)
    {
        var input = new JsonArray();
        if (messages is null)
        {
            return input;
        }

        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = message["role"]?.GetValue<string>() ?? "user";
            var textPartType = role == "assistant" ? "output_text" : "input_text";
            JsonArray? pending = null;

            void Flush()
            {
                if (pending is { Count: > 0 })
                {
                    input.Add(
                        new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = role,
                            ["content"] = pending,
                        }
                    );
                }

                pending = null;
            }

            JsonArray Pending() => pending ??= [];

            switch (message["content"])
            {
                case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                    Pending().Add(new JsonObject { ["type"] = textPartType, ["text"] = text });
                    break;

                case JsonArray blocks:
                    foreach (var block in blocks.OfType<JsonObject>())
                    {
                        switch (block["type"]?.GetValue<string>())
                        {
                            // "thinking" / "redacted_thinking" are dropped: replaying them needs
                            // reasoning.encrypted_content round-tripping, which this sample does not do.
                            // Anything else is unknown and equally not forwarded.
                            default:
                                break;

                            case "text":
                                Pending()
                                    .Add(
                                        new JsonObject
                                        {
                                            ["type"] = textPartType,
                                            ["text"] = block["text"]?.GetValue<string>() ?? "",
                                        }
                                    );
                                break;

                            case "image" when ToImageUrl(block["source"] as JsonObject) is { } imageUrl:
                                Pending().Add(new JsonObject { ["type"] = "input_image", ["image_url"] = imageUrl });
                                break;

                            case "tool_use":
                                Flush();
                                input.Add(
                                    new JsonObject
                                    {
                                        ["type"] = "function_call",
                                        ["call_id"] = block["id"]?.GetValue<string>() ?? "",
                                        ["name"] = block["name"]?.GetValue<string>() ?? "",
                                        // Anthropic sends a JSON object; Responses wants a JSON STRING.
                                        ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString(),
                                    }
                                );
                                break;

                            case "tool_result":
                                Flush();
                                input.Add(
                                    new JsonObject
                                    {
                                        ["type"] = "function_call_output",
                                        ["call_id"] = block["tool_use_id"]?.GetValue<string>() ?? "",
                                        ["output"] = FlattenText(block["content"]),
                                    }
                                );
                                break;
                        }
                    }

                    break;

                default:
                    break;
            }

            Flush();
        }

        return input;
    }

    /// <summary>Builds a data URL (or passes a plain URL through) for an Anthropic image source.</summary>
    private static string? ToImageUrl(JsonObject? imageSource)
    {
        if (imageSource is null)
        {
            return null;
        }

        return imageSource["type"]?.GetValue<string>() switch
        {
            "url" => imageSource["url"]?.GetValue<string>(),
            "base64" =>
                $"data:{imageSource["media_type"]?.GetValue<string>() ?? "image/png"};base64,{imageSource["data"]?.GetValue<string>() ?? ""}",
            _ => null,
        };
    }

    /// <summary>
    ///     Maps Anthropic tools onto Responses function tools. ONLY entries carrying an
    ///     <c>input_schema</c> are mapped — that filter is what silently drops Claude Code's server
    ///     tools (<c>web_search_20250305</c>, <c>advisor_20260301</c>), which Copilot rejects with
    ///     400 "The use of the web search tool is not supported."
    /// </summary>
    private static JsonArray BuildTools(JsonArray? tools)
    {
        var mapped = new JsonArray();
        if (tools is null)
        {
            return mapped;
        }

        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (tool["input_schema"] is not { } schema)
            {
                continue;
            }

            var mappedTool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool["name"]?.GetValue<string>() ?? "",
                ["parameters"] = schema.DeepClone(),
            };

            if (tool["description"]?.GetValue<string>() is { Length: > 0 } description)
            {
                mappedTool["description"] = description;
            }

            mapped.Add(mappedTool);
        }

        return mapped;
    }

    /// <summary>
    ///     Maps Anthropic's tool_choice onto the Responses spelling. <paramref name="tools"/> is the
    ///     already-filtered array BuildTools produced — a "tool" choice naming an entry that filter
    ///     dropped (a server tool, e.g. web_search) resolves to null rather than pointing Responses at
    ///     a tool that no longer exists in the request.
    /// </summary>
    private static JsonNode? BuildToolChoice(JsonNode? toolChoice, JsonArray tools)
    {
        if (toolChoice is not JsonObject choice)
        {
            return null;
        }

        return choice["type"]?.GetValue<string>() switch
        {
            "auto" => JsonValue.Create("auto"),
            "none" => JsonValue.Create("none"),
            "any" => JsonValue.Create("required"),
            "tool" => NamedToolChoice(choice["name"]?.GetValue<string>(), tools),
            _ => null,
        };
    }

    /// <summary>
    ///     Builds the Responses <c>{"type":"function","name":...}</c> choice shape, but only when
    ///     <paramref name="name"/> is one of the function tools that survived BuildTools' filter.
    /// </summary>
    private static JsonObject? NamedToolChoice(string? name, JsonArray tools)
    {
        if (string.IsNullOrEmpty(name) || !tools.OfType<JsonObject>().Any(t => t["name"]?.GetValue<string>() == name))
        {
            return null;
        }

        return new JsonObject { ["type"] = "function", ["name"] = name };
    }
}
