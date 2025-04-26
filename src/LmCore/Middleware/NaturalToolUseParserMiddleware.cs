using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices; // Add this line
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
    private readonly JsonSchemaValidator _schemaValidator;
    private readonly IAgent? _fallbackParser;
    private readonly string? _name;
    private bool _isFirstInvocation = true;

    public NaturalToolUseParserMiddleware(
        IEnumerable<FunctionContract> functions,
        JsonSchemaValidator schemaValidator,
        IAgent? fallbackParser = null,
        string? name = null)
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _schemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
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

    private async IAsyncEnumerable<IMessage> ProcessStreamingRepliesAsync(IAsyncEnumerable<IMessage> streamingReplies, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        bool isBufferingToolCall = false;
        string? currentToolName = null;
        string prefixText = "";

        await foreach (var reply in streamingReplies.WithCancellation(cancellationToken))
        {
            if (reply is TextMessage textMessage)
            {
                var text = textMessage.Text;
                buffer.Append(text);

                if (!isBufferingToolCall)
                {
                    var startMatch = Regex.Match(buffer.ToString(), @"<([a-zA-Z0-9_]+)>");
                    if (startMatch.Success)
                    {
                        isBufferingToolCall = true;
                        currentToolName = startMatch.Groups[1].Value;
                        var startIndex = buffer.ToString().IndexOf(startMatch.Value);
                        prefixText = buffer.ToString().Substring(0, startIndex);
                        if (!string.IsNullOrEmpty(prefixText))
                        {
                            yield return new TextMessage { Text = prefixText, Role = textMessage.Role };
                        }
                        buffer.Clear();
                        buffer.Append(buffer.ToString().Substring(startIndex));
                    }
                    else
                    {
                        yield return textMessage;
                        buffer.Clear();
                    }
                }
                else
                {
                    var endTag = $"</{currentToolName}>";
                    if (buffer.ToString().Contains(endTag))
                    {
                        var toolCallText = buffer.ToString();
                        var messages = await ExtractToolCallsAsync(toolCallText, currentToolName!, prefixText, textMessage.FromAgent, cancellationToken);
                        foreach (var msg in messages)
                        {
                            yield return msg;
                        }
                        buffer.Clear();
                        isBufferingToolCall = false;
                        currentToolName = null;
                        prefixText = "";
                    }
                }
            }
            else
            {
                yield return reply;
            }
        }

        if (buffer.Length > 0 && isBufferingToolCall && currentToolName != null)
        {
            var messages = await ExtractToolCallsAsync(buffer.ToString(), currentToolName, prefixText, null, cancellationToken);
            foreach (var msg in messages)
            {
                yield return msg;
            }
        }
        else if (buffer.Length > 0)
        {
            yield return new TextMessage { Text = buffer.ToString(), Role = Role.Assistant };
        }
    }

    private async Task<IEnumerable<IMessage>> ProcessTextMessageAsync(TextMessage textMessage, CancellationToken cancellationToken)
    {
        var text = textMessage.Text;
        var matches = Regex.Matches(text, @"<([a-zA-Z0-9_]+)>[\s\S]*?</\1>");
        if (matches.Count == 0)
        {
            return new[] { textMessage };
        }

        var result = new List<IMessage>();
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                var prefix = text.Substring(lastIndex, match.Index - lastIndex);
                if (!string.IsNullOrEmpty(prefix))
                {
                    result.Add(new TextMessage { Text = prefix, Role = textMessage.Role });
                }
            }

            var toolName = match.Groups[1].Value;
            var toolCallText = match.Value;
            var extractedMessages = await ExtractToolCallsAsync(toolCallText, toolName, "", textMessage.FromAgent, cancellationToken);
            result.AddRange(extractedMessages);

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            var suffix = text.Substring(lastIndex);
            if (!string.IsNullOrEmpty(suffix))
            {
                result.Add(new TextMessage { Text = suffix, Role = textMessage.Role });
            }
        }

        return result;
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
            var jsonMatch = Regex.Match(text, @"```json[\s\S]*?```");
            if (!jsonMatch.Success)
            {
                jsonMatch = Regex.Match(text, @"```[\s\S]*?```");
            }

            if (jsonMatch.Success)
            {
                var jsonText = jsonMatch.Value.Trim('`').Replace("json", "").Trim();
                var contract = _functions.FirstOrDefault(f => f.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (contract != null && _schemaValidator != null)
                {
                    result.Add(new TextMessage { Text = $"Tool Call: {toolName} with args {jsonText}", Role = Role.Assistant });
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
                    return new IMessage[] { new TextMessage { Text = $"Tool Call: {toolName} with args {jsonText}", Role = Role.Assistant } };
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
        var jsonMatch = Regex.Match(text, @"```json[\s\S]*?```");
        if (!jsonMatch.Success)
        {
            jsonMatch = Regex.Match(text, @"```[\s\S]*?```");
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
