using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     Resolves OpenAI Responses API mock output from embedded instruction-chain tags.
/// </summary>
public static class OpenAiResponsesInstructionPlanResolver
{
    /// <summary>
    ///     Resolves the instruction plan for a <c>response.create</c> request body.
    /// </summary>
    public static InstructionPlan ResolvePlan(
        JsonElement root,
        IInstructionChainParser? chainParser = null,
        ILogger? logger = null
    )
    {
        return TryResolvePlan(root, chainParser, logger)
            ?? new InstructionPlan("fallback", null, [InstructionMessage.ForText(20)]);
    }

    /// <summary>
    ///     Resolves an instruction-chain plan when the request contains embedded instructions;
    ///     returns <c>null</c> when normal scenario dispatch should handle the request.
    /// </summary>
    public static InstructionPlan? TryResolvePlan(
        JsonElement root,
        IInstructionChainParser? chainParser = null,
        ILogger? logger = null
    )
    {
        chainParser ??= new InstructionChainParser(NullLogger<InstructionChainParser>.Instance);
        logger ??= NullLogger.Instance;

        var (chain, assistantCount) = FindInstructionChain(root, chainParser);

        if (chain != null)
        {
            if (assistantCount < chain.Length)
            {
                logger.LogDebug(
                    "Executing instruction {Index} of {Total} from Responses instruction chain",
                    assistantCount + 1,
                    chain.Length
                );
                var plan = chain[assistantCount];
                ResolveDynamicMessages(plan, root, logger);
                return plan;
            }

            logger.LogDebug(
                "Responses instruction chain exhausted after {Count} executions; emitting completion",
                assistantCount
            );
            return new InstructionPlan("completion", null, [InstructionMessage.ForText(5)]);
        }

        var latest = ResponsesInputReader.ExtractLatestUserText(root, concatenateAll: false) ?? string.Empty;
        var plans = chainParser.ExtractInstructionChain(latest);
        if (plans is { Length: > 0 })
        {
            var plan = plans[0];
            ResolveDynamicMessages(plan, root, logger);
            return plan;
        }

        logger.LogDebug("No instruction chain in Responses request");
        return null;
    }

    private static void ResolveDynamicMessages(InstructionPlan plan, JsonElement requestRoot, ILogger logger)
    {
        foreach (var message in plan.Messages)
        {
            if (message.ExplicitText == "__SYSTEM_PROMPT__")
            {
                message.ExplicitText = ExtractSystemPrompt(requestRoot);
                continue;
            }

            if (message.ExplicitText == "__TOOLS_LIST__")
            {
                message.ExplicitText = ExtractToolsList(requestRoot);
                logger.LogDebug("Resolved Responses __TOOLS_LIST__ placeholder to: {ToolsList}", message.ExplicitText);
                continue;
            }

            if (
                message.ExplicitText != null
                && message.ExplicitText.StartsWith("__REQUEST_PARAMS__", StringComparison.Ordinal)
            )
            {
                var fields = message.ExplicitText.Contains(':')
                    ? message.ExplicitText.Split(':', 2)[1].Split(',')
                    : null;
                message.ExplicitText = ExtractRequestParams(requestRoot, fields);
            }
        }
    }

    private static string ExtractSystemPrompt(JsonElement root)
    {
        return
            root.TryGetProperty("instructions", out var instructions)
            && instructions.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(instructions.GetString())
            ? instructions.GetString()!
            : "No system prompt configured";
    }

    private static string ExtractToolsList(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return "No tools available";
        }

        var toolNames = new List<string>();
        var uniqueToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryAddResponseToolName(tool, uniqueToolNames, toolNames))
            {
                continue;
            }

            _ = TryAddOpenAiChatToolName(tool, uniqueToolNames, toolNames);
        }

        return toolNames.Count == 0 ? "No tools available" : string.Join(", ", toolNames);
    }

    private static bool TryAddResponseToolName(
        JsonElement tool,
        HashSet<string> uniqueToolNames,
        List<string> toolNames
    )
    {
        if (!tool.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var toolName = name.GetString();
        if (string.IsNullOrWhiteSpace(toolName) || !uniqueToolNames.Add(toolName))
        {
            return false;
        }

        toolNames.Add(toolName);
        return true;
    }

    private static bool TryAddOpenAiChatToolName(
        JsonElement tool,
        HashSet<string> uniqueToolNames,
        List<string> toolNames
    )
    {
        if (
            !tool.TryGetProperty("function", out var function)
            || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        var toolName = name.GetString();
        if (string.IsNullOrWhiteSpace(toolName) || !uniqueToolNames.Add(toolName))
        {
            return false;
        }

        toolNames.Add(toolName);
        return true;
    }

    private static string ExtractRequestParams(JsonElement root, string[]? fields)
    {
        if (fields == null || fields.Length == 0)
        {
            return root.GetRawText();
        }

        var result = new Dictionary<string, string>();
        foreach (var field in fields.Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f)))
        {
            if (root.TryGetProperty(field, out var value))
            {
                result[field] = value.ToString();
            }
        }

        return result.Count == 0 ? "No matching params" : JsonSerializer.Serialize(result);
    }

    private static (InstructionPlan[]? chain, int assistantCount) FindInstructionChain(
        JsonElement root,
        IInstructionChainParser chainParser
    )
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
        {
            return (null, 0);
        }

        var items = input.EnumerateArray().ToList();

        // Walk newest -> oldest looking for an instruction chain in any user item's input_text.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!IsRole(item, "user"))
            {
                continue;
            }

            var text = ResponsesInputReader.ReadContentText(item, concatenateAll: false);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var chain = chainParser.ExtractInstructionChain(text);
            if (chain is { Length: > 0 })
            {
                var assistantCount = CountAssistantOutputsAfter(items, i);
                return (chain, assistantCount);
            }
        }

        return (null, 0);
    }

    private static int CountAssistantOutputsAfter(IReadOnlyList<JsonElement> items, int chainIndex)
    {
        var count = 0;
        for (var i = chainIndex + 1; i < items.Count; i++)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // In the Responses input array, a "message" with role=assistant counts as one
            // model turn. Function-call/function-call-output items belong to a turn but don't
            // bump the response counter.
            if (IsRole(item, "assistant") && IsType(item, "message"))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsRole(JsonElement item, string role)
    {
        return item.TryGetProperty("role", out var r)
            && r.ValueKind == JsonValueKind.String
            && string.Equals(r.GetString(), role, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsType(JsonElement item, string type)
    {
        return item.TryGetProperty("type", out var t)
            && t.ValueKind == JsonValueKind.String
            && string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase);
    }
}
