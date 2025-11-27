using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.Examples;

/// <summary>
/// Example demonstrating the simplified message flow with explicit tool execution control.
/// This example shows:
/// 1. MessageTransformationMiddleware for bidirectional message transformation
/// 2. ToolCallInjectionMiddleware for injecting tool definitions
/// 3. ToolCallExecutor for explicit tool execution
/// 4. Agentic loop with manual control
/// </summary>
public class SimplifiedMessageFlowExample
{
    private readonly ILogger<SimplifiedMessageFlowExample> _logger;

    public SimplifiedMessageFlowExample(ILogger<SimplifiedMessageFlowExample> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string apiKey)
    {
        _logger.LogInformation("Starting Simplified Message Flow Example");

        // ===== Step 1: Define Tools =====

        var functions = new[]
        {
            new FunctionContract
            {
                Name = "get_weather",
                Description = "Get the current weather for a location",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "location",
                        Description = "The city name (e.g., 'San Francisco', 'New York')",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "unit",
                        Description = "Temperature unit: 'celsius' or 'fahrenheit'",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = false
                    }
                ]
            },
            new FunctionContract
            {
                Name = "get_time",
                Description = "Get the current time for a timezone",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "timezone",
                        Description = "Timezone (e.g., 'PST', 'EST', 'UTC')",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true
                    }
                ]
            }
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["get_weather"] = async (args) =>
            {
                var weatherArgs = JsonSerializer.Deserialize<WeatherArgs>(args);
                _logger.LogInformation("Getting weather for {Location}", weatherArgs?.Location);

                // Simulate API call
                await Task.Delay(100);

                var weather = new
                {
                    location = weatherArgs?.Location,
                    temperature = 72,
                    unit = weatherArgs?.Unit ?? "fahrenheit",
                    condition = "sunny"
                };

                return JsonSerializer.Serialize(weather);
            },
            ["get_time"] = async (args) =>
            {
                var timeArgs = JsonSerializer.Deserialize<TimeArgs>(args);
                _logger.LogInformation("Getting time for {Timezone}", timeArgs?.Timezone);

                // Simulate API call
                await Task.Delay(100);

                var time = new
                {
                    timezone = timeArgs?.Timezone,
                    time = DateTime.UtcNow.ToString("HH:mm:ss"),
                    date = DateTime.UtcNow.ToString("yyyy-MM-dd")
                };

                return JsonSerializer.Serialize(time);
            }
        };

        // ===== Step 2: Setup Agent with Simplified Message Flow =====

        var provider = new OpenAgent(
            model: "gpt-4",
            apiKey: apiKey
        );

        // Create middleware pipeline:
        // 1. MessageTransformationMiddleware (outermost) - assigns messageOrderIdx, reconstructs aggregates
        // 2. ToolCallInjectionMiddleware - injects tool definitions
        var agent = provider
            .WithToolCallInjection(functions)
            .WithMessageTransformation();

        // ===== Step 3: Start Conversation =====

        var conversationHistory = new List<IMessage>
        {
            new TextMessage
            {
                Text = "What's the weather in San Francisco and what time is it in PST?",
                Role = Role.User
            }
        };

        _logger.LogInformation("User: {Message}", conversationHistory[0].GetTextContent());

        // ===== Step 4: Agentic Loop with Explicit Tool Execution =====

        int turnCount = 0;
        const int maxTurns = 10; // Safety limit

        while (turnCount < maxTurns)
        {
            turnCount++;
            _logger.LogInformation("Turn {Turn}: Calling LLM", turnCount);

            // Call LLM
            var response = await agent.GenerateReplyAsync(conversationHistory);
            var messages = response.ToList();

            _logger.LogInformation("Received {Count} messages", messages.Count);

            // Log all messages with their ordering
            foreach (var message in messages)
            {
                _logger.LogInformation(
                    "  [{OrderIdx}] {Type} (GenId: {GenId})",
                    message.MessageOrderIdx,
                    message.GetType().Name,
                    message.GenerationId
                );
            }

            // Add messages to conversation history
            conversationHistory.AddRange(messages);

            // Check for tool calls
            var toolCallMsg = messages.OfType<ToolsCallMessage>().FirstOrDefault();

            if (toolCallMsg == null)
            {
                // No tool calls, LLM provided final response
                _logger.LogInformation("No tool calls - conversation complete");

                var finalText = messages.OfType<TextMessage>().FirstOrDefault();
                if (finalText != null)
                {
                    _logger.LogInformation("Assistant: {Message}", finalText.Text);
                }

                break;
            }

            // Execute tools
            _logger.LogInformation(
                "Executing {Count} tool call(s)",
                toolCallMsg.ToolCalls.Count
            );

            foreach (var toolCall in toolCallMsg.ToolCalls)
            {
                _logger.LogInformation(
                    "  Tool [{Idx}]: {Name} with args: {Args}",
                    toolCall.ToolCallIdx,
                    toolCall.FunctionName,
                    toolCall.FunctionArguments
                );
            }

            try
            {
                var toolResult = await ToolCallExecutor.ExecuteAsync(
                    toolCallMsg,
                    functionMap,
                    logger: _logger
                );

                // Log tool results
                _logger.LogInformation("Tool execution completed:");
                foreach (var result in toolResult.ToolCallResults)
                {
                    _logger.LogInformation(
                        "  Result for {ToolCallId}: {Result}",
                        result.ToolCallId,
                        result.Result.Length > 100
                            ? result.Result[..100] + "..."
                            : result.Result
                    );
                }

                // Add tool results to conversation
                conversationHistory.Add(toolResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool execution failed");

                // Add error message to conversation
                var errorMessage = new TextMessage
                {
                    Text = $"Error executing tools: {ex.Message}",
                    Role = Role.User
                };
                conversationHistory.Add(errorMessage);
            }
        }

        if (turnCount >= maxTurns)
        {
            _logger.LogWarning("Max turns reached - stopping conversation");
        }

        // ===== Step 5: Display Full Conversation =====

        _logger.LogInformation("\n===== Full Conversation =====");
        foreach (var message in conversationHistory)
        {
            var content = message.GetTextContent();
            if (!string.IsNullOrEmpty(content))
            {
                _logger.LogInformation("[{Role}] {Content}", message.Role, content);
            }
            else
            {
                _logger.LogInformation("[{Role}] {Type}", message.Role, message.GetType().Name);
            }
        }
    }

    // ===== Helper Classes for Tool Arguments =====

    private class WeatherArgs
    {
        public string? Location { get; set; }
        public string? Unit { get; set; }
    }

    private class TimeArgs
    {
        public string? Timezone { get; set; }
    }
}

/// <summary>
/// Example demonstrating streaming with simplified message flow
/// </summary>
public class SimplifiedStreamingExample
{
    private readonly ILogger<SimplifiedStreamingExample> _logger;

    public SimplifiedStreamingExample(ILogger<SimplifiedStreamingExample> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string apiKey)
    {
        _logger.LogInformation("Starting Simplified Streaming Example");

        // Setup streaming agent
        var provider = new OpenAgent(
            model: "gpt-4",
            apiKey: apiKey
        );

        var agent = provider
            .WithMessageTransformation(); // MessageTransformationMiddleware assigns ordering to streaming messages

        var messages = new List<IMessage>
        {
            new TextMessage
            {
                Text = "Tell me a short story about a robot.",
                Role = Role.User
            }
        };

        _logger.LogInformation("User: {Message}", messages[0].GetTextContent());
        _logger.LogInformation("Assistant: ");

        // Stream response
        var stream = await agent.GenerateReplyStreamingAsync(messages);

        await foreach (var message in stream)
        {
            // Each streaming message has messageOrderIdx assigned
            switch (message)
            {
                case TextUpdateMessage textUpdate:
                    Console.Write(textUpdate.Text); // Stream to console
                    break;

                case TextMessage finalText:
                    _logger.LogInformation("\n[Final Text - OrderIdx: {Idx}]", finalText.MessageOrderIdx);
                    break;

                case UsageMessage usage:
                    _logger.LogInformation(
                        "[Usage - OrderIdx: {Idx}] Tokens: {Tokens}",
                        usage.MessageOrderIdx,
                        usage.Usage.TotalTokens
                    );
                    break;

                default:
                    _logger.LogInformation(
                        "[{Type} - OrderIdx: {Idx}]",
                        message.GetType().Name,
                        message.MessageOrderIdx
                    );
                    break;
            }
        }

        Console.WriteLine();
    }
}

/// <summary>
/// Example demonstrating backward compatibility with FunctionCallMiddleware
/// </summary>
public class BackwardCompatibilityExample
{
    private readonly ILogger<BackwardCompatibilityExample> _logger;

    public BackwardCompatibilityExample(ILogger<BackwardCompatibilityExample> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string apiKey)
    {
        _logger.LogInformation("Starting Backward Compatibility Example");

        // Old approach: FunctionCallMiddleware still works
        var functions = new[]
        {
            new FunctionContract
            {
                Name = "add",
                Description = "Add two numbers",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "a",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(int)),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(int)),
                        IsRequired = true
                    }
                ]
            }
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["add"] = async (args) =>
            {
                var addArgs = JsonSerializer.Deserialize<AddArgs>(args);
                var result = (addArgs?.A ?? 0) + (addArgs?.B ?? 0);
                await Task.Delay(10); // Simulate work
                return result.ToString();
            }
        };

        var provider = new OpenAgent(model: "gpt-4", apiKey: apiKey);

        // FunctionCallMiddleware uses new components internally:
        // - ToolCallInjectionMiddleware for injection
        // - ToolCallExecutor for execution
        // - MessageTransformationMiddleware for ordering
        var agent = new MiddlewareWrappingAgent(
            provider,
            new FunctionCallMiddleware(functions, functionMap)
        );

        var messages = new List<IMessage>
        {
            new TextMessage { Text = "What is 5 + 7?", Role = Role.User }
        };

        _logger.LogInformation("User: {Message}", messages[0].GetTextContent());

        // Tools execute automatically
        var response = await agent.GenerateReplyAsync(messages);

        foreach (var message in response)
        {
            _logger.LogInformation(
                "[{Type} - OrderIdx: {Idx}] {Content}",
                message.GetType().Name,
                message.MessageOrderIdx,
                message.GetTextContent()
            );
        }
    }

    private class AddArgs
    {
        public int A { get; set; }
        public int B { get; set; }
    }
}
