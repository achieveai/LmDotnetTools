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

        foreach (var passthrough in new[] { "temperature", "top_p" })
        {
            if (source[passthrough] is { } value)
            {
                target[passthrough] = value.DeepClone();
            }
        }

        if (BuildTools(source["tools"] as JsonArray) is { Count: > 0 } tools)
        {
            target["tools"] = tools;
        }

        if (BuildToolChoice(source["tool_choice"]) is { } toolChoice)
        {
            target["tool_choice"] = toolChoice;
        }

        return target;
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

    /// <summary>Maps Anthropic's tool_choice onto the Responses spelling.</summary>
    private static JsonNode? BuildToolChoice(JsonNode? toolChoice)
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
            "tool" => new JsonObject { ["type"] = "function", ["name"] = choice["name"]?.GetValue<string>() ?? "" },
            _ => null,
        };
    }
}
