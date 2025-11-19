using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Agents;

/// <summary>
/// Agent that demonstrates tool calling capabilities
/// Analyzes user messages and calls appropriate tools (weather, calculator, search, etc.)
/// </summary>
public class ToolCallingAgent : IStreamingAgent
{
    private readonly ILogger<ToolCallingAgent> _logger;
    private readonly IEnumerable<IFunctionProvider> _tools;

    public ToolCallingAgent(
        ILogger<ToolCallingAgent> logger,
        IEnumerable<IFunctionProvider> tools)
    {
        _logger = logger;
        _tools = tools;

        var toolNames = string.Join(", ", _tools.SelectMany(t => t.GetFunctions()).Select(f => f.Contract.Name));
        _logger.LogInformation("ToolCallingAgent initialized with tools: {Tools}", toolNames);
    }

    public string Name => "ToolCallingAgent";

    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString();
        _logger.LogInformation("ToolCallingAgent starting non-streaming execution for session {SessionId}", sessionId);

        var messagesList = new List<IMessage>();
        await foreach (var msg in StreamResponseAsync(messages, sessionId, cancellationToken))
        {
            messagesList.Add(msg);
        }

        _logger.LogInformation("ToolCallingAgent completed execution for session {SessionId}", sessionId);
        return messagesList;
    }

    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString();
        _logger.LogInformation("ToolCallingAgent starting streaming execution for session {SessionId}", sessionId);

        return await Task.FromResult(StreamResponseAsync(messages, sessionId, cancellationToken));
    }

    private async IAsyncEnumerable<IMessage> StreamResponseAsync(
        IEnumerable<IMessage> messages,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastMessage = messages.LastOrDefault();
        if (lastMessage is not TextMessage textMessage)
        {
            _logger.LogWarning("ToolCallingAgent requires text message input");
            yield return new TextMessage
            {
                Text = "I can only process text messages.",
                FromAgent = Name,
                Role = Role.Assistant,
                GenerationId = Guid.NewGuid().ToString()
            };
            yield break;
        }

        var messageText = textMessage.Text.ToLower();
        _logger.LogDebug("ToolCallingAgent analyzing message: {Message}", textMessage.Text);

        // Simulate thinking
        await Task.Delay(300, cancellationToken);

        var generationId = Guid.NewGuid().ToString();

        // Determine which tool to call based on message content
        var (toolToCall, arguments) = DetermineToolCall(messageText);

        if (toolToCall == null)
        {
            _logger.LogDebug("No tool call needed, providing direct response");
            yield return new TextMessage
            {
                Text = "I can help you with weather information, calculations, searches, time, or counter operations. What would you like to know?",
                FromAgent = Name,
                Role = Role.Assistant,
                GenerationId = generationId
            };
            yield break;
        }

        _logger.LogInformation("ToolCallingAgent selected tool: {ToolName} with args: {Args}", toolToCall, arguments);

        // Yield thinking message
        yield return new TextMessage
        {
            Text = $"Let me {GetToolAction(toolToCall)} for you...",
            FromAgent = Name,
            Role = Role.Assistant,
            GenerationId = generationId,
            IsThinking = true
        };

        await Task.Delay(200, cancellationToken);

        // Yield tool call
        yield return new ToolsCallMessage
        {
            ToolCalls = ImmutableList.Create(new ToolCall
            {
                FunctionName = toolToCall,
                FunctionArgs = arguments,
                ToolCallId = Guid.NewGuid().ToString(),
                Index = 0
            }),
            FromAgent = Name,
            Role = Role.Assistant,
            GenerationId = generationId
        };

        _logger.LogInformation("ToolCallingAgent completed for session {SessionId}", sessionId);
    }

    private (string? toolName, string arguments) DetermineToolCall(string messageText)
    {
        // Weather queries
        if (messageText.Contains("weather") || messageText.Contains("temperature") || messageText.Contains("forecast"))
        {
            var city = ExtractCity(messageText) ?? "San Francisco";
            var args = JsonSerializer.Serialize(new { city, units = "celsius" });
            return ("get_weather", args);
        }

        // Calculator queries
        if (messageText.Contains("calculate") || messageText.Contains("add") || messageText.Contains("multiply") ||
            messageText.Contains("subtract") || messageText.Contains("divide") ||
            System.Text.RegularExpressions.Regex.IsMatch(messageText, @"\d+\s*[\+\-\*\/]\s*\d+"))
        {
            var (operation, a, b) = ExtractMathOperation(messageText);
            var args = JsonSerializer.Serialize(new { operation, a, b });
            return ("calculate", args);
        }

        // Search queries
        if (messageText.Contains("search") || messageText.Contains("find") || messageText.Contains("look up"))
        {
            var query = ExtractSearchQuery(messageText);
            var args = JsonSerializer.Serialize(new { query, max_results = 3 });
            return ("search", args);
        }

        // Time queries
        if (messageText.Contains("time") || messageText.Contains("date") || messageText.Contains("clock"))
        {
            var args = JsonSerializer.Serialize(new { timezone = "UTC", format = "friendly" });
            return ("get_current_time", args);
        }

        // Counter queries
        if (messageText.Contains("counter") || messageText.Contains("count") || messageText.Contains("increment") || messageText.Contains("decrement"))
        {
            var operation = messageText.Contains("increment") ? "increment" :
                           messageText.Contains("decrement") ? "decrement" :
                           messageText.Contains("reset") ? "reset" : "get";
            var args = JsonSerializer.Serialize(new { operation, name = "default", amount = 1 });
            return ("counter", args);
        }

        return (null, "{}");
    }

    private string? ExtractCity(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cityIndex = Array.FindIndex(words, w => w.Contains("in") || w.Contains("for"));
        if (cityIndex >= 0 && cityIndex < words.Length - 1)
        {
            return words[cityIndex + 1].Trim('?', '.', ',');
        }
        return null;
    }

    private (string operation, double a, double b) ExtractMathOperation(string text)
    {
        // Simple extraction - in real implementation, use proper parsing
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*([\+\-\*\/])\s*(\d+(?:\.\d+)?)");
        if (match.Success)
        {
            var a = double.Parse(match.Groups[1].Value);
            var b = double.Parse(match.Groups[3].Value);
            var op = match.Groups[2].Value switch
            {
                "+" => "add",
                "-" => "subtract",
                "*" => "multiply",
                "/" => "divide",
                _ => "add"
            };
            return (op, a, b);
        }

        return ("add", 10, 5); // Default
    }

    private string ExtractSearchQuery(string text)
    {
        var searchKeywords = new[] { "search", "find", "look up" };
        foreach (var keyword in searchKeywords)
        {
            var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var query = text.Substring(index + keyword.Length).Trim();
                return string.IsNullOrWhiteSpace(query) ? "AG-UI protocol" : query;
            }
        }
        return "AG-UI protocol";
    }

    private string GetToolAction(string toolName) => toolName switch
    {
        "get_weather" => "check the weather",
        "calculate" => "perform that calculation",
        "search" => "search for information",
        "get_current_time" => "get the current time",
        "counter" => "manage the counter",
        _ => "help"
    };
}
