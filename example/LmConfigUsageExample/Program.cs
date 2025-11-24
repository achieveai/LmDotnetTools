using System.Text;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LmConfigUsageExample;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // Parse command-line arguments
        var promptArg = Array.FindIndex(args, a => a == "--prompt" || a == "-p");
        var prompt = promptArg >= 0 && promptArg + 1 < args.Length
            ? args[promptArg + 1]
            : "Hello! Can you tell me what MCP tools are available to you?";

        // Check if user wants to run all examples (excluding ClaudeAgentSDK)
        var runAllExamples = args.Contains("--all");

        if (runAllExamples)
        {
            Console.WriteLine("=== LmConfig Usage Examples ===\n");
            RunFileBasedExample();
            RunEmbeddedResourceExample();
            RunStreamFactoryExample();
            RunIOptionsExample();
            await RunProviderAvailabilityExample();
            await RunModelIdResolutionExample();
            Console.WriteLine("\nNote: ClaudeAgentSDK example (Example 7) requires OneShot mode.");
            Console.WriteLine("Run with: dotnet run --prompt \"Your question here\"\n");
            return;
        }

        // Default: Run ClaudeAgentSDK in OneShot mode
        Console.WriteLine("=== ClaudeAgentSDK One-Shot Mode ===\n");
        await RunClaudeAgentSdkOneShotExample(prompt);
    }

    /// <summary>
    /// Example 1: Traditional file-based configuration loading
    /// </summary>
    private static void RunFileBasedExample()
    {
        Console.WriteLine("1. File-Based Configuration Loading");
        Console.WriteLine("==================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            // Create configuration from models.json file
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("models.json", optional: false, reloadOnChange: false)
                .Build();

            // Add LmConfig with file-based configuration
            _ = services.AddLmConfig(configuration);

            var serviceProvider = services.BuildServiceProvider();
            var agent = serviceProvider.GetRequiredService<IAgent>();

            Console.WriteLine("✓ Successfully loaded configuration from file");
            Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ File-based loading failed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Example 2: Embedded resource configuration loading
    /// </summary>
    private static void RunEmbeddedResourceExample()
    {
        Console.WriteLine("2. Embedded Resource Configuration Loading");
        Console.WriteLine("==========================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            // Try to load from embedded resource (will fallback to file if not embedded)
            try
            {
                _ = services.AddLmConfigFromEmbeddedResource("models.json");
                Console.WriteLine("✓ Successfully loaded configuration from embedded resource");
            }
            catch (InvalidOperationException)
            {
                // Fallback to file-based loading if embedded resource not found
                Console.WriteLine("! Embedded resource not found, falling back to file-based loading");

                var configuration = new ConfigurationBuilder()
                    .AddJsonFile("models.json", optional: false, reloadOnChange: false)
                    .Build();

                _ = services.AddLmConfig(configuration);
                Console.WriteLine("✓ Successfully loaded configuration from file as fallback");
            }

            var serviceProvider = services.BuildServiceProvider();
            var agent = serviceProvider.GetRequiredService<IAgent>();

            Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Embedded resource loading failed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Example 3: Stream factory configuration loading
    /// </summary>
    private static void RunStreamFactoryExample()
    {
        Console.WriteLine("3. Stream Factory Configuration Loading");
        Console.WriteLine("=======================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            // Load configuration using stream factory
            _ = services.AddLmConfigFromStream(() =>
            {
                // This could be from any source: HTTP, database, memory, etc.
                var configPath = Path.Combine("..", "..", "src", "LmConfig", "docs", "models.json");
                var configJson = File.ReadAllText(configPath);
                return new MemoryStream(Encoding.UTF8.GetBytes(configJson));
            });

            var serviceProvider = services.BuildServiceProvider();
            var agent = serviceProvider.GetRequiredService<IAgent>();

            Console.WriteLine("✓ Successfully loaded configuration from stream factory");
            Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Stream factory loading failed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Example 4: IOptions pattern configuration loading
    /// </summary>
    private static void RunIOptionsExample()
    {
        Console.WriteLine("4. IOptions Pattern Configuration Loading");
        Console.WriteLine("=========================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            // Create configuration using .NET's configuration system
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("models.json", optional: false, reloadOnChange: true)
                .Build();

            // Use IOptions pattern with configuration section
            _ = services.AddLmConfig(configuration);

            var serviceProvider = services.BuildServiceProvider();
            var agent = serviceProvider.GetRequiredService<IAgent>();

            Console.WriteLine("✓ Successfully loaded configuration using IOptions pattern");
            Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ IOptions loading failed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Example 5: Provider availability checking
    /// </summary>
    private static async Task RunProviderAvailabilityExample()
    {
        Console.WriteLine("5. Provider Availability Checking");
        Console.WriteLine("==================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("models.json", optional: false, reloadOnChange: false)
                .Build();

            _ = services.AddLmConfig(configuration);

            var serviceProvider = services.BuildServiceProvider();
            var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

            // Check which providers are actually available (have API keys)
            var providerNames = new[] { "OpenAI", "Anthropic", "OpenRouter", "Groq", "DeepSeek" };

            Console.WriteLine("Provider availability status:");
            foreach (var providerName in providerNames)
            {
                var isAvailable = await modelResolver.IsProviderAvailableAsync(providerName);
                var status = isAvailable ? "✓ Available" : "✗ Not available (missing API key or config)";
                Console.WriteLine($"  {providerName}: {status}");
            }

            Console.WriteLine();

            // Try to resolve a model and show which providers would be used
            var testModel = "gpt-4.1-mini";
            Console.WriteLine($"Attempting to resolve providers for model '{testModel}':");

            try
            {
                var resolution = await modelResolver.ResolveProviderAsync(testModel);
                if (resolution != null)
                {
                    Console.WriteLine($"✓ Resolved to provider: {resolution.EffectiveProviderName}");
                    Console.WriteLine($"  Model: {resolution.EffectiveModelName}");
                    Console.WriteLine($"  Endpoint: {resolution.Connection.EndpointUrl}");
                }
                else
                {
                    Console.WriteLine($"✗ No available providers found for model '{testModel}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Model resolution failed: {ex.Message}");
            }

            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Provider availability check failed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Example 6: ModelId Resolution and Translation
    /// </summary>
    private static async Task RunModelIdResolutionExample()
    {
        Console.WriteLine("6. ModelId Resolution and Translation");
        Console.WriteLine("=====================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("models.json", optional: false, reloadOnChange: false)
                .Build();

            _ = services.AddLmConfig(configuration);

            var serviceProvider = services.BuildServiceProvider();
            var unifiedAgent = serviceProvider.GetRequiredService<UnifiedAgent>();

            Console.WriteLine("Demonstrating ModelId translation from logical to provider-specific names:");
            Console.WriteLine();

            // Example 1: GPT-4.1 -> OpenRouter translation
            var gptOptions = new GenerateReplyOptions
            {
                ModelId = "gpt-4.1", // User specifies logical model ID
                Temperature = 0.7f,
            };

            try
            {
                var gptResolution = await unifiedAgent.GetProviderResolutionAsync(gptOptions);
                Console.WriteLine("Example 1: GPT-4.1 Resolution");
                Console.WriteLine($"  User requested: {gptOptions.ModelId}");
                Console.WriteLine($"  Resolved provider: {gptResolution.EffectiveProviderName}");
                Console.WriteLine($"  Provider model name: {gptResolution.EffectiveModelName}");
                Console.WriteLine(
                    $"  ✓ UnifiedAgent will translate '{gptOptions.ModelId}' to '{gptResolution.EffectiveModelName}' when calling the provider"
                );
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ GPT-4.1 resolution failed: {ex.Message}");
                Console.WriteLine();
            }

            // Example 2: Claude -> OpenRouter translation
            var claudeOptions = new GenerateReplyOptions { ModelId = "claude-3-sonnet", Temperature = 0.5f };

            try
            {
                var claudeResolution = await unifiedAgent.GetProviderResolutionAsync(claudeOptions);
                Console.WriteLine("Example 2: Claude-3-Sonnet Resolution");
                Console.WriteLine($"  User requested: {claudeOptions.ModelId}");
                Console.WriteLine($"  Resolved provider: {claudeResolution.EffectiveProviderName}");
                Console.WriteLine($"  Provider model name: {claudeResolution.EffectiveModelName}");
                Console.WriteLine(
                    $"  ✓ UnifiedAgent will translate '{claudeOptions.ModelId}' to '{claudeResolution.EffectiveModelName}' when calling the provider"
                );
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Claude resolution failed: {ex.Message}");
                Console.WriteLine();
            }

            // Example 3: DeepSeek -> Provider-specific name
            var deepSeekOptions = new GenerateReplyOptions { ModelId = "deepseek-r1" };

            try
            {
                var deepSeekResolution = await unifiedAgent.GetProviderResolutionAsync(deepSeekOptions);
                Console.WriteLine("Example 3: DeepSeek-R1 Resolution");
                Console.WriteLine($"  User requested: {deepSeekOptions.ModelId}");
                Console.WriteLine($"  Resolved provider: {deepSeekResolution.EffectiveProviderName}");
                Console.WriteLine($"  Provider model name: {deepSeekResolution.EffectiveModelName}");
                Console.WriteLine(
                    $"  ✓ UnifiedAgent will translate '{deepSeekOptions.ModelId}' to '{deepSeekResolution.EffectiveModelName}' when calling the provider"
                );
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ DeepSeek resolution failed: {ex.Message}");
                Console.WriteLine();
            }

            Console.WriteLine("Key Benefits:");
            Console.WriteLine("  • Users can use logical model names like 'gpt-4.1' or 'claude-3-sonnet'");
            Console.WriteLine("  • UnifiedAgent automatically resolves to the best available provider");
            Console.WriteLine("  • Provider agents receive the correct provider-specific model names");
            Console.WriteLine("  • Supports complex routing through aggregators like OpenRouter");
            Console.WriteLine("  • Enables seamless model switching without code changes");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ModelId resolution example failed: {ex.Message}\n");
        }
    }

    // NOTE: Interactive mode is not yet fully implemented.
    // Example 7 has been removed. Use OneShot mode (default) for ClaudeAgentSDK testing.

    /// <summary>
    /// ClaudeAgentSDK Provider in One-Shot Mode
    /// Sends a prompt, runs to completion, and exits
    /// </summary>
    private static async Task RunClaudeAgentSdkOneShotExample(string prompt)
    {
        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            // Configure ClaudeAgentSdkOptions with OneShot mode
            _ = services.AddSingleton(new AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration.ClaudeAgentSdkOptions
            {
                ProjectRoot = Directory.GetCurrentDirectory(),
                McpConfigPath = ".mcp.json",
                Mode = AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration.ClaudeAgentSdkMode.OneShot
            });

            // Use AddLmConfigFromFile to directly load the JSON file
            _ = services.AddLmConfigFromFile("models.json");

            var serviceProvider = services.BuildServiceProvider();
            var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

            // Check if ClaudeAgentSDK provider is available
            var isAvailable = await modelResolver.IsProviderAvailableAsync("ClaudeAgentSDK");
            if (!isAvailable)
            {
                Console.WriteLine("✗ ClaudeAgentSDK provider is not available");
                Console.WriteLine("  Required:");
                Console.WriteLine("  - Node.js installed");
                Console.WriteLine("  - @anthropic-ai/claude-agent-sdk npm package installed globally");
                Console.WriteLine("  - .mcp.json configuration file in the project root");
                Console.WriteLine("  - Authentication: Claude Code subscription OR ANTHROPIC_API_KEY environment variable");
                Console.WriteLine();
                return;
            }

            Console.WriteLine("✓ ClaudeAgentSDK provider is available");
            Console.WriteLine($"Prompt: \"{prompt}\"\n");

            // Try to resolve the claude-sonnet-4-5 model
            var resolution = await modelResolver.ResolveProviderAsync("claude-sonnet-4-5");
            if (resolution == null)
            {
                Console.WriteLine("✗ Failed to resolve claude-sonnet-4-5 model");
                return;
            }

            Console.WriteLine($"✓ Resolved model: {resolution.EffectiveModelName}");
            Console.WriteLine($"  Provider: {resolution.EffectiveProviderName}");

            // Create the agent with OneShot mode
            var factory = serviceProvider.GetRequiredService<IProviderAgentFactory>();
            var agent = factory.CreateStreamingAgent(resolution);

            // Configure OneShot mode via options
            var messages = new List<IMessage>
            {
                new TextMessage
                {
                    Text = prompt,
                    Role = Role.User
                }
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "claude-sonnet-4-5",
                Temperature = 0.7f,
                MaxToken = 10 // Max turns in one-shot mode
            };

            Console.WriteLine("\nAgent response (streaming):");
            Console.WriteLine("----------------------------\n");

            while (true)
            {
                var streamTask = await agent.GenerateReplyStreamingAsync(messages, options);
                await foreach (var msg in streamTask)
                {
                    switch (msg)
                    {
                        case TextMessage textMsg:
                            Console.Write(textMsg.Text);
                            break;
                        case ReasoningMessage reasoningMsg:
                            Console.WriteLine($"\n[Thinking: {reasoningMsg.Reasoning[..Math.Min(100, reasoningMsg.Reasoning.Length)]}...]");
                            break;
                        case ToolsCallMessage toolCallMsg:
                            if (!toolCallMsg.ToolCalls.IsEmpty)
                            {
                                Console.WriteLine($"\n[Tool Call: {toolCallMsg.ToolCalls[0].FunctionName}]");
                            }
                            break;
                        case ToolsCallResultMessage toolResultMsg:
                            if (!toolResultMsg.ToolCallResults.IsEmpty)
                            {
                                var result = toolResultMsg.ToolCallResults[0].Result?.ToString() ?? "null";
                                Console.WriteLine($"[Tool Result: {result[..Math.Min(100, result.Length)]}...]");
                            }
                            break;
                        case UsageMessage usageMsg:
                            Console.WriteLine($"\n[Usage - Prompt: {usageMsg.Usage.PromptTokens}, Completion: {usageMsg.Usage.CompletionTokens}, Total: {usageMsg.Usage.TotalTokens}]");
                            break;
                        default:
                            break;
                    }

                    messages = [
                        .. messages,
                        msg,
                    ];
                }

                var userInput = Console.ReadLine();
                if (userInput == "/exit")
                {
                    break;
                }
                else
                {
                    messages =
                    [
                        .. messages,
                        new TextMessage
                        {
                            Text = userInput ?? string.Empty,
                            Role = Role.User
                        },
                    ];
                }
            }

            Console.WriteLine("\n\n----------------------------");
            Console.WriteLine("✓ One-shot execution completed, program will now exit");

            // Dispose the agent
            if (agent is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ClaudeAgentSDK one-shot example failed: {ex.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace}");
        }
    }
}
