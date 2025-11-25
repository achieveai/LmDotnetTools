using System.Text;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmConfigUsageExample;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        return await Parser.Default.ParseArguments<CommandLineOptions>(args)
            .MapResult(
                async options => await RunWithOptionsAsync(options),
                _ => Task.FromResult(1)
            );
    }

    private static async Task<int> RunWithOptionsAsync(CommandLineOptions options)
    {
        try
        {
            // Handle list commands first
            if (options.ListModels)
            {
                await ListModelsAsync(options.Verbose);
                return 0;
            }

            if (options.ListProviders)
            {
                await ListProvidersAsync(options.Verbose);
                return 0;
            }

            // Handle run modes
            if (options.RunAll)
            {
                Console.WriteLine("=== LmConfig Usage Examples ===\n");
                RunFileBasedExample();
                RunEmbeddedResourceExample();
                RunStreamFactoryExample();
                RunIOptionsExample();
                await RunProviderAvailabilityExample();
                await RunModelIdResolutionExample();
                Console.WriteLine("\nNote: Use --claude for ClaudeAgentSDK one-shot mode.");
                Console.WriteLine("Note: Use --grok for Grok agentic example.");
                Console.WriteLine("Note: Use --model <model-id> to specify a different model.\n");
                return 0;
            }

            if (options.RunGrok)
            {
                var model = options.Model ?? "grok-4.1";
                Console.WriteLine($"=== Agentic Loop Example with {model} ===\n");
                await RunAgenticExample(options.Prompt, model, options.Temperature, options.MaxTurns, options.Verbose);
                return 0;
            }

            if (options.RunClaude)
            {
                Console.WriteLine("=== ClaudeAgentSDK One-Shot Mode ===\n");
                await RunClaudeAgentSdkOneShotExample(options.Prompt, options.Temperature);
                return 0;
            }

            // Default: If model is specified, run agentic example with that model
            if (!string.IsNullOrEmpty(options.Model))
            {
                Console.WriteLine($"=== Agentic Loop Example with {options.Model} ===\n");
                await RunAgenticExample(options.Prompt, options.Model, options.Temperature, options.MaxTurns, options.Verbose);
                return 0;
            }

            // Default: Run ClaudeAgentSDK in OneShot mode
            Console.WriteLine("=== ClaudeAgentSDK One-Shot Mode (default) ===\n");
            Console.WriteLine("Tip: Use --help to see all options, --grok for Grok example, --model <id> for other models.\n");
            await RunClaudeAgentSdkOneShotExample(options.Prompt, options.Temperature);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            if (options.Verbose)
            {
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }

            return 1;
        }
    }

    /// <summary>
    /// Lists all available models from the configuration.
    /// </summary>
    private static async Task ListModelsAsync(bool verbose)
    {
        Console.WriteLine("=== Available Models ===\n");

        var services = new ServiceCollection();
        var logLevel = verbose ? LogLevel.Debug : LogLevel.Warning;
        _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(logLevel));
        _ = services.AddLmConfigFromFile("models.json");

        var serviceProvider = services.BuildServiceProvider();
        var appConfig = serviceProvider.GetRequiredService<IOptions<AchieveAi.LmDotnetTools.LmConfig.Models.AppConfig>>();
        var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

        var models = appConfig.Value.Models;
        Console.WriteLine($"Total models: {models.Count}\n");

        // Group models by provider family
        var groupedModels = models
            .GroupBy(m => GetModelFamily(m.Id))
            .OrderBy(g => g.Key);

        foreach (var group in groupedModels)
        {
            Console.WriteLine($"--- {group.Key} ({group.Count()} models) ---");
            foreach (var model in group.OrderBy(m => m.Id).Take(verbose ? int.MaxValue : 5))
            {
                var isReasoning = model.IsReasoning ? " [reasoning]" : "";
                var contextLength = model.Capabilities?.TokenLimits?.MaxContextTokens;
                var contextStr = contextLength.HasValue ? $" ({contextLength.Value / 1000}K ctx)" : "";

                // Check if model can be resolved
                var resolution = await modelResolver.ResolveProviderAsync(model.Id);
                var status = resolution != null ? "✓" : "✗";

                Console.WriteLine($"  {status} {model.Id}{isReasoning}{contextStr}");
            }

            if (!verbose && group.Count() > 5)
            {
                Console.WriteLine($"  ... and {group.Count() - 5} more (use --verbose to see all)");
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Lists all available providers and their status.
    /// </summary>
    private static async Task ListProvidersAsync(bool verbose)
    {
        Console.WriteLine("=== Available Providers ===\n");

        var services = new ServiceCollection();
        var logLevel = verbose ? LogLevel.Debug : LogLevel.Warning;
        _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(logLevel));
        _ = services.AddLmConfigFromFile("models.json");

        var serviceProvider = services.BuildServiceProvider();
        var appConfig = serviceProvider.GetRequiredService<IOptions<AchieveAi.LmDotnetTools.LmConfig.Models.AppConfig>>();
        var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

        var providers = appConfig.Value.ProviderRegistry ?? new Dictionary<string, AchieveAi.LmDotnetTools.LmConfig.Models.ProviderConnectionInfo>();
        Console.WriteLine($"Total providers: {providers.Count}\n");

        foreach (var (providerName, providerInfo) in providers.OrderBy(p => p.Key))
        {
            var isAvailable = await modelResolver.IsProviderAvailableAsync(providerName);
            var status = isAvailable ? "✓ Available" : "✗ Not available";
            var envVar = providerInfo.ApiKeyEnvironmentVariable ?? "N/A";
            var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar ?? ""));

            Console.WriteLine($"{status}: {providerName}");
            Console.WriteLine($"  Endpoint: {providerInfo.EndpointUrl}");
            Console.WriteLine($"  API Key Env: {envVar} {(hasKey ? "(set)" : "(not set)")}");
            Console.WriteLine($"  Compatibility: {providerInfo.Compatibility}");

            if (verbose && providerInfo.Description != null)
            {
                Console.WriteLine($"  Description: {providerInfo.Description}");
            }

            Console.WriteLine();
        }
    }

    private static string GetModelFamily(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("grok") || id.Contains("x-ai"))
        {
            return "xAI (Grok)";
        }

        if (id.Contains("claude") || id.Contains("anthropic"))
        {
            return "Anthropic (Claude)";
        }

        if (id.Contains("gpt") || id.Contains("openai"))
        {
            return "OpenAI (GPT)";
        }

        if (id.Contains("gemini") || id.Contains("google"))
        {
            return "Google (Gemini)";
        }

        if (id.Contains("deepseek"))
        {
            return "DeepSeek";
        }

        if (id.Contains("qwen"))
        {
            return "Alibaba (Qwen)";
        }

        if (id.Contains("kimi") || id.Contains("moonshot"))
        {
            return "Moonshot (Kimi)";
        }

        if (id.Contains("minimax"))
        {
            return "MiniMax";
        }

        if (id.Contains("openrouter/"))
        {
            return "OpenRouter (Cloaked)";
        }

        if (id.Contains("mistral"))
        {
            return "Mistral";
        }

        if (id.Contains("llama") || id.Contains("meta"))
        {
            return "Meta (Llama)";
        }

        return "Other";
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
            Console.WriteLine("  - Users can use logical model names like 'gpt-4.1' or 'claude-3-sonnet'");
            Console.WriteLine("  - UnifiedAgent automatically resolves to the best available provider");
            Console.WriteLine("  - Provider agents receive the correct provider-specific model names");
            Console.WriteLine("  - Supports complex routing through aggregators like OpenRouter");
            Console.WriteLine("  - Enables seamless model switching without code changes");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ModelId resolution example failed: {ex.Message}\n");
        }
    }

    /// <summary>
    /// ClaudeAgentSDK Provider in One-Shot Mode
    /// Sends a prompt, runs to completion, and exits
    /// </summary>
    private static async Task RunClaudeAgentSdkOneShotExample(string prompt, float temperature)
    {
        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            // Configure ClaudeAgentSdkOptions with OneShot mode
            _ = services.AddSingleton(
                new AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration.ClaudeAgentSdkOptions
                {
                    ProjectRoot = Directory.GetCurrentDirectory(),
                    McpConfigPath = ".mcp.json",
                    Mode = AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration.ClaudeAgentSdkMode.OneShot,
                }
            );

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
                Console.WriteLine(
                    "  - Authentication: Claude Code subscription OR ANTHROPIC_API_KEY environment variable"
                );
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
                new TextMessage { Text = prompt, Role = Role.User },
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "claude-sonnet-4-5",
                Temperature = temperature,
                MaxToken = 10, // Max turns in one-shot mode
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
                            Console.WriteLine(
                                $"\n[Thinking: {reasoningMsg.Reasoning[..Math.Min(100, reasoningMsg.Reasoning.Length)]}...]"
                            );
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
                            Console.WriteLine(
                                $"\n[Usage - Prompt: {usageMsg.Usage.PromptTokens}, Completion: {usageMsg.Usage.CompletionTokens}, Total: {usageMsg.Usage.TotalTokens}]"
                            );
                            break;
                    }

                    messages = [.. messages, msg];
                }

                var userInput = Console.ReadLine();
                if (userInput == "/exit")
                {
                    break;
                }
                else
                {
                    messages = [.. messages, new TextMessage { Text = userInput ?? string.Empty, Role = Role.User }];
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

    /// <summary>
    /// Agentic Loop Example with configurable model
    /// Demonstrates the agentic loop pattern with middleware chain
    /// </summary>
    private static async Task RunAgenticExample(string prompt, string modelId, float temperature, int maxTurns, bool verbose)
    {
        try
        {
            var services = new ServiceCollection();
            var logLevel = verbose ? LogLevel.Debug : LogLevel.Information;
            _ = services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(logLevel));

            // Load configuration
            _ = services.AddLmConfigFromFile("models.json");

            var serviceProvider = services.BuildServiceProvider();
            var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();
            var agentFactory = serviceProvider.GetRequiredService<IProviderAgentFactory>();
            var logger = serviceProvider.GetRequiredService<ILogger<OpenAiGrokAgenticExample>>();

            // Create and run the example with the specified model
            var example = new OpenAiGrokAgenticExample(modelResolver, agentFactory, logger);
            await example.RunAsync(prompt, modelId, temperature, maxTurns);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Agentic example failed: {ex.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace}");
        }
    }
}
