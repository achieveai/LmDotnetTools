using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices; 
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

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
                    if (contract != null && _schemaValidator != null)
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
