using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using CommandLine;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace LmConfigUsageExample;

internal class Program
{
    /// <summary>
    /// Shared system prompt used by both BackgroundAgenticLoop and ClaudeAgentSdkBackgroundLoop examples.
    /// </summary>
    private const string DefaultSystemPrompt =
        @"You're a medical doctor, acting as professor in medical college.

When student asks question, you'll query using query and match tool for
references using provided tools. But before you use tools, you'll analyze
(think) about what exactly student is asking, and what kind of information is
relevant to answer the question, also understand if there are any ambiguities
that need to be clarified.

You will prefer to use tools in parallel to get information faster, and you
keep trying (up to 5 times) to collect relevant information if initial attempts
don't yield useful results. Find references about all the options provided in
the question, so that each option can be evaluated and explained properly.

It's extremely important to gather accurate and comprehensive information about
each option that students can cross validate.

Once you've collected the information then answer student's question. in
following format:
---

### Question Analysis (Optional, only if there are ambiguities)
Analyze the question and outline key points to focus on.

### Per Option Explanation (If applicable)
#### Option A:
What the option means.
How is it relevant to the question.
Is it likely to be correct or incorrect.

#### Option B:
...

### Detailed Explanation
Provide a through explanation (usually 2-3 paragraph) why the correct option is correct and why the other options are incorrect. Use references gathered from tools to support your explanation.

### Any pearls (if applicable)

Pearls are concise, high-yield facts or mnemonics that help in recalling important information related to the question.
Provide any additional relevant information or mnemonics to help remember the concept.

Tabular pearls are especially useful for comparing different options or classifications.

### Short Explanation
Explaination on why correct option is correct (2-3 sentences).

Key Points: List of 2-4 bullet points summarizing the main takeaways (use bold or italics for highlighting keywords)

### Final Answer
Final answer: <Option Letter>

### References Used

list references from above tool calls with book name, chapter, page number
";

    private static async Task<int> Main(string[] args)
    {
        // Load environment variables from .env file if it exists
        // Searches current directory and parent directories
        _ = Env.TraversePath().Load();

        // Load configuration for Serilog
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        // Configure Serilog from configuration
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            Log.Information("LmConfigUsageExample starting up");

            return await Parser.Default.ParseArguments<CommandLineOptions>(args)
                .MapResult(
                    async options => await RunWithOptionsAsync(options),
                    _ => Task.FromResult(1)
                );
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.Information("LmConfigUsageExample shutting down");
            await Log.CloseAndFlushAsync();
        }
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
                var model = options.Model ?? "openrouter/bert-nebulon-alpha"; // "openai/gpt-5.1-codex-mini""x-ai/grok-4.1-fast";
                Console.WriteLine($"=== Agentic Loop Example with {model} ===\n");
                await RunAgenticExample(options.Prompt, model, options.Temperature, options.MaxTurns, options.Verbose);
                return 0;
            }

            if (options.RunBackground)
            {
                var model = options.Model ?? "openai/gpt-5.1-codex-mini"; // "qwen/qwen3-235b-a22b-thinking-2507"; // "x-ai/grok-4.1-fast"; // "openrouter/bert-nebulon-alpha";
                Console.WriteLine($"=== Background Agentic Loop Example with {model} ===\n");
                await RunBackgroundAgenticLoopExample(options.Prompt, model, options.Temperature, options.MaxTurns, options.Verbose);
                return 0;
            }

            if (options.RunClaude)
            {
                Console.WriteLine("=== ClaudeAgentSDK One-Shot Mode ===\n");
                await RunClaudeAgentSdkOneShotExample(options.Prompt, options.Temperature);
                return 0;
            }

            if (options.RunClaudeBackground)
            {
                Console.WriteLine("=== ClaudeAgentSDK Background Loop Example ===\n");
                await RunClaudeAgentSdkBackgroundLoopExample(options.Prompt, options.Temperature, options.MaxTurns, options.Verbose);
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
        _ = services.AddLogging(builder => builder.AddSerilog());
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
        _ = services.AddLogging(builder => builder.AddSerilog());
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
    ///     Example 1: Traditional file-based configuration loading
    /// </summary>
    private static void RunFileBasedExample()
    {
        Console.WriteLine("1. File-Based Configuration Loading");
        Console.WriteLine("==================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            // Create configuration from models.json file
            var configuration = new ConfigurationBuilder().AddJsonFile("models.json", false, false).Build();

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
    ///     Example 2: Embedded resource configuration loading
    /// </summary>
    private static void RunEmbeddedResourceExample()
    {
        Console.WriteLine("2. Embedded Resource Configuration Loading");
        Console.WriteLine("==========================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

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

                var configuration = new ConfigurationBuilder().AddJsonFile("models.json", false, false).Build();

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
    ///     Example 3: Stream factory configuration loading
    /// </summary>
    private static void RunStreamFactoryExample()
    {
        Console.WriteLine("3. Stream Factory Configuration Loading");
        Console.WriteLine("=======================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

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
    ///     Example 4: IOptions pattern configuration loading
    /// </summary>
    private static void RunIOptionsExample()
    {
        Console.WriteLine("4. IOptions Pattern Configuration Loading");
        Console.WriteLine("=========================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            // Create configuration using .NET's configuration system
            var configuration = new ConfigurationBuilder().AddJsonFile("models.json", false, true).Build();

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
    ///     Example 5: Provider availability checking
    /// </summary>
    private static async Task RunProviderAvailabilityExample()
    {
        Console.WriteLine("5. Provider Availability Checking");
        Console.WriteLine("==================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            var configuration = new ConfigurationBuilder().AddJsonFile("models.json", false, false).Build();

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
    ///     Example 6: ModelId Resolution and Translation
    /// </summary>
    private static async Task RunModelIdResolutionExample()
    {
        Console.WriteLine("6. ModelId Resolution and Translation");
        Console.WriteLine("=====================================");

        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            var configuration = new ConfigurationBuilder().AddJsonFile("models.json", false, false).Build();

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
    ///     ClaudeAgentSDK Provider in One-Shot Mode
    ///     Sends a prompt, runs to completion, and exits
    /// </summary>
    private static async Task RunClaudeAgentSdkOneShotExample(string prompt, float temperature)
    {
        try
        {
            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            // Configure ClaudeAgentSdkOptions with OneShot mode
            _ = services.AddSingleton(
                new ClaudeAgentSdkOptions
                {
                    ProjectRoot = Directory.GetCurrentDirectory(),
                    McpConfigPath = ".mcp.json",
                    Mode = ClaudeAgentSdkMode.OneShot,
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
                                var result = toolResultMsg.ToolCallResults[0].Result ?? "null";
                                Console.WriteLine($"[Tool Result: {result[..Math.Min(100, result.Length)]}...]");
                            }

                            break;
                        case UsageMessage usageMsg:
                            Console.WriteLine(
                                $"\n[Usage - Prompt: {usageMsg.Usage.PromptTokens}, Completion: {usageMsg.Usage.CompletionTokens}, Total: {usageMsg.Usage.TotalTokens}]"
                            );
                            Console.WriteLine(
                                $"\n[Usage - Prompt: {usageMsg.Usage.PromptTokens}, Completion: {usageMsg.Usage.CompletionTokens}, Total: {usageMsg.Usage.TotalTokens}]"
                            );
                            break;
                        case TextUpdateMessage:
                        case ToolsCallUpdateMessage:
                        case ReasoningUpdateMessage:
                        case ToolsCallAggregateMessage:
                        case ImageMessage:
                        case CompositeMessage:
                        default:
                            // Ignore these message types in this example
                            break;
                    }

                    messages = [.. messages, msg];
                }

                var userInput = Console.ReadLine();
                if (userInput == "/exit")
                {
                    break;
                }

                messages = [.. messages, new TextMessage { Text = userInput ?? string.Empty, Role = Role.User }];
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
            _ = services.AddLogging(builder => builder.AddSerilog());

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

    /// <summary>
    /// Background Agentic Loop Example
    /// Demonstrates the background agentic loop with event queues and multiple subscribers
    /// </summary>
    private static async Task RunBackgroundAgenticLoopExample(string prompt, string modelId, float temperature, int maxTurns, bool verbose)
    {
        try
        {
            prompt = @"Which of the following is the most appropriate treatment for a patient with tuberculosis in which mycobacterium is resistant to both isoniazid and rifampicin?
    A. 6 drugs for 9 months and 4 drugs for 18 months
    B. 7 drugs for 4-6 months and 4 drugs for 5 months
    C. 5 drugs for 6 months and 4 drugs for 14-16 months
    D. 5 drugs for 2 months, 4 drugs for one month and 3 drugs for 5 months";

            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            // Load configuration
            _ = services.AddLmConfigFromFile("models.json");

            var serviceProvider = services.BuildServiceProvider();
            var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();
            var agentFactory = serviceProvider.GetRequiredService<IProviderAgentFactory>();
            var logger = serviceProvider.GetRequiredService<ILogger<MultiTurnAgentLoop>>();

            // Resolve the model
            var resolution = await modelResolver.ResolveProviderAsync(modelId);
            if (resolution == null)
            {
                Console.WriteLine($"✗ Failed to resolve model: {modelId}");
                return;
            }

            Console.WriteLine($"✓ Resolved model: {resolution.EffectiveModelName}");
            Console.WriteLine($"  Provider: {resolution.EffectiveProviderName}");
            Console.WriteLine($"  Endpoint: {resolution.Connection.EndpointUrl}\n");

            // Create function registry with tools
            var registry = new FunctionRegistry();

            // Add WeatherTool via IFunctionProvider (same tool used by ClaudeAgentSdkBackgroundLoop)
            var weatherTool = new WeatherTool();
            _ = registry.AddProvider(weatherTool);

            // Load MCP servers from .mcp.json and add their tools to the registry
            var mcpConfigLoaderLogger = serviceProvider.GetRequiredService<ILogger<McpConfigLoader>>();
            await using var mcpConfigLoader = new McpConfigLoader(mcpConfigLoaderLogger);
            var mcpClients = await mcpConfigLoader.LoadFromFileAsync(".mcp.json");

            if (mcpClients.Count > 0)
            {
                Console.WriteLine($"✓ Loaded {mcpClients.Count} MCP server(s) from .mcp.json");
                var mcpProviderLogger = serviceProvider.GetService<ILogger<AchieveAi.LmDotnetTools.McpMiddleware.McpClientFunctionProvider>>();
                _ = await registry.AddMcpClientsAsync(
                    new Dictionary<string, ModelContextProtocol.Client.McpClient>(mcpClients),
                    providerName: "McpServers",
                    logger: mcpProviderLogger);
            }

            // Build and display registered functions for debugging
            var (contracts, handlers) = registry.Build();
            Console.WriteLine($"✓ Registered {contracts.Count()} function(s) via FunctionRegistry:");
            foreach (var contract in contracts)
            {
                Console.WriteLine($"    - {contract.Name}: {contract.Description}");
            }

            Console.WriteLine();

            // Create the base provider agent (no middleware - BackgroundAgenticLoop owns the stack)
            var providerAgent = agentFactory.CreateStreamingAgent(resolution);

            // Create default options with resolved model configuration
            // Enable thinking/reasoning with temperature=1.0 (required for reasoning models)
            var defaultOptions = new GenerateReplyOptions
            {
                ModelId = resolution.EffectiveModelName,
                Temperature = 1.0f, // Required for thinking models
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["reasoning"] = new Dictionary<string, object?>
                    {
                        ["effort"] = "medium",
                        ["max_tokens"] = 4096,
                    },
                    ["parallel_tool_calls"] = true,
                }.ToImmutableDictionary(),
            };

            // Create the multi-turn agent loop - it builds the full middleware stack internally:
            // MessageTransformation -> JsonFragmentUpdate -> MessageUpdateJoiner -> ToolCallInjection
            var threadId = Guid.NewGuid().ToString("N");
            await using var loop = new MultiTurnAgentLoop(
                providerAgent,
                registry,
                threadId,
                systemPrompt: DefaultSystemPrompt,
                defaultOptions: defaultOptions,
                maxTurnsPerRun: maxTurns,
                logger: logger);

            using var cts = new CancellationTokenSource();

            // Track run completions for "send and wait" pattern
            var runCompletions = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>();

            // Start UI subscriber (displays messages)
            var uiTask = Task.Run(async () =>
            {
                Console.WriteLine("[UI Subscriber] Connected\n");
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    switch (msg)
                    {
                        case RunAssignmentMessage assignment:
                            Console.WriteLine($"\n[Run Started] RunId: {assignment.Assignment.RunId}");
                            Console.WriteLine($"              GenerationId: {assignment.Assignment.GenerationId}");
                            if (assignment.Assignment.WasInjected)
                            {
                                Console.WriteLine($"              (Injected from parent: {assignment.Assignment.ParentRunId})");
                            }
                            break;
                        case RunCompletedMessage completed:
                            Console.WriteLine($"\n[Run Completed] RunId: {completed.CompletedRunId}");
                            if (completed.WasForked)
                            {
                                Console.WriteLine($"                Forked to: {completed.ForkedToRunId}");
                            }
                            // Signal completion for any waiters
                            if (runCompletions.TryRemove(completed.CompletedRunId, out var tcs))
                            {
                                _ = tcs.TrySetResult(true);
                            }
                            break;
                        case TextMessage textMsg when !string.IsNullOrEmpty(textMsg.Text):
                            Console.Write(textMsg.Text);
                            break;
                        case TextUpdateMessage textUpdate when !string.IsNullOrEmpty(textUpdate.Text):
                            Console.Write(textUpdate.Text);
                            break;
                        case ToolCallMessage toolCall:
                            Console.WriteLine($"\n[Tool Call] {toolCall.FunctionName}({toolCall.FunctionArgs})");
                            break;
                        case ToolCallUpdateMessage toolCall:
                            if (string.IsNullOrWhiteSpace(toolCall.FunctionArgs))
                            {
                                Console.Write($"\n[Tool Call] {toolCall.FunctionName} - ");
                            }
                            else
                            {
                                Console.Write(toolCall.FunctionArgs);
                            }
                            break;
                        case ToolCallResultMessage toolResult:
                            Console.WriteLine($"[Tool Result] {toolResult.Result[..Math.Min(100, toolResult.Result.Length)]}...");
                            break;
                        case ReasoningMessage reasoningMsg when !string.IsNullOrEmpty(reasoningMsg.Reasoning):
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"\n[Thinking] {reasoningMsg.Reasoning}");
                            Console.ResetColor();
                            break;
                        case ReasoningUpdateMessage reasoningUpdate when !string.IsNullOrEmpty(reasoningUpdate.Reasoning):
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.Write(reasoningUpdate.Reasoning);
                            Console.ResetColor();
                            break;

                        default:
                            // Ignore other message types
                            break;
                    }
                }
            }, cts.Token);

            // Start persistence subscriber (just logs)
            var persistTask = Task.Run(async () =>
            {
                Console.WriteLine("[Persistence Subscriber] Connected\n");
                var count = 0;
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    if (msg is TextMessage or ToolsCallMessage or ToolsCallResultMessage or RunAssignmentMessage or RunCompletedMessage)
                    {
                        count++;
                        if (verbose)
                        {
                            Console.WriteLine($"[Persist] Stored message #{count}: {msg.GetType().Name}");
                        }
                    }
                }
                Console.WriteLine($"[Persistence] Total messages stored: {count}");
            }, cts.Token);

            // Start the background loop
            var loopTask = loop.RunAsync(cts.Token);

            Console.WriteLine("Background loop started. Type messages to send, or 'exit' to quit.\n");

            // Helper to send and optionally wait for completion
            // Note: SendAsync now returns SendReceipt (fire-and-forget). The RunAssignment
            // comes via RunAssignmentMessage to subscribers when the run actually starts.
            async Task<SendReceipt> SendAndWaitAsync(string text, string inputId, bool waitForCompletion = false)
            {
                var receipt = await loop.SendAsync(
                    [new TextMessage { Text = text, Role = Role.User }],
                    inputId: inputId);

                if (waitForCompletion)
                {
                    // Register completion waiter using the receipt ID
                    // The subscriber will match RunAssignmentMessage.InputIds to find associated runs
                    var completionTcs = new TaskCompletionSource<bool>();
                    runCompletions[receipt.ReceiptId] = completionTcs;

                    // Wait for run to complete (with timeout)
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);

                    try
                    {
                        _ = await completionTcs.Task.WaitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        Console.WriteLine("\n[Timeout] Run did not complete within 5 minutes");
                    }
                }

                return receipt;
            }

            // Send initial prompt and wait for completion
            Console.WriteLine($"Sending initial prompt: \"{prompt}\"\n");
            var receipt1 = await SendAndWaitAsync(prompt, "initial-prompt", waitForCompletion: true);
            Console.WriteLine($"[Client] Message queued with receipt: {receipt1.ReceiptId}\n");

            // Check if we should go interactive (stdin available) or exit
            var isInteractive = !Console.IsInputRedirected;

            if (isInteractive)
            {
                // Interactive loop
                while (true)
                {
                    Console.Write("\n> ");
                    var input = Console.ReadLine();
                    if (string.IsNullOrEmpty(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    // Send the message and wait for completion
                    // Note: SendAsync returns SendReceipt (fire-and-forget). RunAssignment comes via subscriber.
                    var receipt = await SendAndWaitAsync(input, Guid.NewGuid().ToString("N")[..8], waitForCompletion: true);

                    Console.WriteLine($"[Client] Message queued with receipt: {receipt.ReceiptId}");
                }
            }
            else
            {
                Console.WriteLine("\n[Non-interactive mode] Initial prompt completed, exiting.");
            }

            // Stop the loop
            Console.WriteLine("\nStopping background loop...");
            await cts.CancelAsync();

            try
            {
                await Task.WhenAll(loopTask, uiTask, persistTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            Console.WriteLine("\n✓ Background agentic loop example completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Background agentic loop example failed: {ex.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// ClaudeAgentSDK Background Loop Example
    /// Demonstrates the same interface as BackgroundAgenticLoop using ClaudeAgentSdkAgent
    /// with MCP servers for tool access via config
    /// </summary>
    private static async Task RunClaudeAgentSdkBackgroundLoopExample(
        string prompt,
        float temperature,
        int maxTurns,
        bool verbose)
    {
        try
        {
            prompt = @"Which of the following is the most appropriate treatment for a patient with tuberculosis in which mycobacterium is resistant to both isoniazid and rifampicin?
    A. 6 drugs for 9 months and 4 drugs for 18 months
    B. 7 drugs for 4-6 months and 4 drugs for 5 months
    C. 5 drugs for 6 months and 4 drugs for 14-16 months
    D. 5 drugs for 2 months, 4 drugs for one month and 3 drugs for 5 months";

            var services = new ServiceCollection();
            _ = services.AddLogging(builder => builder.AddSerilog());

            // Load configuration
            _ = services.AddLmConfigFromFile("models.json");

            var serviceProvider = services.BuildServiceProvider();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<ClaudeAgentLoop>();

            // Configure ClaudeAgentSdkOptions
            var claudeOptions = new ClaudeAgentSdkOptions
            {
                ProjectRoot = Directory.GetCurrentDirectory(),
                McpConfigPath = ".mcp.json",
                Mode = ClaudeAgentSdkMode.Interactive, // Also works with OneShot
                MaxThinkingTokens = 8092,
                MaxTurnsPerRun = maxTurns
            };

            // Load MCP servers from .mcp.json
            var mcpServers = new Dictionary<string, McpServerConfig>();
            var mcpConfigPath = ".mcp.json";

            if (File.Exists(mcpConfigPath))
            {
                Console.WriteLine("Loading MCP server configuration from .mcp.json...");
                try
                {
                    var mcpJson = await File.ReadAllTextAsync(mcpConfigPath);
                    var mcpConfig = JsonSerializer.Deserialize<McpConfiguration>(mcpJson);

                    if (mcpConfig?.McpServers != null)
                    {
                        foreach (var (name, config) in mcpConfig.McpServers)
                        {
                            mcpServers[name] = config;
                        }

                        Console.WriteLine($"✓ Loaded {mcpServers.Count} MCP server(s) from config");
                        foreach (var (name, config) in mcpServers)
                        {
                            var serverType = config.Type ?? "stdio";
                            if (serverType == "http")
                            {
                                Console.WriteLine($"  - {name}: HTTP endpoint at {config.Url}");
                            }
                            else
                            {
                                Console.WriteLine($"  - {name}: {config.Command} {string.Join(" ", config.Args ?? [])}");
                            }
                        }

                        Console.WriteLine();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to load MCP config: {ex.Message}\n");
                }
            }
            else
            {
                Console.WriteLine($"Note: No .mcp.json found at {mcpConfigPath}, running without MCP tools\n");
            }

            // Start .NET MCP server hosting our weather tool
            // This demonstrates how Claude can call back into our .NET function via MCP HTTP protocol
            var weatherTool = new WeatherTool();
            var mcpServices = new ServiceCollection();
            _ = mcpServices.AddFunctionProvider(weatherTool);
            _ = mcpServices.AddMcpFunctionProviderServer();
            var mcpServiceProvider = mcpServices.BuildServiceProvider();
            var mcpServer = mcpServiceProvider.GetRequiredService<McpFunctionProviderServer>();

            await mcpServer.StartAsync();
            Console.WriteLine($"✓ Started .NET MCP server at: {mcpServer.McpEndpointUrl}");

            // Add .NET MCP server as HTTP type (Claude will call back into our function)
            mcpServers["dotnet-weather"] = McpServerConfig.CreateHttp(mcpServer.McpEndpointUrl!);
            Console.WriteLine($"  - dotnet-weather: HTTP endpoint at {mcpServer.McpEndpointUrl}\n");

            // Create default options
            var defaultOptions = new GenerateReplyOptions
            {
                ModelId = "claude-haiku-4-5",
                Temperature = temperature,
            };

            // Create the ClaudeAgentLoop
            var threadId = Guid.NewGuid().ToString("N");
            await using var loop = new ClaudeAgentLoop(
                claudeOptions,
                mcpServers,
                threadId,
                systemPrompt: DefaultSystemPrompt,
                defaultOptions: defaultOptions,
                logger: logger,
                loggerFactory: loggerFactory);

            using var cts = new CancellationTokenSource();

            // Track run completions
            var runCompletions = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>();

            // Start UI subscriber
            var uiTask = Task.Run(async () =>
            {
                Console.WriteLine("[UI Subscriber] Connected\n");
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    switch (msg)
                    {
                        case RunAssignmentMessage assignment:
                            Console.WriteLine($"\n[Run Started] RunId: {assignment.Assignment.RunId}");
                            break;
                        case RunCompletedMessage completed:
                            Console.WriteLine($"\n[Run Completed] RunId: {completed.CompletedRunId}");
                            if (runCompletions.TryRemove(completed.CompletedRunId, out var tcs))
                            {
                                _ = tcs.TrySetResult(true);
                            }

                            break;
                        case TextMessage textMsg when !string.IsNullOrEmpty(textMsg.Text):
                            Console.Write(textMsg.Text);
                            break;
                        case TextUpdateMessage textUpdate when !string.IsNullOrEmpty(textUpdate.Text):
                            Console.Write(textUpdate.Text);
                            break;
                        case ReasoningMessage reasoning:
                            Console.WriteLine($"\n[Thinking] {reasoning.Reasoning}");
                            break;
                        case ToolCallMessage toolCall:
                            Console.WriteLine($"\n[Tool Call] {toolCall.FunctionName}({toolCall.FunctionArgs})");
                            break;
                        case ToolCallResultMessage toolResult:
                            var resultPreview = toolResult.Result.Length > 100
                                ? toolResult.Result[..100] + "..."
                                : toolResult.Result;
                            Console.WriteLine($"[Tool Result] {resultPreview}");
                            break;
                        default:
                            break;
                    }
                }
            }, cts.Token);

            // Start the background loop
            var loopTask = loop.RunAsync(cts.Token);

            Console.WriteLine("ClaudeAgentLoop started. Type messages to send, or 'exit' to quit.\n");

            // Helper to send and wait
            // Note: SendAsync now returns SendReceipt (fire-and-forget). The RunAssignment
            // comes via RunAssignmentMessage to subscribers when the run actually starts.
            async Task<SendReceipt> SendAndWaitAsync(string text, string inputId, bool waitForCompletion = false)
            {
                var receipt = await loop.SendAsync(
                    [new TextMessage { Text = text, Role = Role.User }],
                    inputId: inputId);

                if (waitForCompletion)
                {
                    var completionTcs = new TaskCompletionSource<bool>();
                    runCompletions[receipt.ReceiptId] = completionTcs;

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);

                    try
                    {
                        _ = await completionTcs.Task.WaitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        Console.WriteLine("\n[Timeout] Run did not complete within 5 minutes");
                    }
                }

                return receipt;
            }

            // Send initial prompt
            Console.WriteLine($"Sending initial prompt: \"{prompt}\"\n");
            var receipt1 = await SendAndWaitAsync(prompt, "initial-prompt", waitForCompletion: true);
            Console.WriteLine($"[Client] Message queued with receipt: {receipt1.ReceiptId}\n");

            // Interactive loop
            var isInteractive = !Console.IsInputRedirected;

            if (isInteractive)
            {
                while (true)
                {
                    Console.Write("\n> ");
                    var input = Console.ReadLine();
                    if (string.IsNullOrEmpty(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    var receipt = await SendAndWaitAsync(input, Guid.NewGuid().ToString("N")[..8], waitForCompletion: true);
                    Console.WriteLine($"[Client] Message queued with receipt: {receipt.ReceiptId}");
                }
            }
            else
            {
                Console.WriteLine("\n[Non-interactive mode] Initial prompt completed, exiting.");
            }

            // Stop the loop
            Console.WriteLine("\nStopping ClaudeAgentLoop...");
            await cts.CancelAsync();

            try
            {
                await Task.WhenAll(loopTask, uiTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Dispose the .NET MCP server
            Console.WriteLine("Stopping .NET MCP server...");
            await mcpServer.DisposeAsync();

            Console.WriteLine("\n✓ ClaudeAgentLoop example completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ClaudeAgentLoop example failed: {ex.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace}");
        }
    }
}

/// <summary>
/// Weather tool that demonstrates IFunctionProvider implementation for MCP server exposure.
/// This tool is exposed via McpFunctionProviderServer so Claude can call back into our .NET function.
/// </summary>
internal sealed class WeatherTool : IFunctionProvider
{
    public string ProviderName => "DotNetWeather";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "get_weather",
            Description = "Get the current weather for a city (from .NET)",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "city",
                    Description = "The city name",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true,
                },
            ],
            ReturnType = typeof(string),
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = GetWeatherAsync,
            ProviderName = ProviderName,
        };
    }

    private static async Task<string> GetWeatherAsync(string argumentsJson)
    {
        await Task.Delay(50); // Simulate async operation

        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        var city = args?["city"].GetString() ?? "Unknown";

        var result = new
        {
            city,
            temperature = 72,
            condition = "Sunny",
            source = ".NET McpFunctionProviderServer",
        };

        return JsonSerializer.Serialize(result);
    }
}
