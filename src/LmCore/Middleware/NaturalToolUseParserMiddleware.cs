using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

// Data structures for parsed chunks
public abstract class ParsedChunk { }

public class TextChunk : ParsedChunk
{
    public string Text { get; set; } = string.Empty;

    public TextChunk() { }

    public TextChunk(string text) => Text = text;
}

public class ToolCallChunk : ParsedChunk
{
    public string ToolName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string RawMatch { get; set; } = string.Empty;

    public ToolCallChunk() { }

    public ToolCallChunk(string toolName, string content, string rawMatch)
    {
        ToolName = toolName;
        Content = content;
        RawMatch = rawMatch;
    }
}

public class PartialToolCallMatch
{
    public int StartIndex { get; set; } = -1;
    public string PartialPattern { get; set; } = string.Empty;
    public bool IsMatch => StartIndex >= 0;

    public PartialToolCallMatch() { }

    public PartialToolCallMatch(int startIndex, string partialPattern)
    {
        StartIndex = startIndex;
        PartialPattern = partialPattern;
    }

    public static PartialToolCallMatch NoMatch => new();
}

public class SafeTextResult
{
    public string SafeText { get; set; } = string.Empty;
    public string RemainingBuffer { get; set; } = string.Empty;

    public SafeTextResult() { }

    public SafeTextResult(string safeText, string remainingBuffer)
    {
        SafeText = safeText;
        RemainingBuffer = remainingBuffer;
    }
}

// Component 1: Text parser for complete tool calls
public partial class ToolCallTextParser
{
    // Simplified regex - just extract tool name and content, don't worry about content format
    private static readonly Regex ToolCallPattern = MyRegex();

    public static List<ParsedChunk> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var chunks = new List<ParsedChunk>();
        var matches = ToolCallPattern.Matches(text);

        if (matches.Count == 0)
        {
            chunks.Add(new TextChunk(text));
            return chunks;
        }

        var currentIndex = 0;
        foreach (Match match in matches)
        {
            // Add text before this tool call
            if (match.Index > currentIndex)
            {
                var prefixText = text.Substring(currentIndex, match.Index - currentIndex);
                if (!string.IsNullOrEmpty(prefixText))
                {
                    chunks.Add(new TextChunk(prefixText));
                }
            }

            // Add the tool call chunk
            var toolName = match.Groups[1].Value;
            var content = match.Groups[2].Value.Trim();
            chunks.Add(new ToolCallChunk(toolName, content, match.Value));

            currentIndex = match.Index + match.Length;
        }

        // Add remaining text after last tool call
        if (currentIndex < text.Length)
        {
            var suffixText = text.Substring(currentIndex);
            if (!string.IsNullOrEmpty(suffixText))
            {
                chunks.Add(new TextChunk(suffixText));
            }
        }

        return chunks;
    }

    [GeneratedRegex(
        @"<tool_call\s+name\s*=\s*[""']([^""']+)[""']\s*>(.*?)</tool_call>",
        RegexOptions.Compiled | RegexOptions.Singleline
    )]
    private static partial Regex MyRegex();
}

// Component 2: Detector for partial tool call patterns
public partial class PartialToolCallDetector
{
    // Check if text contains any opening tool_call tags without matching closing tags
    private static readonly Regex OpeningTagPattern = MyRegex();
    private static readonly Regex ClosingTagPattern = MyRegex1();

    // Patterns for detecting incomplete tags at the end
    private static readonly Regex[] PartialPatterns =
    [
        // 1. Incomplete opening tag patterns: <, <t, <tool_call, <tool_call name="test
        MyRegex2(),
        // 2. Incomplete closing tag: ...content</tool_call but missing final >
        MyRegex3(),
        MyRegex4(),
        MyRegex5(),
        MyRegex6(),
        MyRegex7(),
        MyRegex8(),
        MyRegex9(),
        MyRegex10(),
        MyRegex11(),
        MyRegex12(),
    ];

    public static PartialToolCallMatch DetectPartialStart(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return PartialToolCallMatch.NoMatch;
        }

        // First check for unmatched opening tags anywhere in the text
        var openMatches = OpeningTagPattern.Matches(text);
        var closeMatches = ClosingTagPattern.Matches(text);

        if (openMatches.Count > closeMatches.Count)
        {
            // We have more opening tags than closing tags - find the first unmatched opening tag
            var matchedCloses = new HashSet<int>();

            // Mark all properly closed tool calls
            foreach (Match closeMatch in closeMatches)
            {
                // Find the last opening tag before this closing tag
                Match? correspondingOpen = null;
                foreach (Match openMatch in openMatches)
                {
                    if (openMatch.Index < closeMatch.Index && !matchedCloses.Contains(openMatch.Index))
                    {
                        correspondingOpen = openMatch;
                    }
                }
                if (correspondingOpen != null)
                {
                    _ = matchedCloses.Add(correspondingOpen.Index);
                }
            }

            // Find the first unmatched opening tag
            foreach (Match openMatch in openMatches)
            {
                if (!matchedCloses.Contains(openMatch.Index))
                {
                    return new PartialToolCallMatch(openMatch.Index, text.Substring(openMatch.Index));
                }
            }
        }

        // Then check for incomplete tags at the end
        foreach (var pattern in PartialPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                var startIndex = match.Index;
                var partialPattern = text.Substring(startIndex);
                return new PartialToolCallMatch(startIndex, partialPattern);
            }
        }

        return PartialToolCallMatch.NoMatch;
    }

    [GeneratedRegex(@"<tool_call\s+[^>]*>", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"</tool_call>", RegexOptions.Compiled)]
    private static partial Regex MyRegex1();

    [GeneratedRegex(@"<(?:t(?:o(?:o(?:l(?:_(?:c(?:a(?:l(?:l(?:\s+[^>]*)?)?)?)?)?)?)?)?)?)?$", RegexOptions.Compiled)]
    private static partial Regex MyRegex2();

    [GeneratedRegex(@"</tool_call$", RegexOptions.Compiled)]
    private static partial Regex MyRegex3();

    [GeneratedRegex(@"</tool_cal$", RegexOptions.Compiled)]
    private static partial Regex MyRegex4();

    [GeneratedRegex(@"</tool_ca$", RegexOptions.Compiled)]
    private static partial Regex MyRegex5();

    [GeneratedRegex(@"</tool_c$", RegexOptions.Compiled)]
    private static partial Regex MyRegex6();

    [GeneratedRegex(@"</tool_$", RegexOptions.Compiled)]
    private static partial Regex MyRegex7();

    [GeneratedRegex(@"</tool$", RegexOptions.Compiled)]
    private static partial Regex MyRegex8();

    [GeneratedRegex(@"</too$", RegexOptions.Compiled)]
    private static partial Regex MyRegex9();

    [GeneratedRegex(@"</to$", RegexOptions.Compiled)]
    private static partial Regex MyRegex10();

    [GeneratedRegex(@"</t$", RegexOptions.Compiled)]
    private static partial Regex MyRegex11();

    [GeneratedRegex(@"</$", RegexOptions.Compiled)]
    private static partial Regex MyRegex12();
}

// Component 3: Safe text extractor
public class SafeTextExtractor
{
    private readonly PartialToolCallDetector _detector;

    public SafeTextExtractor(PartialToolCallDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public static SafeTextResult ExtractSafeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SafeTextResult(string.Empty, string.Empty);
        }

        var partialMatch = PartialToolCallDetector.DetectPartialStart(text);

        if (!partialMatch.IsMatch)
        {
            return new SafeTextResult(text, string.Empty);
        }

        var safeText = text.Substring(0, partialMatch.StartIndex);
        var remainingBuffer = text.Substring(partialMatch.StartIndex);

        return new SafeTextResult(safeText, remainingBuffer);
    }
}

/// <summary>
/// Middleware for parsing natural tool use calls from LLM responses.
/// Detects inline tool calls within fenced blocks and splits them into text and tool call messages.
/// </summary>
public partial class NaturalToolUseParserMiddleware : IStreamingMiddleware
{
    // Shared regex patterns to eliminate duplication
    private static readonly Regex JsonCodeBlockPattern = MyRegex();
    private static readonly Regex ToolCallPattern = MyRegex1();

    private readonly IEnumerable<FunctionContract> _functions;
    private readonly IJsonSchemaValidator? _schemaValidator;
    private readonly IAgent? _fallbackParser;
    private bool _isFirstInvocation = true;

    // New parsing components
    private readonly ToolCallTextParser _textParser;
    private readonly PartialToolCallDetector _partialDetector;
    private readonly SafeTextExtractor _safeTextExtractor;

    public NaturalToolUseParserMiddleware(
        IEnumerable<FunctionContract> functions,
        IJsonSchemaValidator? schemaValidator = null,
        IAgent? fallbackParser = null,
        string? name = null
    )
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _schemaValidator = schemaValidator;
        _fallbackParser = fallbackParser;
        Name = name ?? nameof(NaturalToolUseParserMiddleware);

        // Initialize parsing components
        _textParser = new ToolCallTextParser();
        _partialDetector = new PartialToolCallDetector();
        _safeTextExtractor = new SafeTextExtractor(_partialDetector);
    }

    public string? Name { get; }

    /// <summary>
    /// Extracts JSON content from code blocks, handling both labeled and unlabeled blocks.
    /// </summary>
    private static string? TryExtractJsonFromContent(string content)
    {
        // First try fenced JSON blocks (existing behavior)
        var match = JsonCodeBlockPattern.Match(content);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // Then try unfenced JSON (new behavior)
        return TryExtractUnfencedJson(content);
    }

    /// <summary>
    /// Attempts to extract JSON content that is not wrapped in fenced code blocks.
    /// Validates that the content is valid JSON before returning it.
    /// </summary>
    private static string? TryExtractUnfencedJson(string content)
    {
        var trimmed = content.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // Check if content looks like JSON (starts with { or [)
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try
            {
                // Validate it's parseable JSON
                _ = JsonDocument.Parse(trimmed);
                return trimmed;
            }
            catch (JsonException)
            {
                // Not valid JSON, return null
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Unified method for processing tool calls with validation and fallback logic.
    /// </summary>
    private async Task<IEnumerable<IMessage>> ProcessToolCallAsync(
        string toolName,
        string content,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var jsonText = TryExtractJsonFromContent(content);
            if (jsonText != null)
            {
                var contract = _functions.FirstOrDefault(f =>
                    f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)
                );
                if (contract != null && contract.Parameters != null && _schemaValidator != null)
                {
                    var jsonSchema = contract.GetJsonSchema();
                    var schemaString =
                        jsonSchema != null
                            ? JsonSerializer.Serialize(jsonSchema, JsonSchemaValidator.SchemaSerializationOptions)
                            : string.Empty;

                    var isValid = _schemaValidator.Validate(jsonText, schemaString);
                    if (isValid)
                    {
                        var toolCall = new ToolCall
                        {
                            FunctionName = toolName,
                            FunctionArgs = jsonText,
                            ToolCallId = Guid.NewGuid().ToString(),
                        };
                        return
                        [
                            new ToolsCallMessage
                            {
                                ToolCalls = [toolCall],
                                Role = Role.Assistant,
                            },
                        ];
                    }

                    Console.WriteLine($"[DEBUG] Validation result: {isValid}");

                    // Requirement 2.5 & 3.1: When no fallback agent is provided AND validation fails, throw exception immediately
                    if (_fallbackParser == null)
                    {
                        throw new ToolUseParsingException($"Invalid schema for tool call {toolName}");
                    }

                    // Requirement 3.2: When fallback agent is provided, use structured output fallback for validation failures
                    return await UseFallbackParserAsync(content, toolName, cancellationToken);
                }
                else
                {
                    // Requirement 3.1: Maintain existing error handling behavior when no fallback agent
                    return _fallbackParser == null
                        ? throw new ToolUseParsingException(
                            $"Tool {toolName} not found or no schema validator provided"
                        )
                        : await UseFallbackParserAsync(content, toolName, cancellationToken);
                }
            }
            else
            {
                // Requirement 3.1: Maintain existing error handling behavior when no fallback agent
                return _fallbackParser == null
                    ? throw new ToolUseParsingException($"No JSON content found for tool call {toolName}")
                    : await UseFallbackParserAsync(content, toolName, cancellationToken);
            }
        }
        catch (ToolUseParsingException)
        {
            // Re-throw ToolUseParsingException without modification to maintain existing patterns
            throw;
        }
        catch (Exception ex)
        {
            // Requirement 3.1: When no fallback agent, throw ToolUseParsingException immediately
            if (_fallbackParser == null)
            {
                throw new ToolUseParsingException($"Failed to parse tool call {toolName}: {ex.Message}", ex);
            }

            // Requirement 3.2: When fallback agent is available, try fallback for any other exceptions
            return await UseFallbackParserAsync(content, toolName, cancellationToken);
        }
    }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        var modifiedContext = PrepareContext(context);
        var replies = await agent.GenerateReplyAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        return await ProcessRepliesAsync(replies, cancellationToken);
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        var modifiedContext = PrepareContext(context);
        var streamingReplies = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        return ProcessStreamingRepliesAsync(streamingReplies, cancellationToken);
    }

    private MiddlewareContext PrepareContext(MiddlewareContext context)
    {
        if (_isFirstInvocation && _functions.Any())
        {
            _isFirstInvocation = false;
            var markdown = RenderContractsToMarkdown(_functions);
            var systemMessage = context.Messages.FirstOrDefault(m => m.Role == Role.System)?.ToString() ?? "";
            systemMessage = systemMessage + "\n\n---\n\n# Tool Calls\n\n" + markdown;
            var newMessages = context.Messages.ToList();
            var systemMsgIndex = newMessages.FindIndex(m => m.Role == Role.System);

            if (systemMsgIndex >= 0)
            {
                newMessages[systemMsgIndex] = new TextMessage { Text = systemMessage, Role = Role.System };
            }
            else
            {
                newMessages.Insert(0, new TextMessage { Text = systemMessage, Role = Role.System });
            }

            if (context.Options?.Functions != null && context.Options.Functions.Length != 0)
            {
                context = context with { Options = context.Options with { Functions = null } };
            }

            return new MiddlewareContext(newMessages, context.Options);
        }

        return context;
    }

    private static string RenderContractsToMarkdown(IEnumerable<FunctionContract> functions)
    {
        var sb = new StringBuilder();
        foreach (var func in functions)
        {
            _ = sb.AppendLine(func.ToMarkdown());
        }
        return sb.ToString();
    }

    private async Task<IEnumerable<IMessage>> ProcessRepliesAsync(
        IEnumerable<IMessage> replies,
        CancellationToken cancellationToken
    )
    {
        var processedReplies = new List<IMessage>();
        foreach (var reply in replies)
        {
            if (reply is TextMessage textMessage)
            {
                var processedMessages = await ProcessTextMessageAsync(textMessage, cancellationToken);
                processedReplies.AddRange(processedMessages);
            }
            else
            {
                processedReplies.Add(reply);
            }
        }

        return processedReplies;
    }

    private IAsyncEnumerable<IMessage> ProcessStreamingRepliesAsync(
        IAsyncEnumerable<IMessage> streamingReplies,
        CancellationToken cancellationToken
    )
    {
        return ProcessStreamingRepliesInternalAsync(streamingReplies, cancellationToken);
    }

    private async IAsyncEnumerable<IMessage> ProcessStreamingRepliesInternalAsync(
        IAsyncEnumerable<IMessage> streamingReplies,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        // State for buffering TextUpdateMessages
        var textBuffer = new StringBuilder();
        var bufferedUpdates = new List<TextUpdateMessage>();
        TextUpdateMessage? templateUpdate = null;

        await foreach (var reply in streamingReplies.WithCancellation(cancellationToken))
        {
            // Handle TextUpdateMessage with streaming logic
            if (reply is TextUpdateMessage textUpdate)
            {
                // Store template for creating new messages
                templateUpdate ??= textUpdate;

                // Add to buffer
                _ = textBuffer.Append(textUpdate.Text);
                bufferedUpdates.Add(textUpdate);

                // Check if remaining buffer contains complete tool calls
                if (textBuffer.Length > 0)
                {
                    var parsedChunks = ToolCallTextParser.Parse(textBuffer.ToString());
                    var hasToolCalls = parsedChunks.Any(chunk => chunk is ToolCallChunk);

                    if (hasToolCalls)
                    {
                        // Process complete tool calls and emit results
                        await foreach (
                            var message in ProcessParsedChunksAsync(parsedChunks, templateUpdate, cancellationToken)
                        )
                        {
                            yield return message;
                        }

                        // Clear buffer after processing
                        _ = textBuffer.Clear();
                        bufferedUpdates.Clear();
                    }
                }

                // Try to extract safe text that can be emitted immediately
                var safeTextResult = SafeTextExtractor.ExtractSafeText(textBuffer.ToString());

                if (!string.IsNullOrEmpty(safeTextResult.SafeText))
                {
                    var safeTextLen = safeTextResult.SafeText.Length;
                    var returned = 0;
                    foreach (var item in bufferedUpdates)
                    {
                        if (safeTextLen < item.Text.Length)
                        {
                            break;
                        }

                        returned++;
                        safeTextLen -= item.Text.Length;
                        yield return item;
                    }

                    if (returned > 0)
                    {
                        _ = textBuffer.Clear();
                        bufferedUpdates = [.. bufferedUpdates.Skip(returned)];
                        bufferedUpdates.ForEach(b => textBuffer.Append(b.Text));
                    }
                }
            }
            // Handle complete TextMessage (legacy support)
            else
            {
                foreach (var message in bufferedUpdates)
                {
                    yield return message;
                }

                _ = textBuffer.Clear();
                bufferedUpdates.Clear();

                if (reply is TextMessage textMsg)
                {
                    // Flush any buffered text first
                    var processed = await ProcessTextMessageAsync(textMsg, cancellationToken);
                    foreach (var processedMsg in processed)
                    {
                        yield return processedMsg;
                    }
                }
                // Pass through other message types unchanged
                else
                {
                    yield return reply;
                }
            }
        }

        // Flush any remaining buffered text at the end
        foreach (var message in bufferedUpdates)
        {
            yield return message;
        }
    }

    private async Task<IEnumerable<IMessage>> ProcessTextMessageAsync(
        TextMessage textMessage,
        CancellationToken cancellationToken
    )
    {
        var text = textMessage.Text;
        var messages = new List<IMessage>();

        // Check if there are tool calls in the format <tool_call name="...">...</tool_call>
        var matches = ToolCallPattern.Matches(text).Cast<Match>().ToList();

        if (matches.Count == 0)
        {
            messages.Add(textMessage);
            return messages;
        }

        var currentPosition = 0;

        foreach (var match in matches)
        {
            var startIndex = match.Index;
            var endIndex = startIndex + match.Length;
            var toolName = match.Groups[1].Value;
            var content = match.Groups[2].Value.Trim();

            if (startIndex > currentPosition)
            {
                var prefixText = text.Substring(currentPosition, startIndex - currentPosition).Trim();
                if (!string.IsNullOrEmpty(prefixText))
                {
                    messages.Add(new TextMessage { Text = prefixText, Role = textMessage.Role });
                }
            }

            try
            {
                var toolCallMessages = await ProcessToolCallAsync(toolName, content, cancellationToken);
                messages.AddRange(toolCallMessages);
            }
            catch (ToolUseParsingException)
            {
                // Re-throw ToolUseParsingException to maintain existing error handling patterns
                // ProcessToolCallAsync already handles fallback logic internally
                throw;
            }
            catch (Exception ex)
            {
                // This should not happen as ProcessToolCallAsync handles all exceptions internally
                // But maintain backward compatibility by wrapping in ToolUseParsingException
                throw new ToolUseParsingException($"Failed to parse tool call {toolName}: {ex.Message}", ex);
            }

            currentPosition = endIndex;
        }

        if (currentPosition < text.Length)
        {
            var suffixText = text.Substring(currentPosition).Trim();
            if (!string.IsNullOrEmpty(suffixText))
            {
                messages.Add(new TextMessage { Text = suffixText, Role = textMessage.Role });
            }
        }

        return messages;
    }

    private async Task<IEnumerable<IMessage>> ExtractToolCallsAsync(
        string text,
        string toolName,
        string prefixText,
        string? fromAgent,
        CancellationToken cancellationToken
    )
    {
        var result = new List<IMessage>();
        if (!string.IsNullOrEmpty(prefixText))
        {
            result.Add(new TextMessage { Text = prefixText, Role = Role.Assistant });
        }

        var toolCallMessages = await ProcessToolCallAsync(toolName, text, cancellationToken);
        result.AddRange(toolCallMessages);

        return result;
    }

    private async Task<IEnumerable<IMessage>> UseFallbackParserAsync(
        string rawText,
        string toolName,
        CancellationToken cancellationToken
    )
    {
        ValidateFallbackParserConfiguration();

        var contract = GetFunctionContract(toolName);
        var jsonSchema = contract?.GetJsonSchema();

        return jsonSchema == null
            ? await UseLegacyFallbackAsync(rawText, toolName, cancellationToken)
            : await UseStructuredOutputFallbackAsync(rawText, toolName, jsonSchema, cancellationToken);
    }

    private void ValidateFallbackParserConfiguration()
    {
        if (_fallbackParser == null)
        {
            throw new ToolUseParsingException("Fallback parser is not configured.");
        }
    }

    private FunctionContract? GetFunctionContract(string toolName)
    {
        var contract = _functions.FirstOrDefault(f => f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        return contract == null
            ? throw new ToolUseParsingException($"Tool {toolName} not found in function contracts")
            : contract;
    }

    private async Task<IEnumerable<IMessage>> UseLegacyFallbackAsync(
        string rawText,
        string toolName,
        CancellationToken cancellationToken
    )
    {
        var prompt = CreateLegacyFallbackPrompt(rawText, toolName);
        var messages = CreatePromptMessages(prompt);

        try
        {
            var fallbackReplies = await _fallbackParser!.GenerateReplyAsync(messages, null, cancellationToken);
            var fallbackReply = ExtractTextMessageFromReplies(fallbackReplies);

            if (fallbackReply != null)
            {
                return [fallbackReply];
            }
        }
        catch (Exception ex) when (ex is not ToolUseParsingException)
        {
            throw new ToolUseParsingException($"Fallback parser failed for {toolName}: {ex.Message}", ex);
        }

        throw new ToolUseParsingException($"Fallback parser failed to generate valid JSON for {toolName}");
    }

    private async Task<IEnumerable<IMessage>> UseStructuredOutputFallbackAsync(
        string rawText,
        string toolName,
        JsonSchemaObject jsonSchema,
        CancellationToken cancellationToken
    )
    {
        var responseFormat = CreateResponseFormat(toolName, jsonSchema);
        var options = new GenerateReplyOptions { ResponseFormat = responseFormat };

        var prompt = CreateStructuredOutputPrompt(rawText, toolName);
        var messages = CreatePromptMessages(prompt);

        try
        {
            var fallbackReplies = await _fallbackParser!.GenerateReplyAsync(messages, options, cancellationToken);
            var fallbackReply = ExtractTextMessageFromReplies(fallbackReplies);

            if (fallbackReply != null)
            {
                var jsonText = fallbackReply.Text.Trim();
                return ValidateAndReturnJsonResponse(jsonText, jsonSchema, toolName);
            }

            // Dont' throw, no one would be able to catch and work with this exception.
            // throw new ToolUseParsingException($"Fallback parser failed to generate response for {toolName}");
            return await FallbackToUnstructuredOutput(rawText, toolName, jsonSchema, messages, cancellationToken);
        }
        catch (ToolUseParsingException)
        {
            return await FallbackToUnstructuredOutput(rawText, toolName, jsonSchema, messages, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Structured output failed for {toolName}: {ex.Message}");
            return await FallbackToUnstructuredOutput(rawText, toolName, jsonSchema, messages, cancellationToken);
        }
    }

    private async Task<IEnumerable<IMessage>> FallbackToUnstructuredOutput(
        string rawText,
        string toolName,
        object jsonSchema,
        List<IMessage> messages,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var fallbackReplies = await _fallbackParser!.GenerateReplyAsync(messages, null, cancellationToken);
            var fallbackReply = ExtractTextMessageFromReplies(fallbackReplies);

            if (fallbackReply != null)
            {
                var jsonText = TryExtractJsonFromContent(fallbackReply.Text) ?? fallbackReply.Text.Trim();
                return ValidateAndReturnJsonResponse(jsonText, jsonSchema, toolName);
            }

            throw new ToolUseParsingException($"Fallback parser failed to generate response for {toolName}");
        }
        catch (ToolUseParsingException)
        {
            throw;
        }
        catch (Exception fallbackEx)
        {
            throw new ToolUseParsingException(
                $"Fallback parser failed for {toolName}: {fallbackEx.Message}",
                fallbackEx
            );
        }
    }

    private IEnumerable<IMessage> ValidateAndReturnJsonResponse(string jsonText, object jsonSchema, string toolName)
    {
        if (_schemaValidator != null)
        {
            var schemaString = JsonSerializer.Serialize(jsonSchema, JsonSchemaValidator.SchemaSerializationOptions);
            var isValid = _schemaValidator.Validate(jsonText, schemaString);

            return isValid
                ? (IEnumerable<IMessage>)

                    [
                        new ToolsCallMessage
                        {
                            GenerationId = Guid.NewGuid().ToString(),
                            Role = Role.Tool,
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    FunctionArgs = jsonText,
                                    FunctionName = toolName,
                                    Index = 0,
                                    ToolCallId = Guid.NewGuid().ToString(),
                                },
                            ],
                        },
                    ]

                : throw new ToolUseParsingException($"Fallback parser returned invalid JSON for {toolName}");
        }

        return ValidateJsonSyntaxAndReturn(jsonText, toolName);
    }

    private static IEnumerable<IMessage> ValidateJsonSyntaxAndReturn(string jsonText, string toolName)
    {
        try
        {
            _ = JsonDocument.Parse(jsonText);
            return [new TextMessage { Text = jsonText, Role = Role.Assistant }];
        }
        catch (JsonException)
        {
            throw new ToolUseParsingException($"Fallback parser returned invalid JSON for {toolName}");
        }
    }

    private static string CreateLegacyFallbackPrompt(string rawText, string toolName)
    {
        return $"Rewrite the following reply as a valid function call JSON for {toolName}. Extract the intent and parameters:\n\n{rawText}";
    }

    private static string CreateStructuredOutputPrompt(string rawText, string toolName)
    {
        return $"Extract and fix the parameters for the {toolName} function call from the following content. Return only valid JSON that matches the expected schema:\n\n{rawText}";
    }

    private static List<IMessage> CreatePromptMessages(string prompt)
    {
        return [new TextMessage { Text = prompt, Role = Role.User }];
    }

    private static ResponseFormat CreateResponseFormat(string toolName, JsonSchemaObject jsonSchema)
    {
        return ResponseFormat.CreateWithSchema(
            schemaName: $"{toolName}_parameters",
            schemaObject: jsonSchema,
            strictValidation: true
        );
    }

    private static TextMessage? ExtractTextMessageFromReplies(IEnumerable<IMessage> replies)
    {
        return replies.FirstOrDefault(r => r is TextMessage) as TextMessage;
    }

    private static TextUpdateMessage CreateTextUpdateMessage(string text, TextUpdateMessage template)
    {
        return new TextUpdateMessage
        {
            Text = text,
            Role = template.Role,
            FromAgent = template.FromAgent,
            Metadata = template.Metadata,
            GenerationId = template.GenerationId,
            IsThinking = template.IsThinking,
        };
    }

    private async IAsyncEnumerable<IMessage> ProcessParsedChunksAsync(
        List<ParsedChunk> parsedChunks,
        TextUpdateMessage template,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var chunk in parsedChunks)
        {
            if (chunk is TextChunk textChunk)
            {
                if (!string.IsNullOrEmpty(textChunk.Text))
                {
                    yield return CreateTextUpdateMessage(textChunk.Text, template);
                }
            }
            else if (chunk is ToolCallChunk toolCallChunk)
            {
                // Process the tool call and create ToolsCallMessage
                var toolCallMessages = await ProcessToolCallChunkAsync(toolCallChunk, template, cancellationToken);
                foreach (var message in toolCallMessages)
                {
                    yield return message;
                }
            }
        }
    }

    private async Task<IEnumerable<IMessage>> ProcessToolCallChunkAsync(
        ToolCallChunk toolCallChunk,
        TextUpdateMessage template,
        CancellationToken cancellationToken
    )
    {
        return await ProcessToolCallAsync(toolCallChunk.ToolName, toolCallChunk.Content, cancellationToken);
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex MyRegex();

    [GeneratedRegex(
        @"<tool_call\s+name\s*=\s*[""']([^""']+)[""']\s*>(.*?)</tool_call>",
        RegexOptions.Compiled | RegexOptions.Singleline
    )]
    private static partial Regex MyRegex1();
}

/// <summary>
/// Exception thrown when parsing of a natural tool use call fails.
/// </summary>
public class ToolUseParsingException : Exception
{
    public ToolUseParsingException(string message)
        : base(message) { }

    public ToolUseParsingException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Wrapper class to convert a list to an async enumerable.
/// </summary>
public class AsyncEnumerableWrapper<T> : IAsyncEnumerable<T>
{
    private readonly IEnumerable<T> _source;

    public AsyncEnumerableWrapper(IEnumerable<T> source)
    {
        _source = source;
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new AsyncEnumeratorWrapper<T>(_source.GetEnumerator());
    }
}

/// <summary>
/// Wrapper class to convert an enumerator to an async enumerator.
/// </summary>
public class AsyncEnumeratorWrapper<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _source;

    public AsyncEnumeratorWrapper(IEnumerator<T> source)
    {
        _source = source;
    }

    public T Current => _source.Current;

    public ValueTask DisposeAsync()
    {
        _source.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> MoveNextAsync()
    {
        return ValueTask.FromResult(_source.MoveNext());
    }
}
