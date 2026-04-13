using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     Default implementation of instruction chain parser for test mode.
/// </summary>
public sealed class InstructionChainParser(ILogger<InstructionChainParser> logger) : IInstructionChainParser
{
    private const string InstructionStartTag = "<|instruction_start|>";
    private const string InstructionEndTag = "<|instruction_end|>";

    private readonly ILogger<InstructionChainParser> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public InstructionPlan[]? ExtractInstructionChain(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogDebug("Content is null or whitespace, no instruction chain to extract");
            return null;
        }

        var start = content.IndexOf(InstructionStartTag, StringComparison.Ordinal);
        var end = content.IndexOf(InstructionEndTag, StringComparison.Ordinal);

        if (start < 0 || end <= start)
        {
            _logger.LogDebug("Instruction tags not found in content");
            return null;
        }

        var json = content
            .Substring(start + InstructionStartTag.Length, end - start - InstructionStartTag.Length)
            .Trim();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for instruction_chain array format
            if (root.TryGetProperty("instruction_chain", out var chainEl) && chainEl.ValueKind == JsonValueKind.Array)
            {
                var chain = new List<InstructionPlan>();
                foreach (var item in chainEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var plan = ParseSingleInstruction(item);
                    if (plan != null)
                    {
                        chain.Add(plan);
                    }
                }

                if (chain.Count > 0)
                {
                    _logger.LogInformation("Parsed instruction chain with {Count} instructions", chain.Count);

                    return [.. chain];
                }

                // Empty chain array - return null to indicate no instructions
                _logger.LogDebug("Empty instruction chain array found");
                return null;
            }

            // Backward compatibility: single instruction format
            var singleInstruction = ParseSingleInstruction(root);
            if (singleInstruction != null)
            {
                _logger.LogInformation("Parsed single instruction (backward compatibility mode)");
                return [singleInstruction];
            }

            _logger.LogWarning("No valid instruction format found in JSON");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse instruction chain JSON, returning null");
            return null;
        }
    }

    /// <inheritdoc />
    public InstructionPlan? ParseSingleInstruction(JsonElement instructionEl)
    {
        if (instructionEl.ValueKind != JsonValueKind.Object)
        {
            _logger.LogDebug("Instruction element is not an object");
            return null;
        }

        // Extract id (optional, but useful for logging)
        var id =
            instructionEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty
                : string.Empty;

        // Extract id_message (used only for logging, not emitted as content)
        var idMessage =
            instructionEl.TryGetProperty("id_message", out var idMsgEl) && idMsgEl.ValueKind == JsonValueKind.String
                ? idMsgEl.GetString() ?? string.Empty
                : string.Empty;

        // Extract reasoning length (optional)
        int? reasoningLen = null;
        if (instructionEl.TryGetProperty("reasoning", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.Object)
        {
            if (
                reasonEl.TryGetProperty("length", out var lenEl)
                && lenEl.ValueKind == JsonValueKind.Number
                && lenEl.TryGetInt32(out var len)
            )
            {
                reasoningLen = Math.Max(0, len);
            }
        }

        // Extract messages array
        var messages = ParseInstructionMessages(instructionEl);

        // An instruction is valid if it has messages OR reasoning
        if (messages.Count == 0 && reasoningLen is null)
        {
            _logger.LogWarning("No valid messages or reasoning found in instruction");
            return null;
        }

        _logger.LogDebug(
            "Parsed instruction: id={Id}, idMessage={IdMessage}, messageCount={Count}, reasoningLength={ReasoningLength}",
            id,
            idMessage,
            messages.Count,
            reasoningLen
        );

        return new InstructionPlan(idMessage, reasoningLen, messages);
    }

    private static List<InstructionMessage> ParseInstructionMessages(JsonElement instructionEl)
    {
        var messages = new List<InstructionMessage>();

        if (!instructionEl.TryGetProperty("messages", out var msgsEl) || msgsEl.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var item in msgsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Check for text message (lorem ipsum of given length)
            if (item.TryGetProperty("text_message", out var textEl) && textEl.ValueKind == JsonValueKind.Object)
            {
                if (
                    textEl.TryGetProperty("length", out var lEl)
                    && lEl.ValueKind == JsonValueKind.Number
                    && lEl.TryGetInt32(out var tlen)
                )
                {
                    messages.Add(InstructionMessage.ForText(Math.Max(0, tlen)));
                    continue;
                }
            }

            // Check for explicit text content: {"text": "some string"}
            if (
                item.TryGetProperty("text", out var explicitTextEl)
                && explicitTextEl.ValueKind == JsonValueKind.String
            )
            {
                var explicitText = explicitTextEl.GetString();
                if (!string.IsNullOrEmpty(explicitText))
                {
                    messages.Add(InstructionMessage.ForExplicitText(explicitText));
                    continue;
                }
            }

            // Check for tool calls
            if (item.TryGetProperty("tool_call", out var toolEl) && toolEl.ValueKind == JsonValueKind.Array)
            {
                var calls = ParseToolCalls(toolEl);
                if (calls.Count > 0)
                {
                    messages.Add(InstructionMessage.ForToolCalls(calls));
                    continue;
                }
            }

            // Check for system_prompt_echo - returns the system prompt as text
            if (item.TryGetProperty("system_prompt_echo", out _))
            {
                messages.Add(InstructionMessage.ForExplicitText("__SYSTEM_PROMPT__"));
                continue;
            }

            // Check for tools_list - returns names of all visible tools
            if (item.TryGetProperty("tools_list", out _))
            {
                messages.Add(InstructionMessage.ForExplicitText("__TOOLS_LIST__"));
                continue;
            }

            // Check for request_url_echo - returns the request URL as text
            if (item.TryGetProperty("request_url_echo", out _))
            {
                messages.Add(InstructionMessage.ForExplicitText("__REQUEST_URL__"));
                continue;
            }

            // Check for request_headers_echo - returns request headers as text
            if (item.TryGetProperty("request_headers_echo", out _))
            {
                messages.Add(InstructionMessage.ForExplicitText("__REQUEST_HEADERS__"));
                continue;
            }

            // Check for request_params_echo - returns request body params as text
            if (
                item.TryGetProperty("request_params_echo", out var paramsEchoEl)
                && paramsEchoEl.ValueKind == JsonValueKind.Object
            )
            {
                List<string>? fields = null;
                if (
                    paramsEchoEl.TryGetProperty("fields", out var fieldsEl)
                    && fieldsEl.ValueKind == JsonValueKind.Array
                )
                {
                    fields = [.. fieldsEl
                        .EnumerateArray()
                        .Where(f => f.ValueKind == JsonValueKind.String)
                        .Select(f => f.GetString()!)];
                }

                var placeholder =
                    fields != null && fields.Count > 0
                        ? $"__REQUEST_PARAMS__:{string.Join(",", fields)}"
                        : "__REQUEST_PARAMS__";
                messages.Add(InstructionMessage.ForExplicitText(placeholder));
                continue;
            }

            // Check for server_tool_use (built-in tools like web_search)
            if (
                item.TryGetProperty("server_tool_use", out var serverToolUseEl)
                && serverToolUseEl.ValueKind == JsonValueKind.Object
            )
            {
                var serverToolUse = ParseServerToolUse(serverToolUseEl);
                if (serverToolUse != null)
                {
                    messages.Add(InstructionMessage.ForServerToolUse(serverToolUse));
                    continue;
                }
            }

            // Check for server_tool_result
            if (
                item.TryGetProperty("server_tool_result", out var serverToolResultEl)
                && serverToolResultEl.ValueKind == JsonValueKind.Object
            )
            {
                var serverToolResult = ParseServerToolResult(serverToolResultEl);
                if (serverToolResult != null)
                {
                    messages.Add(InstructionMessage.ForServerToolResult(serverToolResult));
                    continue;
                }
            }

            // Check for text_with_citations
            if (
                item.TryGetProperty("text_with_citations", out var textWithCitationsEl)
                && textWithCitationsEl.ValueKind == JsonValueKind.Object
            )
            {
                var textWithCitations = ParseTextWithCitations(textWithCitationsEl);
                if (textWithCitations != null)
                {
                    messages.Add(InstructionMessage.ForTextWithCitations(textWithCitations));
                    continue;
                }
            }
        }

        return messages;
    }

    private static InstructionServerToolUse? ParseServerToolUse(JsonElement element)
    {
        var name =
            element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var id =
            element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

        JsonElement? input = element.TryGetProperty("input", out var inputEl) ? inputEl.Clone() : null;

        return new InstructionServerToolUse { Id = id, Name = name, Input = input };
    }

    private static InstructionServerToolResult? ParseServerToolResult(JsonElement element)
    {
        var name =
            element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var toolUseId =
            element.TryGetProperty("tool_use_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

        var errorCode =
            element.TryGetProperty("error_code", out var errorEl) && errorEl.ValueKind == JsonValueKind.String
                ? errorEl.GetString()
                : null;

        JsonElement? result = element.TryGetProperty("result", out var resultEl) ? resultEl.Clone() : null;

        return new InstructionServerToolResult
        {
            ToolUseId = toolUseId,
            Name = name,
            Result = result,
            ErrorCode = errorCode,
        };
    }

    private static InstructionTextWithCitations? ParseTextWithCitations(JsonElement element)
    {
        string? text = null;
        int? length = null;

        if (element.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            text = textEl.GetString();
        }

        if (
            element.TryGetProperty("length", out var lengthEl)
            && lengthEl.ValueKind == JsonValueKind.Number
            && lengthEl.TryGetInt32(out var len)
        )
        {
            length = Math.Max(0, len);
        }

        // Must have either text or length
        if (text == null && length == null)
        {
            return null;
        }

        List<InstructionCitation>? citations = null;
        if (element.TryGetProperty("citations", out var citationsEl) && citationsEl.ValueKind == JsonValueKind.Array)
        {
            citations = [];
            foreach (var citationEl in citationsEl.EnumerateArray())
            {
                if (citationEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type =
                    citationEl.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString() ?? "url_citation"
                        : "url_citation";

                var url =
                    citationEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                        ? urlEl.GetString()
                        : null;

                var title =
                    citationEl.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                        ? titleEl.GetString()
                        : null;

                var citedText =
                    citationEl.TryGetProperty("cited_text", out var citedEl) && citedEl.ValueKind == JsonValueKind.String
                        ? citedEl.GetString()
                        : null;

                citations.Add(
                    new InstructionCitation { Type = type, Url = url, Title = title, CitedText = citedText }
                );
            }
        }

        return new InstructionTextWithCitations { Text = text, Length = length, Citations = citations };
    }

    private static List<InstructionToolCall> ParseToolCalls(JsonElement toolCallsElement)
    {
        var calls = new List<InstructionToolCall>();

        foreach (var call in toolCallsElement.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name =
                call.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString() ?? string.Empty
                    : string.Empty;

            var argsObj = call.TryGetProperty("args", out var aEl) ? aEl : default;
            var argsJson = argsObj.ValueKind != JsonValueKind.Undefined ? argsObj.GetRawText() : "{}";

            calls.Add(new InstructionToolCall(name, argsJson));
        }

        return calls;
    }
}
