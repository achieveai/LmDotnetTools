using System.Text;
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
    private static readonly Regex ToolCallPattern = new(@"<tool_call\s+name\s*=\s*[""']([a-zA-Z0-9_]+)[""']\s*>(.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled);
    
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
    // Simpler approach: detect incomplete tool calls that should be held back
    // We'll use multiple patterns to handle different scenarios
    private static readonly Regex[] PartialPatterns = {
        // 1. Incomplete opening tag patterns: <, <t, <tool_call, <tool_call name="test
        new Regex(@"<(?:t(?:o(?:o(?:l(?:_(?:c(?:a(?:l(?:l(?:\s+[^>]*)?)?)?)?)?)?)?)?)?)?$", RegexOptions.Compiled),
        
        // 2. Complete opening tag but no closing: <tool_call name="test">...content but no </tool_call>
        new Regex(@"<tool_call\s+[^>]*>(?:(?!</tool_call>).)*$", RegexOptions.Compiled | RegexOptions.Singleline),
        
        // 3. Incomplete closing tag: ...content</tool_call but missing final >
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

        return await ProcessStreamingRepliesAsync(streamingReplies, cancellationToken);
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

    private async Task<IAsyncEnumerable<IMessage>> ProcessStreamingRepliesAsync(IAsyncEnumerable<IMessage> streamingReplies, CancellationToken cancellationToken)
    {
        List<IMessage> accumulatedMessages = new List<IMessage>();
        await foreach (var reply in streamingReplies.WithCancellation(cancellationToken))
        {
            accumulatedMessages.Add(reply);
        }

        List<IMessage> processedMessages = new List<IMessage>();
        foreach (var msg in accumulatedMessages)
        {
            if (msg is TextMessage textMsg)
            {
                var processed = await ProcessTextMessageAsync(textMsg, cancellationToken);
                processedMessages.AddRange(processed);
            }
            else
            {
                processedMessages.Add(msg);
            }
        }

        return new AsyncEnumerableWrapper<IMessage>(processedMessages);
    }

    private async Task<IEnumerable<IMessage>> ProcessTextMessageAsync(TextMessage textMessage, CancellationToken cancellationToken)
    {
        var text = textMessage.Text;
        var messages = new List<IMessage>();

        // Check if there are tool calls in the format <toolName>...
        var toolCallPattern = new Regex(@"<([a-zA-Z0-9_]+)>(.*?)</\1>", RegexOptions.Singleline);
        var matches = toolCallPattern.Matches(text).Cast<Match>().ToList();

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
                // Extract JSON from content, looking for code blocks
                var jsonMatch = Regex.Match(content, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.Singleline);
                if (jsonMatch.Success)
                {
                    var jsonText = jsonMatch.Groups[1].Value.Trim();
                    var contract = _functions.FirstOrDefault(f => f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                    if (contract != null && contract.Parameters != null && _schemaValidator != null)
                    {
                        // Using ToString() as a workaround - ideally should use the schema property directly
                        string schemaString = contract.Parameters.ToString() ?? string.Empty;
                        if (_schemaValidator.Validate(jsonText, schemaString))
                        {
                            messages.Add(new TextMessage { Text = $"Tool Call: {toolName} with args {jsonText}", Role = Role.Assistant });
                        }
                        else if (_fallbackParser != null)
                        {
                            var fallbackMessages = await UseFallbackParserAsync(content, toolName, cancellationToken);
                            messages.AddRange(fallbackMessages);
                        }
                        else
                        {
                            throw new ToolUseParsingException($"Invalid schema for tool call {toolName}");
                        }
                    }
                    else if (_fallbackParser != null)
                    {
                        var fallbackMessages = await UseFallbackParserAsync(content, toolName, cancellationToken);
                        messages.AddRange(fallbackMessages);
                    }
                    else
                    {
                        throw new ToolUseParsingException($"Tool {toolName} not found or no schema validator provided");
                    }
                }
                else if (_fallbackParser != null)
                {
                    var fallbackMessages = await UseFallbackParserAsync(content, toolName, cancellationToken);
                    messages.AddRange(fallbackMessages);
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

        try
        {
            var jsonMatch = Regex.Match(text, @"```json[\s\S]*?```", RegexOptions.Singleline);
            if (!jsonMatch.Success)
            {
                jsonMatch = Regex.Match(text, @"```[\s\S]*?```", RegexOptions.Singleline);
            }

            if (jsonMatch.Success)
            {
                var jsonText = jsonMatch.Value.Trim('`').Replace("json", "").Trim();
                var contract = _functions.FirstOrDefault(f => f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (contract != null && _schemaValidator != null)
                {
                    result.Add(new TextMessage { Text = jsonText, Role = Role.Assistant });
                }
                else if (_fallbackParser != null)
                {
                    var fallbackMessages = await UseFallbackParserAsync(text, toolName, cancellationToken);
                    result.AddRange(fallbackMessages);
                }
                else
                {
                    throw new ToolUseParsingException($"Invalid schema for tool call {toolName}");
                }
            }
            else if (_fallbackParser != null)
            {
                var fallbackMessages = await UseFallbackParserAsync(text, toolName, cancellationToken);
                result.AddRange(fallbackMessages);
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
                var fallbackMessages = await UseFallbackParserAsync(text, toolName, cancellationToken);
                result.AddRange(fallbackMessages);
            }
            else
            {
                throw new ToolUseParsingException($"Failed to parse tool call {toolName}: {ex.Message}", ex);
            }
        }

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
                var jsonText = ExtractJsonFromText(fallbackReply.Text);
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

    private string ExtractJsonFromText(string text)
    {
        var jsonMatch = Regex.Match(text, @"```json[\s\S]*?```", RegexOptions.Singleline);
        if (!jsonMatch.Success)
        {
            jsonMatch = Regex.Match(text, @"```[\s\S]*?```", RegexOptions.Singleline);
        }

        if (jsonMatch.Success)
        {
            return jsonMatch.Value.Trim('`').Replace("json", "").Trim();
        }

        return text;
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
