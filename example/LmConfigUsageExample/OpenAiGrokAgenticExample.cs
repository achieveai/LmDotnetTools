using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
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

    public async Task RunAsync(string prompt, string modelId = "x-ai/grok-4.1-fast", float temperature = 0.7f, int maxTurns = 10)
    {
        _logger.LogInformation("=== Agentic Loop Example with {ModelId} ===\n", modelId);

        // ===== Step 1: Resolve the specified model =====
        var resolution = await _modelResolver.ResolveProviderAsync(modelId);
        if (resolution == null)
        {
            _logger.LogError("Failed to resolve model: {ModelId}", modelId);
            _logger.LogWarning("Make sure the model exists in models.json and the provider has an API key set.");
            _logger.LogWarning("");
            _logger.LogWarning("Required API keys by model:");
            _logger.LogWarning("  grok-4.1, x-ai/*       -> XAI_API_KEY");
            _logger.LogWarning("  gpt-4.1*, openai/*     -> OPENAI_API_KEY");
            _logger.LogWarning("  claude-3-*, anthropic/* -> ANTHROPIC_API_KEY");
            _logger.LogWarning("  openrouter/*           -> OPENROUTER_API_KEY");
            _logger.LogWarning("  deepseek/*             -> DEEPSEEK_API_KEY");
            _logger.LogWarning("  claude-sonnet-4-5      -> ClaudeAgentSDK (no API key needed if Claude Code authenticated)");
            _logger.LogWarning("");
            _logger.LogWarning("Use '--list-providers' to see provider status.");
            return;
        }

        _logger.LogInformation("✓ Resolved model: {ModelName}", resolution.EffectiveModelName);
        _logger.LogInformation("  Provider: {ProviderName}", resolution.EffectiveProviderName);
        _logger.LogInformation("  Endpoint: {Endpoint}\n", resolution.Connection.EndpointUrl);

        // ===== Step 3: Define Tools using FunctionRegistry =====
        var registry = new FunctionRegistry();

        // get_weather
        _ = registry.AddFunction(
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
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "unit",
                        Description = "Temperature unit: 'celsius' or 'fahrenheit'",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = false,
                    },
                ],
            },
            async (args) =>
            {
                var weatherArgs = JsonSerializer.Deserialize<WeatherArgs>(args);
                _logger.LogInformation("[TOOL] Getting weather for {Location}", weatherArgs?.Location);

                await Task.Delay(100); // Simulate API call

                var weather = new
                {
                    location = weatherArgs?.Location,
                    temperature = Random.Shared.Next(60, 85),
                    unit = weatherArgs?.Unit ?? "fahrenheit",
                    condition = new[] { "sunny", "cloudy", "rainy", "partly cloudy" }[Random.Shared.Next(4)],
                };

                return JsonSerializer.Serialize(weather);
            },
            providerName: "Example"
        );

        // get_time
        _ = registry.AddFunction(
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
                        IsRequired = true,
                    },
                ],
            },
            async (args) =>
            {
                var timeArgs = JsonSerializer.Deserialize<TimeArgs>(args);
                _logger.LogInformation("[TOOL] Getting time for {Timezone}", timeArgs?.Timezone);

                await Task.Delay(100); // Simulate API call

                var time = new
                {
                    timezone = timeArgs?.Timezone,
                    time = DateTime.UtcNow.ToString("HH:mm:ss"),
                    date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                };

                return JsonSerializer.Serialize(time);
            },
            providerName: "Example"
        );

        // calculate
        _ = registry.AddFunction(
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
                        IsRequired = true,
                    },
                ],
            },
            async (args) =>
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
            },
            providerName: "Example"
        );

        // Build middleware and handlers from registry
        var (toolCallMiddleware, functionHandlers) = registry.BuildToolCallComponents(name: "ToolCallInjection");

        // ===== Step 4: Create Provider Agent =====
        var providerAgent = _agentFactory.CreateStreamingAgent(resolution)
            .WithMessageTransformation()
            .WithMiddleware(new JsonFragmentUpdateMiddleware())
            .WithMiddleware(new MessageUpdateJoinerMiddleware(name: "MessageJoiner"))
            .WithMiddleware(toolCallMiddleware);

        // ===== Step 6: Start Conversation =====
        var conversationHistory = new List<IMessage>
        {
            new TextMessage { Text = prompt, Role = Role.User },
        };

        _logger.LogInformation("User: {Prompt}\n", prompt);

        // ===== Step 7: Agentic Loop =====
        var turnCount = 0;

        while (turnCount < maxTurns)
        {
            turnCount++;
            _logger.LogInformation("=== Turn {Turn} ===", turnCount);

            // Call LLM through middleware chain (streaming)
            var streamTask = await providerAgent.GenerateReplyStreamingAsync(
                conversationHistory,
                new GenerateReplyOptions
                {
                    ModelId = modelId,
                    // Temperature = temperature,
                    RunId = Guid.NewGuid().ToString(),
                    ParentRunId = null,
                    ThreadId = Guid.NewGuid().ToString(),
                }
            );

            // Collect messages from stream and execute tool calls in parallel as they arrive
            var messages = new List<IMessage>();
            var textContent = new System.Text.StringBuilder();
            var pendingToolCalls = new Dictionary<string, Task<ToolCallResultMessage>>();

            await foreach (var message in streamTask)
            {
                messages.Add(message);

                // Display streaming content in real-time and handle tool calls
                switch (message)
                {
                    case ReasoningMessage reasoningMsg:
                        if (!string.IsNullOrEmpty(reasoningMsg.Reasoning))
                        {
                            Console.Write(reasoningMsg.Reasoning);
                            _ = textContent.Append(reasoningMsg.Reasoning);
                        }
                        break;

                    case TextMessage textMsg:
                        if (!string.IsNullOrEmpty(textMsg.Text))
                        {
                            Console.Write(textMsg.Text);
                            _ = textContent.Append(textMsg.Text);
                        }
                        break;

                    case ToolCallMessage toolCall:
                        // Start executing tool call immediately when we receive it
                        _logger.LogInformation(
                            "[Tool Call] Starting execution: {FunctionName} (id: {ToolCallId})",
                            toolCall.FunctionName,
                            toolCall.ToolCallId
                        );

                        var executionTask = ExecuteToolCallAsync(toolCall, functionHandlers);
                        pendingToolCalls[toolCall.ToolCallId ?? $"call_{pendingToolCalls.Count}"] = executionTask;
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

            // ===== Step 8: Check for Tool Calls and Await Results =====
            if (pendingToolCalls.Count == 0)
            {
                // No tool calls, conversation complete
                _logger.LogInformation("✓ No tool calls - conversation complete\n");

                if (textContent.Length > 0)
                {
                    _logger.LogInformation("Assistant: {Response}", textContent.ToString());
                }

                break;
            }

            // ===== Step 9: Await Tool Results and Add to Conversation =====
            _logger.LogInformation("Awaiting {Count} tool call result(s)...", pendingToolCalls.Count);

            try
            {
                // Await all pending tool executions
                _ = await Task.WhenAll(pendingToolCalls.Values);

                // Collect results and add to conversation
                foreach (var kvp in pendingToolCalls)
                {
                    var result = await kvp.Value;
                    var resultPreview = result.Result.Length > 100 ? result.Result[..100] + "..." : result.Result;
                    _logger.LogInformation("  Result for {ToolCallId}: {Result}", kvp.Key, resultPreview);

                    // Add each tool result to conversation history
                    conversationHistory.Add(result);
                }

                _logger.LogInformation("Tool execution completed.");
                Console.WriteLine(); // Spacing
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool execution failed");

                // Add error message to conversation
                var errorMessage = new TextMessage { Text = $"Error executing tools: {ex.Message}", Role = Role.User };
                conversationHistory.Add(errorMessage);
            }
        }

        if (turnCount >= maxTurns)
        {
            _logger.LogWarning("⚠ Max turns reached - stopping conversation");
        }

        _logger.LogInformation("\n=== Agentic Loop Complete ===");
    }

    // ===== Helper Methods =====

    /// <summary>
    /// Executes a single tool call and returns the result message.
    /// </summary>
    private async Task<ToolCallResultMessage> ExecuteToolCallAsync(
        ToolCallMessage toolCall,
        IDictionary<string, Func<string, Task<string>>> handlers
    )
    {
        var toolCallId = toolCall.ToolCallId ?? $"call_{toolCall.Index}";
        var functionName = toolCall.FunctionName ?? "unknown";
        var functionArgs = toolCall.FunctionArgs ?? "{}";

        try
        {
            if (handlers.TryGetValue(functionName, out var handler))
            {
                var result = await handler(functionArgs);
                return new ToolCallResultMessage
                {
                    ToolCallId = toolCallId,
                    Result = result,
                    Role = Role.User,
                    FromAgent = toolCall.FromAgent,
                    GenerationId = toolCall.GenerationId,
                };
            }
            else
            {
                _logger.LogWarning("No handler found for function: {FunctionName}", functionName);
                return new ToolCallResultMessage
                {
                    ToolCallId = toolCallId,
                    Result = JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" }),
                    Role = Role.User,
                    FromAgent = toolCall.FromAgent,
                    GenerationId = toolCall.GenerationId,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing tool call: {FunctionName}", functionName);
            return new ToolCallResultMessage
            {
                ToolCallId = toolCallId,
                Result = JsonSerializer.Serialize(new { error = ex.Message }),
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            };
        }
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
