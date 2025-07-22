using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

// Data structures for parsed chunks
public abstract class ParsedChunk {}

public class TextChunk : ParsedChunk 
{
    public string Text { get; set; } = string.Empty;
    public TextChunk() {}
    public TextChunk(string text) => Text = text;
}

public class ToolCallChunk : ParsedChunk 
{
    public string ToolName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string RawMatch { get; set; } = string.Empty;
    
    public ToolCallChunk() {}
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
    
    public PartialToolCallMatch() {}
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
    
    public SafeTextResult() {}
    public SafeTextResult(string safeText, string remainingBuffer)
    {
        SafeText = safeText;
        RemainingBuffer = remainingBuffer;
    }
}

// Component 1: Text parser for complete tool calls
public class ToolCallTextParser
{
    // Simplified regex - just extract tool name and content, don't worry about content format
    private static readonly Regex ToolCallPattern = new(@"<tool_call\s+name\s*=\s*[""']([^""']+)[""']\s*>(.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled);
    
    public List<ParsedChunk> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<ParsedChunk>();
            
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
                    chunks.Add(new TextChunk(prefixText));
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
                chunks.Add(new TextChunk(suffixText));
        }
        
        return chunks;
    }
}

// Component 2: Detector for partial tool call patterns 
public class PartialToolCallDetector
{
    // Check if text contains any opening tool_call tags without matching closing tags
    private static readonly Regex OpeningTagPattern = new(@"<tool_call\s+[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ClosingTagPattern = new(@"</tool_call>", RegexOptions.Compiled);
    
    // Patterns for detecting incomplete tags at the end
    private static readonly Regex[] PartialPatterns = {
        // 1. Incomplete opening tag patterns: <, <t, <tool_call, <tool_call name="test
        new Regex(@"<(?:t(?:o(?:o(?:l(?:_(?:c(?:a(?:l(?:l(?:\s+[^>]*)?)?)?)?)?)?)?)?)?)?$", RegexOptions.Compiled),
        
        // 2. Incomplete closing tag: ...content</tool_call but missing final >
        new Regex(@"</tool_call$", RegexOptions.Compiled),
        new Regex(@"</tool_cal$", RegexOptions.Compiled),
        new Regex(@"</tool_ca$", RegexOptions.Compiled),
        new Regex(@"</tool_c$", RegexOptions.Compiled),
        new Regex(@"</tool_$", RegexOptions.Compiled),
        new Regex(@"</tool$", RegexOptions.Compiled),
        new Regex(@"</too$", RegexOptions.Compiled),
        new Regex(@"</to$", RegexOptions.Compiled),
        new Regex(@"</t$", RegexOptions.Compiled),
        new Regex(@"</$", RegexOptions.Compiled)
    };
    
    public PartialToolCallMatch DetectPartialStart(string text)
    {
        if (string.IsNullOrEmpty(text))
            return PartialToolCallMatch.NoMatch;
        
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
                    matchedCloses.Add(correspondingOpen.Index);
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
}

// Component 3: Safe text extractor
public class SafeTextExtractor
{
    private readonly PartialToolCallDetector _detector;
    
    public SafeTextExtractor(PartialToolCallDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }
    
    public SafeTextResult ExtractSafeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new SafeTextResult(string.Empty, string.Empty);
            
        var partialMatch = _detector.DetectPartialStart(text);
        
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
public class NaturalToolUseParserMiddleware : IStreamingMiddleware
{
    // Shared regex patterns to eliminate duplication
    private static readonly Regex JsonCodeBlockPattern = new(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ToolCallPattern = new(@"<tool_call\s+name\s*=\s*[""']([^""']+)[""']\s*>(.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IEnumerable<FunctionContract> _functions;
    private readonly IJsonSchemaValidator? _schemaValidator;
    private readonly IAgent? _fallbackParser;
    private readonly string? _name;
    private bool _isFirstInvocation = true;
    
    // New parsing components
    private readonly ToolCallTextParser _textParser;
    private readonly PartialToolCallDetector _partialDetector;
    private readonly SafeTextExtractor _safeTextExtractor;

    public NaturalToolUseParserMiddleware(
        IEnumerable<FunctionContract> functions,
        IJsonSchemaValidator? schemaValidator = null,
        IAgent? fallbackParser = null,
        string? name = null)
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _schemaValidator = schemaValidator;
        _fallbackParser = fallbackParser;
        _name = name ?? nameof(NaturalToolUseParserMiddleware);
        
        // Initialize parsing components
        _textParser = new ToolCallTextParser();
        _partialDetector = new PartialToolCallDetector();
        _safeTextExtractor = new SafeTextExtractor(_partialDetector);
    }

    public string? Name => _name;

    /// <summary>
    /// Extracts JSON content from code blocks, handling both labeled and unlabeled blocks.
    /// </summary>
    private static string? TryExtractJsonFromContent(string content)
    {
        var match = JsonCodeBlockPattern.Match(content);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Unified method for processing tool calls with validation and fallback logic.
    /// </summary>
    private async Task<IEnumerable<IMessage>> ProcessToolCallAsync(
        string toolName, 
        string content, 
        CancellationToken cancellationToken)
    {
        try
        {
            var jsonText = TryExtractJsonFromContent(content);
            if (jsonText != null)
            {
                var contract = _functions.FirstOrDefault(f => f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (contract != null && contract.Parameters != null && _schemaValidator != null)
                {
                    var jsonSchema = contract.GetJsonSchema();
                    string schemaString = jsonSchema != null
                        ? JsonSerializer.Serialize(
                            jsonSchema,
                            JsonSchemaValidator.SchemaSerializationOptions)
                        : string.Empty;

                    var validationResult = _schemaValidator.ValidateDetailed(jsonText, schemaString);
                    if (validationResult.IsValid)
                    {
                        var toolCall = new ToolCall
                        {
                            FunctionName = toolName,
                            FunctionArgs = jsonText,
                            ToolCallId = Guid.NewGuid().ToString()
                        };
                        return [new ToolsCallMessage { ToolCalls = new[] { toolCall }.ToImmutableList(), Role = Role.Assistant }];
                    }

                    Console.WriteLine($"[DEBUG] Validation result: {validationResult.IsValid}");
                    foreach (var error in validationResult.Errors)
                    {
                        Console.WriteLine($"[DEBUG] Validation error: {error}");
                    }

                    if (_fallbackParser != null)
                    {
                        return await UseFallbackParserAsync(content, toolName, cancellationToken);
                    }
                    else
                    {
                        throw new ToolUseParsingException($"Invalid schema for tool call {toolName}");
                    }
                }
                else if (_fallbackParser != null)
                {
                    return await UseFallbackParserAsync(content, toolName, cancellationToken);
                }
                else
                {
                    throw new ToolUseParsingException($"Tool {toolName} not found or no schema validator provided");
                }
            }
            else if (_fallbackParser != null)
            {
                return await UseFallbackParserAsync(content, toolName, cancellationToken);
            }
            else
            {
                throw new ToolUseParsingException($"No JSON content found for tool call {toolName}");
            }
        }
        catch (Exception ex)
        {
            if (_fallbackParser != null)
            {
                return await UseFallbackParserAsync(content, toolName, cancellationToken);
            }
            else
            {
                throw new ToolUseParsingException($"Failed to parse tool call {toolName}: {ex.Message}", ex);
            }
        }
    }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default)
    {
        var modifiedContext = PrepareContext(context);
        var replies = await agent.GenerateReplyAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken);

        return await ProcessRepliesAsync(replies, cancellationToken);
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        var modifiedContext = PrepareContext(context);
        var streamingReplies = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken);

        return ProcessStreamingRepliesAsync(streamingReplies, cancellationToken);
    }

    private MiddlewareContext PrepareContext(MiddlewareContext context)
    {
        if (_isFirstInvocation && _functions.Any())
        {
            _isFirstInvocation = false;
            var markdown = RenderContractsToMarkdown(_functions);
            var systemMessage = markdown + "\n\n" + (context.Messages.FirstOrDefault(m => m.Role == Role.System)?.ToString() ?? "");
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
            return new MiddlewareContext(newMessages, context.Options);
        }
        return context;
    }

    private string RenderContractsToMarkdown(IEnumerable<FunctionContract> functions)
    {
        var sb = new StringBuilder();
        foreach (var func in functions)
        {
            sb.AppendLine(func.ToMarkdown());
        }
        return sb.ToString();
    }

    private async Task<IEnumerable<IMessage>> ProcessRepliesAsync(IEnumerable<IMessage> replies, CancellationToken cancellationToken)
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

    private IAsyncEnumerable<IMessage> ProcessStreamingRepliesAsync(IAsyncEnumerable<IMessage> streamingReplies, CancellationToken cancellationToken)
    {
        return ProcessStreamingRepliesInternalAsync(streamingReplies, cancellationToken);
    }

    private async IAsyncEnumerable<IMessage> ProcessStreamingRepliesInternalAsync(
        IAsyncEnumerable<IMessage> streamingReplies, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
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
                textBuffer.Append(textUpdate.Text);
                bufferedUpdates.Add(textUpdate);

                // Check if remaining buffer contains complete tool calls
                if (textBuffer.Length > 0)
                {
                    var parsedChunks = _textParser.Parse(textBuffer.ToString());
                    var hasToolCalls = parsedChunks.Any(chunk => chunk is ToolCallChunk);


                    if (hasToolCalls)
                    {
                        // Process complete tool calls and emit results
                        await foreach (var message in ProcessParsedChunksAsync(parsedChunks, templateUpdate, cancellationToken))
                        {
                            yield return message;
                        }

                        // Clear buffer after processing
                        textBuffer.Clear();
                        bufferedUpdates.Clear();
                    }
                }

                // Try to extract safe text that can be emitted immediately
                var safeTextResult = _safeTextExtractor.ExtractSafeText(textBuffer.ToString());

                if (!string.IsNullOrEmpty(safeTextResult.SafeText))
                {
                    var safeTextLen = safeTextResult.SafeText.Length;
                    int returned = 0;
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
                        textBuffer.Clear();
                        bufferedUpdates = bufferedUpdates.Skip(returned).ToList();
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

                textBuffer.Clear();
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

    private async Task<IEnumerable<IMessage>> ProcessTextMessageAsync(TextMessage textMessage, CancellationToken cancellationToken)
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

        int currentPosition = 0;

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
            catch (Exception ex)
            {
                if (_fallbackParser != null)
                {
                    var fallbackMessages = await UseFallbackParserAsync(content, toolName, cancellationToken);
                    messages.AddRange(fallbackMessages);
                }
                else
                {
                    throw new ToolUseParsingException($"Failed to parse tool call {toolName}: {ex.Message}", ex);
                }
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

    private async Task<IEnumerable<IMessage>> ExtractToolCallsAsync(string text, string toolName, string prefixText, string? fromAgent, CancellationToken cancellationToken)
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

    private async Task<IEnumerable<IMessage>> UseFallbackParserAsync(string rawText, string toolName, CancellationToken cancellationToken)
    {
        if (_fallbackParser == null)
        {
            throw new InvalidOperationException("Fallback parser is not configured.");
        }

        var prompt = $"Rewrite the following reply as a valid function call JSON for {toolName}. Extract the intent and parameters:\n\n{rawText}";
        var messages = new List<IMessage>
        {
            new TextMessage { Text = prompt, Role = Role.User }
        };
        var fallbackReplies = await _fallbackParser.GenerateReplyAsync(messages, null, cancellationToken);
        var fallbackReply = fallbackReplies.FirstOrDefault(r => r is TextMessage) as TextMessage;

        if (fallbackReply != null)
        {
            try
            {
                var jsonText = TryExtractJsonFromContent(fallbackReply.Text) ?? fallbackReply.Text;
                var contract = _functions.FirstOrDefault(f => f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (contract != null && _schemaValidator != null)
                {
                    return new IMessage[] { new TextMessage { Text = jsonText, Role = Role.Assistant } };
                }
            }
            catch
            {
                // Fallback failed, throw original exception
            }
        }

        throw new ToolUseParsingException($"Fallback parser failed to generate valid JSON for {toolName}");
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
            IsThinking = template.IsThinking
        };
    }

    private async IAsyncEnumerable<IMessage> ProcessParsedChunksAsync(
        List<ParsedChunk> parsedChunks, 
        TextUpdateMessage template, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
    {
        return await ProcessToolCallAsync(
            toolCallChunk.ToolName,
            toolCallChunk.Content,
            cancellationToken);
    }
}

/// <summary>
/// Exception thrown when parsing of a natural tool use call fails.
/// </summary>
public class ToolUseParsingException : Exception
{
    public ToolUseParsingException(string message) : base(message) { }
    public ToolUseParsingException(string message, Exception innerException) : base(message, innerException) { }
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
