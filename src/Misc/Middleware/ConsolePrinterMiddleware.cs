using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Middleware;

/// <summary>
/// Middleware for printing message tokens to the console as they come in
/// </summary>
public class ConsolePrinterHelperMiddleware : IStreamingMiddleware
{
    private readonly ConsolePrinterColors _colors;
    private readonly IToolFormatterFactory _toolFormatterFactory;
    private bool _isFirstMessage = true;
    private bool _isNewMessage = true;
    private ToolsCallMessageBuilder? _toolsCallMessageBuilder = null;

    // Dictionary to track partial tool calls by their ID
    private readonly Dictionary<string, ToolCall> _partialToolCallsById = [];

    // Dictionary to track partial tool calls by their Index when ID is not available
    private readonly Dictionary<int, ToolCall> _partialToolCallsByIndex = [];
    private IMessage? _lastMessage = null;
    private ToolFormatter? _formatter = null;

    /// <summary>
    /// The name of this middleware
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Creates a new instance of ConsolePrinterHelperMiddleware with default colors
    /// </summary>
    /// <param name="name">Optional name for the middleware</param>
    public ConsolePrinterHelperMiddleware(string? name = null)
        : this(new ConsolePrinterColors(), null, name) { }

    /// <summary>
    /// Creates a new instance of ConsolePrinterHelperMiddleware with custom colors
    /// </summary>
    /// <param name="colors">Colors to use for different message types</param>
    /// <param name="name">Optional name for the middleware</param>
    public ConsolePrinterHelperMiddleware(ConsolePrinterColors colors, string? name = null)
        : this(colors, null, name) { }

    /// <summary>
    /// Creates a new instance of ConsolePrinterHelperMiddleware with custom colors and tool formatter factory
    /// </summary>
    /// <param name="colors">Colors to use for different message types</param>
    /// <param name="toolFormatterFactory">Factory for creating tool formatters</param>
    /// <param name="name">Optional name for the middleware</param>
    public ConsolePrinterHelperMiddleware(
        ConsolePrinterColors colors,
        IToolFormatterFactory? toolFormatterFactory,
        string? name = null
    )
    {
        _colors = colors;
        _toolFormatterFactory =
            toolFormatterFactory ?? CreateDefaultToolFormatterFactory();
        Name = name ?? nameof(ConsolePrinterHelperMiddleware);
    }

    /// <summary>
    /// Invokes the middleware to print non-streaming messages
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Call the agent to get responses
        var responses = await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);

        // Print each response
        foreach (var response in responses)
        {
            PrintMessage(response);
        }

        // Print double line to indicate completion
        PrintCompletionLine();

        return responses;
    }

    /// <summary>
    /// Invokes the middleware to print streaming messages
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Call the agent to get streaming responses
        var responses = await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);

        // Reset state for a new streaming session
        _isFirstMessage = true;
        _isNewMessage = true;

        // Transform the stream to print each message as it comes in
        return new PrintingAsyncEnumerable(responses, this);
    }

    /// <summary>
    /// Prints a single message to the console with appropriate coloring
    /// </summary>
    /// <param name="message">The message to print</param>
    private void PrintMessage(IMessage message)
    {
        if (
            message is UsageMessage usageMessage
            && usageMessage.Usage != null
            && usageMessage.Usage.PromptTokens + usageMessage.Usage.CompletionTokens == 0
        )
        {
            return;
        }

        message = CheckMessageContinuity(message);

        // Print different message types with different colors
        switch (message)
        {
            case TextMessage textMessage:
                WriteColoredText(textMessage.Text, _colors.TextMessageColor);
                break;

            case TextUpdateMessage textUpdateMessage:
                WriteColoredText(
                    textUpdateMessage.Text,
                    _colors.TextMessageColor,
                    isLine: false
                );
                break;

            case ReasoningMessage reasoningMessage:
                if (reasoningMessage.Visibility == ReasoningVisibility.Encrypted)
                {
                    WriteColoredText(
                        "Reasoning is encrypted",
                        _colors.ReasoningMessageColor
                    );
                }
                else
                {
                    WriteColoredText(
                        reasoningMessage.Reasoning,
                        _colors.ReasoningMessageColor
                    );
                }
                break;

            case ReasoningUpdateMessage reasoningMessage:
                if (reasoningMessage.Visibility == ReasoningVisibility.Encrypted)
                {
                    WriteColoredText(
                        "Reasoning is encrypted",
                        _colors.ReasoningMessageColor,
                        isLine: false
                    );
                }
                else
                {
                    WriteColoredText(
                        reasoningMessage.Reasoning,
                        _colors.ReasoningMessageColor,
                        isLine: false
                    );
                }
                break;

            case UsageMessage usageMessage1:
                WriteColoredText(
                    $"Prompt tokens: {usageMessage1.Usage.PromptTokens}",
                    _colors.UsageMessageColor
                );
                WriteColoredText(
                    $"Completion tokens: {usageMessage1.Usage.CompletionTokens}",
                    _colors.UsageMessageColor
                );
                WriteColoredText(
                    $"Total tokens: {usageMessage1.Usage.TotalTokens}",
                    _colors.UsageMessageColor
                );
                break;
            case ToolsCallMessage toolsCallMessage:
                PrintToolCalls(toolsCallMessage.ToolCalls);
                break;

            case ToolsCallUpdateMessage toolsCallUpdateMessage:
                foreach (var update in toolsCallUpdateMessage.ToolCallUpdates)
                {
                    ProcessToolCallUpdate(update);
                }
                break;
            default:
                // For unknown message types, just convert to string
                Console.WriteLine(message.ToString());
                break;
        }

        _lastMessage = message;
    }

    private IMessage CheckMessageContinuity(IMessage message)
    {
        // We'll set _isNewMessage to false for most message types except those that
        // explicitly indicate a new logical message is starting
        _isNewMessage = true;

        // Check if last message is same type as current message
        if (message is ToolsCallMessage or TextMessage)
        {
            _toolsCallMessageBuilder = null; // Reset the tool call message builder
        }
        else if (_lastMessage != null && _lastMessage.GetType() == message.GetType())
        {
            _isNewMessage = false;

            if (message is ToolsCallUpdateMessage toolsCallUpdateMessage)
            {
                var completedToolCallCount = _toolsCallMessageBuilder!.CompletedToolCalls.Count;
                _toolsCallMessageBuilder.Add(toolsCallUpdateMessage);
                if (completedToolCallCount != _toolsCallMessageBuilder.CompletedToolCalls.Count)
                {
                    _isNewMessage = true; // New tool call completed
                }

                message = new ToolsCallUpdateMessage
                {
                    FromAgent = toolsCallUpdateMessage.FromAgent,
                    Role = toolsCallUpdateMessage.Role,
                    GenerationId = toolsCallUpdateMessage.GenerationId,
                    ToolCallUpdates = [.. toolsCallUpdateMessage
                        .ToolCallUpdates.Select(tc => new ToolCallUpdate
                        {
                            FunctionName = _toolsCallMessageBuilder.CurrentFunctionName,
                            FunctionArgs = tc.FunctionArgs,
                            ToolCallId = _toolsCallMessageBuilder.CurrentToolCallId,
                            Index = _toolsCallMessageBuilder.CurrentIndex,
                            JsonFragmentUpdates = tc.JsonFragmentUpdates,
                        })],
                };
            }
        }
        else if (message is ToolsCallUpdateMessage toolsCallMessage)
        {
            // If we have a new tool call message, we need to print it
            _toolsCallMessageBuilder = new ToolsCallMessageBuilder
            {
                FromAgent = toolsCallMessage.FromAgent,
                Role = toolsCallMessage.Role,
                GenerationId = toolsCallMessage.GenerationId,
            };

            _toolsCallMessageBuilder.Add(toolsCallMessage);
        }
        else
        {
            _toolsCallMessageBuilder = null; // Reset the tool call message builder
        }

        // Print horizontal line before a new message (if not the first one)
        if (!_isFirstMessage && _isNewMessage)
        {
            if (message is TextUpdateMessage || message is ToolsCallUpdateMessage toolsCallMessage)
            {
                WriteColoredText(string.Empty, new ConsoleColorPair(), true);
            }

            PrintHorizontalLine();
        }

        if (_isNewMessage)
        {
            _formatter = null; // Reset the formatter for new messages
        }

        _isFirstMessage = false;
        return message;
    }

    /// <summary>
    /// Prints a collection of tool calls with formatted parameters
    /// </summary>
    /// <param name="toolCalls">The tool calls to print</param>
    private void PrintToolCalls(IEnumerable<ToolCall> toolCalls)
    {
        foreach (var toolCall in toolCalls)
        {
            PrintToolCall(toolCall);
        }
    }

    /// <summary>
    /// Prints a single tool call with formatted parameters
    /// </summary>
    /// <param name="toolCall">The tool call to print</param>
    private void PrintToolCall(ToolCall toolCall)
    {
        // Write the tool call header
        var idDisplay = toolCall.ToolCallId != null ? $" (ID: {toolCall.ToolCallId})" : "";
        var indexDisplay = toolCall.Index.HasValue ? $" [#{toolCall.Index.Value}]" : "";
        WriteColoredText(
            $"Tool call{indexDisplay}{idDisplay}:",
            _colors.ToolUseMessageColor
        );

        // Get a formatter for this tool call and use it to format the parameters
        _formatter ??= _toolFormatterFactory.GetFormatter(toolCall.FunctionName ?? "unknown");

        // First call with empty updates just to print the function name
        var headerParts = _formatter(toolCall.FunctionName ?? "unknown", []);
        foreach (var (color, text) in headerParts)
        {
            WriteColoredText(text, color, isLine: false);
        }

        if (string.IsNullOrEmpty(toolCall.FunctionArgs))
        {
            return; // No function args to print
        }

        // Generate fragment updates from the function args and format them
        var fragmentUpdates = CreateFragmentUpdatesFromRawJson(
            toolCall.FunctionName ?? "unknown",
            toolCall.FunctionArgs ?? ""
        );
        var formattedParts = _formatter(toolCall.FunctionName ?? "unknown", fragmentUpdates);

        foreach (var (color, text) in formattedParts)
        {
            WriteColoredText(text, color, isLine: false);
        }
    }

    /// <summary>
    /// Prints a horizontal line separator
    /// </summary>
    private void PrintHorizontalLine()
    {
        WriteColoredText(
            new string('-', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80),
            _colors.HorizontalLineColor
        );
    }

    /// <summary>
    /// Prints a double line to indicate completion of a response
    /// </summary>
    private void PrintCompletionLine()
    {
        WriteColoredText(
            new string('=', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80),
            _colors.CompletionLineColor
        );
    }

    /// <summary>
    /// Writes text to the console with the specified color
    /// </summary>
    /// <param name="text">The text to write</param>
    /// <param name="color">The color pair to use</param>
    /// <param name="isLine">Whether to write as a line (with newline) or inline</param>
    private static void WriteColoredText(string text, ConsoleColorPair color, bool isLine = true)
    {
        var oldForeground = Console.ForegroundColor;
        var oldBackground = Console.BackgroundColor;

        try
        {
            // Only change colors that are explicitly specified
            if (color.Foreground.HasValue)
            {
                Console.ForegroundColor = color.Foreground.Value;
            }

            if (color.Background.HasValue)
            {
                Console.BackgroundColor = color.Background.Value;
            }

            if (isLine)
            {
                Console.WriteLine(text);
            }
            else
            {
                Console.Write(text);
            }
        }
        finally
        {
            // Restore console colors
            Console.ForegroundColor = oldForeground;
            Console.BackgroundColor = oldBackground;
        }
    }

    private static void Flush()
    {
        Console.Out.Flush();
    }

    /// <summary>
    /// Creates the default tool formatter factory
    /// </summary>
    private static IToolFormatterFactory CreateDefaultToolFormatterFactory()
    {
        return new DefaultToolFormatterFactory(new ConsoleColorPair { Foreground = ConsoleColor.Blue });
    }

    /// <summary>
    /// AsyncEnumerable wrapper that prints each message as it is enumerated
    /// </summary>
    private class PrintingAsyncEnumerable : IAsyncEnumerable<IMessage>
    {
        private readonly IAsyncEnumerable<IMessage> _sourceStream;
        private readonly ConsolePrinterHelperMiddleware _printer;

        public PrintingAsyncEnumerable(IAsyncEnumerable<IMessage> sourceStream, ConsolePrinterHelperMiddleware printer)
        {
            _sourceStream = sourceStream;
            _printer = printer;
        }

        public async IAsyncEnumerator<IMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            await using var enumerator = _sourceStream.GetAsyncEnumerator(cancellationToken);

            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    var message = enumerator.Current;

                    // Print the message
                    _printer.PrintMessage(message);

                    // Yield the message for downstream middleware
                    yield return message;
                }

                // Print completion line when the stream ends
                _printer.PrintCompletionLine();
            }
            finally
            {
                // Make sure we dispose the source enumerator
                await enumerator.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Processes a single tool call update and tracks its state
    /// </summary>
    /// <param name="update">The tool call update to process</param>
    private void ProcessToolCallUpdate(ToolCallUpdate update)
    {
        // Generate a key for tracking this tool call
        var toolCallKey = update.ToolCallId;
        var indexKey = update.Index;

        if (_isNewMessage)
        {
            // Create a new tool call object
            var newToolCall = new ToolCall
            {
                FunctionName = update.FunctionName ?? "unknown",
                FunctionArgs = update.FunctionArgs ?? "{}",
                ToolCallId = update.ToolCallId,
                Index = update.Index ?? indexKey,
            };

            // Store it for future updates
            if (!string.IsNullOrEmpty(newToolCall.ToolCallId))
            {
                _partialToolCallsById[newToolCall.ToolCallId] = newToolCall;
            }

            if (newToolCall.Index.HasValue)
            {
                _partialToolCallsByIndex[newToolCall.Index.Value] = newToolCall;
            }

            // Print the new tool call
            PrintToolCall(newToolCall);
        }
        else
        {
            // Get formatter for this tool call
            _formatter ??= _toolFormatterFactory.GetFormatter(update.FunctionName ?? "unknown");

            // Check if JsonFragmentUpdates is available (populated by JsonFragmentUpdateMiddleware)
            if (update.JsonFragmentUpdates != null && update.JsonFragmentUpdates.Any())
            {
                // Use the structured fragment updates directly
                var formattedParts = _formatter(update.FunctionName ?? "unknown", update.JsonFragmentUpdates);

                foreach (var (color, text) in formattedParts)
                {
                    WriteColoredText(text, color, isLine: false);
                }
            }
            else
            {
                // Fallback: create fragment updates from raw FunctionArgs
                // This ensures backward compatibility when JsonFragmentUpdateMiddleware is not in the chain
                var fragmentUpdates = CreateFragmentUpdatesFromRawJson(
                    update.FunctionName ?? "unknown",
                    update.FunctionArgs ?? ""
                );
                var formattedParts = _formatter(update.FunctionName ?? "unknown", fragmentUpdates);

                foreach (var (color, text) in formattedParts)
                {
                    WriteColoredText(text, color, isLine: false);
                }
            }

            Flush();
        }
    }

    /// <summary>
    /// Creates JsonFragmentUpdates from raw JSON string for backward compatibility
    /// </summary>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="jsonString">Raw JSON string</param>
    /// <returns>JsonFragmentUpdates generated from the JSON</returns>
    private static IEnumerable<JsonFragmentUpdate> CreateFragmentUpdatesFromRawJson(string toolName, string jsonString)
    {
        if (string.IsNullOrEmpty(jsonString))
        {
            return [];
        }

        // Create a temporary generator for backward compatibility
        var generator = new JsonFragmentToStructuredUpdateGenerator(toolName);
        return generator.AddFragment(jsonString);
    }
}
