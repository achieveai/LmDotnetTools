using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Static utility class for transforming ToolsCallAggregateMessage back to natural language format
///     with XML-style tool calls and responses.
/// </summary>
public static class ToolsCallAggregateTransformer
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Transforms a ToolsCallAggregateMessage to natural language format with XML-style tool calls.
    /// </summary>
    /// <param name="aggregateMessage">The aggregate message containing tool calls and results</param>
    /// <returns>A TextMessage with embedded XML tool calls and responses</returns>
    public static TextMessage TransformToNaturalFormat(ToolsCallAggregateMessage aggregateMessage)
    {
        ArgumentNullException.ThrowIfNull(aggregateMessage);

        var contentBuilder = new StringBuilder();
        var toolCalls = aggregateMessage.ToolsCallMessage.ToolCalls;
        var toolResults = aggregateMessage.ToolsCallResult.ToolCallResults;

        // Process each tool call with its corresponding result
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var toolCall = toolCalls[i];

            // Find matching result by ToolCallId or by index
            ToolCallResult? toolResult = null;

            // First try to find by ToolCallId
            foreach (var result in toolResults)
            {
                if (result.ToolCallId == toolCall.ToolCallId)
                {
                    toolResult = result;
                    break;
                }
            }

            // If not found by ID, try by index
            if (toolResult == null && i < toolResults.Count)
            {
                toolResult = toolResults[i];
            }

            // If still no result, create a placeholder
            if (toolResult == null)
            {
                toolResult = new ToolCallResult(toolCall.ToolCallId, "No result available");
            }

            // Add separator between multiple tool calls
            if (i > 0)
            {
                _ = contentBuilder.AppendLine("---");
            }

            // Format this tool call and response pair
            var formattedToolPair = FormatToolCallAndResponse(toolCall, toolResult.Value);
            _ = contentBuilder.Append(formattedToolPair);
        }

        // Create the result TextMessage with preserved metadata
        return new TextMessage
        {
            Text = contentBuilder.ToString(),
            Role = Role.Assistant, // Tool calls are from assistant
            FromAgent = aggregateMessage.FromAgent,
            GenerationId = aggregateMessage.GenerationId,
            Metadata = aggregateMessage.Metadata,
        };
    }

    /// <summary>
    ///     Combines a sequence of messages that may include TextMessages and ToolsCallAggregateMessages
    ///     into a single TextMessage with natural tool use format.
    /// </summary>
    /// <param name="messageSequence">The sequence of messages to combine</param>
    /// <returns>A single TextMessage containing all content</returns>
    public static TextMessage CombineMessageSequence(IEnumerable<IMessage> messageSequence)
    {
        ArgumentNullException.ThrowIfNull(messageSequence);

        var messages = messageSequence.ToList();
        if (messages.Count == 0)
        {
            return new TextMessage { Text = string.Empty, Role = Role.Assistant };
        }

        var contentBuilder = new StringBuilder();
        var combinedMetadata = ImmutableDictionary<string, object>.Empty;
        var lastRole = Role.Assistant;
        string? fromAgent = null;
        string? generationId = null;

        foreach (var message in messages)
        {
            // Extract text content based on message type
            var messageText = message switch
            {
                TextMessage textMsg => textMsg.Text,
                ToolsCallAggregateMessage aggregateMsg => TransformToNaturalFormat(aggregateMsg).Text,
                ICanGetText textInterface => textInterface.GetText() ?? string.Empty,
                _ => string.Empty,
            };

            // Append the content
            if (contentBuilder.Length > 0 && !string.IsNullOrWhiteSpace(messageText))
            {
                _ = contentBuilder.AppendLine(); // Add spacing between messages
            }

            _ = contentBuilder.Append(messageText);

            // Merge metadata from this message
            if (message.Metadata != null)
            {
                combinedMetadata = MergeMetadata(combinedMetadata, message.Metadata);
            }

            // Use last non-null values for other properties
            lastRole = message.Role;
            fromAgent ??= message.FromAgent;
            generationId ??= message.GenerationId;
        }

        return new TextMessage
        {
            Text = contentBuilder.ToString(),
            Role = lastRole,
            FromAgent = fromAgent,
            GenerationId = generationId,
            Metadata = combinedMetadata.Count > 0 ? combinedMetadata : null,
        };
    }

    /// <summary>
    ///     Formats a single tool call and its result as XML.
    /// </summary>
    /// <param name="toolCall">The tool call to format</param>
    /// <param name="toolResult">The corresponding tool result</param>
    /// <returns>XML formatted string for the tool call and response pair</returns>
    public static string FormatToolCallAndResponse(ToolCall toolCall, ToolCallResult toolResult)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var functionName = toolCall.FunctionName ?? "UnknownFunction";
        var formattedArgs = FormatToolArguments(toolCall.FunctionArgs);
        var formattedResponse = FormatToolResponse(toolResult.Result);

        var result = new StringBuilder();

        // Format tool call
        _ = result.AppendLine($"<tool_call name=\"{functionName}\">");
        _ = result.AppendLine(formattedArgs);
        _ = result.AppendLine("</tool_call>");

        // Format tool response
        _ = result.AppendLine($"<tool_response name=\"{functionName}\">");
        _ = result.AppendLine(formattedResponse);
        _ = result.Append("</tool_response>");

        return result.ToString();
    }

    /// <summary>
    ///     Formats tool arguments, pretty-printing JSON if applicable.
    /// </summary>
    /// <param name="functionArgs">The function arguments string</param>
    /// <returns>Formatted arguments string</returns>
    private static string FormatToolArguments(string? functionArgs)
    {
        return string.IsNullOrWhiteSpace(functionArgs) ? "{}" : TryFormatAsJson(functionArgs) ?? functionArgs;
    }

    /// <summary>
    ///     Formats tool response, pretty-printing JSON if detected.
    /// </summary>
    /// <param name="result">The tool result string</param>
    /// <returns>Formatted response string</returns>
    private static string FormatToolResponse(string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return string.Empty;
        }

        // Try to format as JSON if it looks like JSON
        return TryFormatAsJson(result) ?? result;
    }

    /// <summary>
    ///     Attempts to parse and pretty-print a string as JSON.
    /// </summary>
    /// <param name="text">The text to try formatting as JSON</param>
    /// <returns>Pretty-printed JSON if successful, null otherwise</returns>
    private static string? TryFormatAsJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        // Quick check if it looks like JSON
        if (!(text.StartsWith('{') && text.EndsWith('}')) && !(text.StartsWith('[') && text.EndsWith(']')))
        {
            return null;
        }

        try
        {
            // Parse and re-serialize with pretty printing
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            // Not valid JSON, return null to use original text
            return null;
        }
    }

    /// <summary>
    ///     Merges two metadata dictionaries, with the second taking precedence for conflicting keys.
    /// </summary>
    /// <param name="first">The first metadata dictionary</param>
    /// <param name="second">The second metadata dictionary (takes precedence)</param>
    /// <returns>Combined metadata dictionary</returns>
    private static ImmutableDictionary<string, object> MergeMetadata(
        ImmutableDictionary<string, object> first,
        ImmutableDictionary<string, object> second
    )
    {
        var result = first;

        foreach (var kvp in second)
        {
            result = result.SetItem(kvp.Key, kvp.Value);
        }

        return result;
    }
}
