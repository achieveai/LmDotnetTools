using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;

namespace LmConfigUsageExample;

/// <summary>
/// Example demonstrating an agentic loop using OpenAI provider connected to Grok4.1
/// with the new middleware chain architecture.
///
/// Middleware Chain:
/// Provider Agent => MessageTransformationMiddleware => JsonFragmentUpdateMiddleware
/// => MessageUpdateJoinerMiddleware => ToolCallInjectionMiddleware
///
/// Agentic Loop:
/// 1. Call middleware chain (streaming)
/// 2. Collect all messages from the stream
/// 3. Find ToolsCallMessage in the messages
/// 4. If found, use ToolCallExecutor to execute tool calls asynchronously
/// 5. Add ToolsCallResultMessage to conversation
/// 6. Loop again
/// 7. If no ToolsCallMessage, end the loop
/// </summary>
public class OpenAiGrokAgenticExample
{
    private readonly IModelResolver _modelResolver;
    private readonly IProviderAgentFactory _agentFactory;
    private readonly ILogger<OpenAiGrokAgenticExample> _logger;

    public OpenAiGrokAgenticExample(
        IModelResolver modelResolver,
        IProviderAgentFactory agentFactory,
        ILogger<OpenAiGrokAgenticExample> logger
    )
    {
        _modelResolver = modelResolver;
        _agentFactory = agentFactory;
        _logger = logger;
    }

    public async Task RunAsync(string prompt)
    {
        _logger.LogInformation("=== OpenAI Grok4.1 Agentic Loop Example ===\n");

        // ===== Step 1: Check Grok4.1 availability =====
        var isAvailable = await _modelResolver.IsProviderAvailableAsync("xAI");
        if (!isAvailable)
        {
            _logger.LogWarning("xAI provider is not available. Set XAI_API_KEY environment variable.");
            return;
        }

        // ===== Step 2: Resolve Grok4.1 model =====
        var resolution = await _modelResolver.ResolveProviderAsync("grok-4.1");
        if (resolution == null)
        {
            _logger.LogError("Failed to resolve grok-4.1 model");
            return;
        }

        _logger.LogInformation("✓ Resolved model: {ModelName}", resolution.EffectiveModelName);
        _logger.LogInformation("  Provider: {ProviderName}", resolution.EffectiveProviderName);
        _logger.LogInformation("  Endpoint: {Endpoint}\n", resolution.Connection.EndpointUrl);

        // ===== Step 3: Define Tools =====
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
            },
            new FunctionContract
            {
                Name = "calculate",
                Description = "Perform a mathematical calculation",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "expression",
                        Description = "Mathematical expression (e.g., '2 + 2', '10 * 5')",
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
                _logger.LogInformation("[TOOL] Getting weather for {Location}", weatherArgs?.Location);

                await Task.Delay(100); // Simulate API call

                var weather = new
                {
                    location = weatherArgs?.Location,
                    temperature = Random.Shared.Next(60, 85),
                    unit = weatherArgs?.Unit ?? "fahrenheit",
                    condition = new[] { "sunny", "cloudy", "rainy", "partly cloudy" }[Random.Shared.Next(4)]
                };

                return JsonSerializer.Serialize(weather);
            },
            ["get_time"] = async (args) =>
            {
                var timeArgs = JsonSerializer.Deserialize<TimeArgs>(args);
                _logger.LogInformation("[TOOL] Getting time for {Timezone}", timeArgs?.Timezone);

                await Task.Delay(100); // Simulate API call

                var time = new
                {
                    timezone = timeArgs?.Timezone,
                    time = DateTime.UtcNow.ToString("HH:mm:ss"),
                    date = DateTime.UtcNow.ToString("yyyy-MM-dd")
                };

                return JsonSerializer.Serialize(time);
            },
            ["calculate"] = async (args) =>
            {
                var calcArgs = JsonSerializer.Deserialize<CalculateArgs>(args);
                _logger.LogInformation("[TOOL] Calculating: {Expression}", calcArgs?.Expression);

                await Task.Delay(50); // Simulate processing

                // Simple eval-like calculation (for demo purposes only - unsafe in production)
                try
                {
                    var result = EvaluateSimpleExpression(calcArgs?.Expression ?? "0");
                    return JsonSerializer.Serialize(new { result, expression = calcArgs?.Expression });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }
            }
        };

        // ===== Step 4: Create Provider Agent =====
        var providerAgent = _agentFactory.CreateStreamingAgent(resolution);

        // ===== Step 5: Build Middleware Chain =====
        // Chain structure (innermost to outermost):
        // Provider Agent -> ToolCallInjectionMiddleware -> MessageUpdateJoinerMiddleware
        // -> JsonFragmentUpdateMiddleware -> MessageTransformationMiddleware

        // Build middleware chain by wrapping one at a time (innermost first)
        IStreamingAgent agentWithMiddleware = providerAgent;

        // 1. ToolCallInjectionMiddleware (innermost - closest to provider)
        agentWithMiddleware = new MiddlewareWrappingStreamingAgent(
            agentWithMiddleware,
            new ToolCallInjectionMiddleware(functions: functions, name: "ToolCallInjection")
        );

        // 2. MessageUpdateJoinerMiddleware
        agentWithMiddleware = new MiddlewareWrappingStreamingAgent(
            agentWithMiddleware,
            new MessageUpdateJoinerMiddleware(name: "MessageJoiner")
        );

        // 3. JsonFragmentUpdateMiddleware
        agentWithMiddleware = new MiddlewareWrappingStreamingAgent(
            agentWithMiddleware,
            new JsonFragmentUpdateMiddleware()
        );

        // 4. MessageTransformationMiddleware (outermost)
        agentWithMiddleware = new MiddlewareWrappingStreamingAgent(
            agentWithMiddleware,
            new MessageTransformationMiddleware(name: "MessageTransformation")
        );

        // ===== Step 6: Start Conversation =====
        var conversationHistory = new List<IMessage>
        {
            new TextMessage
            {
                Text = prompt,
                Role = Role.User
            }
        };

        _logger.LogInformation("User: {Prompt}\n", prompt);

        // ===== Step 7: Agentic Loop =====
        int turnCount = 0;
        const int maxTurns = 10; // Safety limit

        while (turnCount < maxTurns)
        {
            turnCount++;
            _logger.LogInformation("=== Turn {Turn} ===", turnCount);

            // Call LLM through middleware chain (streaming)
            var streamTask = await agentWithMiddleware.GenerateReplyStreamingAsync(
                conversationHistory,
                new GenerateReplyOptions
                {
                    ModelId = "grok-4.1",
                    Temperature = 0.7f
                }
            );

            // Collect messages from stream
            var messages = new List<IMessage>();
            var textContent = new System.Text.StringBuilder();

            await foreach (var message in streamTask)
            {
                messages.Add(message);

                // Display streaming content in real-time
                switch (message)
                {
                    case TextUpdateMessage textUpdate:
                        Console.Write(textUpdate.Text);
                        textContent.Append(textUpdate.Text);
                        break;

                    case TextMessage textMsg:
                        if (!string.IsNullOrEmpty(textMsg.Text))
                        {
                            Console.Write(textMsg.Text);
                            textContent.Append(textMsg.Text);
                        }
                        break;

                    case ReasoningUpdateMessage reasoningUpdate:
                        // Optionally display reasoning
                        break;

                    case ToolCallUpdateMessage toolCallUpdate:
                        // Display tool call progress
                        if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
                        {
                            _logger.LogDebug(
                                "[Tool Update] {FunctionName}: {Args}",
                                toolCallUpdate.FunctionName,
                                toolCallUpdate.FunctionArgs
                            );
                        }
                        break;

                    case UsageMessage usage:
                        _logger.LogDebug(
                            "[Usage] Prompt: {Prompt}, Completion: {Completion}",
                            usage.Usage.PromptTokens,
                            usage.Usage.CompletionTokens
                        );
                        break;
                }
            }

            Console.WriteLine(); // New line after streaming

            // Add all messages to conversation history
            conversationHistory.AddRange(messages);

            _logger.LogInformation("Received {Count} message(s)", messages.Count);

            // ===== Step 8: Check for Tool Calls =====
            // Look for ToolsCallMessage (plural) in the messages
            var toolCallMessage = messages.OfType<ToolsCallMessage>().FirstOrDefault();

            if (toolCallMessage == null)
            {
                // No tool calls, conversation complete
                _logger.LogInformation("✓ No tool calls - conversation complete\n");

                if (textContent.Length > 0)
                {
                    _logger.LogInformation("Assistant: {Response}", textContent.ToString());
                }

                break;
            }

            // ===== Step 9: Execute Tools =====
            _logger.LogInformation(
                "Executing {Count} tool call(s)...",
                toolCallMessage.ToolCalls.Count
            );

            foreach (var toolCall in toolCallMessage.ToolCalls)
            {
                _logger.LogInformation(
                    "  [{Idx}] {Name} with args: {Args}",
                    toolCall.ToolCallIdx,
                    toolCall.FunctionName,
                    toolCall.FunctionArgs
                );
            }

            try
            {
                // Execute tools using ToolCallExecutor
                var toolResult = await ToolCallExecutor.ExecuteAsync(
                    toolCallMessage,
                    functionMap,
                    logger: _logger
                );

                // Log tool results
                _logger.LogInformation("Tool execution completed:");
                foreach (var result in toolResult.ToolCallResults)
                {
                    var resultPreview = result.Result.Length > 100
                        ? result.Result[..100] + "..."
                        : result.Result;
                    _logger.LogInformation("  Result for {ToolCallId}: {Result}", result.ToolCallId, resultPreview);
                }

                // Add tool results to conversation
                conversationHistory.Add(toolResult);

                Console.WriteLine(); // Spacing
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
            _logger.LogWarning("⚠ Max turns reached - stopping conversation");
        }

        _logger.LogInformation("\n=== Agentic Loop Complete ===");
    }

    // ===== Helper Classes =====

    private class WeatherArgs
    {
        public string? Location { get; set; }
        public string? Unit { get; set; }
    }

    private class TimeArgs
    {
        public string? Timezone { get; set; }
    }

    private class CalculateArgs
    {
        public string? Expression { get; set; }
    }

    // Simple expression evaluator (for demo purposes only - unsafe in production)
    private static double EvaluateSimpleExpression(string expression)
    {
        expression = expression.Replace(" ", "");

        // Very basic calculator - only handles +, -, *, /
        if (expression.Contains('+'))
        {
            var parts = expression.Split('+');
            return double.Parse(parts[0]) + double.Parse(parts[1]);
        }
        if (expression.Contains('-'))
        {
            var parts = expression.Split('-');
            return double.Parse(parts[0]) - double.Parse(parts[1]);
        }
        if (expression.Contains('*'))
        {
            var parts = expression.Split('*');
            return double.Parse(parts[0]) * double.Parse(parts[1]);
        }
        if (expression.Contains('/'))
        {
            var parts = expression.Split('/');
            return double.Parse(parts[0]) / double.Parse(parts[1]);
        }

        return double.Parse(expression);
    }
}
