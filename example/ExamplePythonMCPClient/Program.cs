using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Extensions;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using AchieveAi.LmDotnetTools.Misc.Extensions;
using AchieveAi.LmDotnetTools.Misc.Middleware;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient;

/// <summary>
/// Custom function provider for the AskUser function
/// </summary>
public class CustomFunctionProvider : IFunctionProvider
{
    public string ProviderName => "Custom";
    public int Priority => 50; // Higher priority than MCP functions

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var askUserContract = new FunctionContract
        {
            Name = "AskUser",
            Description =
                "Ask the user a question and return the answer. Use this tool "
                + "to ask the user for clarifications or to provide more information, this is "
                + "important because you will need to continue work after the user's "
                + "response.",
            Parameters = new[]
            {
                new FunctionParameterContract
                {
                    Name = "question",
                    ParameterType = new JsonSchemaObject { Type = "string" },
                    Description = "The question to ask the user.",
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "options",
                    ParameterType = JsonSchemaObject.StringArray(
                        description: "The options to choose from. If the user doesn't choose any of the options, they can say 'Other' or 'None of the above'.",
                        itemDescription: "The options to choose from. If the user doesn't choose any of the options, they can say 'Other' or 'None of the above'."
                    ),
                    Description =
                        "The options to choose from. If the user doesn't choose any of the options, they can say 'Other' or 'None of the above'.",
                    IsRequired = true,
                },
            }.ToList(),
        };

        var askUserHandler = async (string json) =>
        {
            var jsonObject = JsonObject.Parse(json)!;
            var question = jsonObject["question"]?.ToString() ?? "";
            var options =
                jsonObject["options"]?.AsArray().Select(x => x!.ToString()).ToArray() ?? [];
            return await Program.AskUser(question, options);
        };

        yield return new FunctionDescriptor
        {
            Contract = askUserContract,
            Handler = askUserHandler,
            ProviderName = ProviderName,
        };
    }
}

public static class Program
{
    public static async Task Main()
    {
        // Load environment variables from .env file
        LoadEnvironmentVariables();

        string API_KEY = Environment.GetEnvironmentVariable("LLM_API_KEY")!;
        string API_URL = Environment.GetEnvironmentVariable("LLM_API_BASE_URL")!;
        string KV_STORE_PATH = Environment.GetEnvironmentVariable("KV_STORE_PATH")!;
        string PROMPTS_PATH = Path.Combine(
            GetWorkspaceRootPath(),
            "example",
            "ExamplePythonMCPClient",
            "prompts.yaml"
        );

        Console.WriteLine("Example Python MCP Client Demo");

        var braveSearchMcpServer = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "brave-search",
                Command = "npx",
                Arguments = ["-y", "@modelcontextprotocol/server-brave-search"],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["BRAVE_API_KEY"] = Environment.GetEnvironmentVariable("BRAVE_API_KEY")!,
                },
            }
        );

        var rustMCPSystem = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "fs",
                Command =
                    @"D:\LmDotnetTools\example\rust-mcp-filesystem-x86_64-pc-windows-msvc\rust-mcp-filesystem.exe",
                Arguments = ["--allow-write", @"D:\scratchPad"],
            }
        );

        var turnDownMcpServer = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "url - fetcher",
                Command = "node",
                Arguments = ["d:/turndown-mcp/server.js"],
            }
        );

        var thinkingMcpServer = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "thinking",
                Command = "npx",
                Arguments = ["-y", "@modelcontextprotocol/server-sequential-thinking"],
            }
        );

        var memoryMcpServer = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "memory",
                Command = "npx",
                Arguments = ["-y", "@modelcontextprotocol/server-memory"],
            }
        );

        var mcpServers = new[]
        {
            braveSearchMcpServer,
            turnDownMcpServer,
            thinkingMcpServer,
            memoryMcpServer,
            rustMCPSystem,
        };
        var clientIds = new[] { "brave-search", "url-fetcher", "thinking", "memory", "fs" };

        var mcpClients = await Task.WhenAll(
            mcpServers.Select(transport => McpClientFactory.CreateAsync(transport))
        );

        try
        {
            var functionRegistry = await new FunctionRegistry()
                .AddProvider(new CustomFunctionProvider())
                .AddMcpClientsAsync(
                    mcpClients.ToDictionary(client => client.ServerInfo.Name, client => client)
                );

            // Register Todo TaskManager instance functions (stateful)
            var taskManager = new TaskManager();
            functionRegistry.AddFunctionsFromObject(
                taskManager,
                providerName: "TodoManager",
                priority: 50
            );

            // Print comprehensive function documentation
            Console.WriteLine("=== Available Functions ===");
            Console.WriteLine();
            var markdownDocs = functionRegistry.GetMarkdownDocumentation();
            Console.WriteLine(markdownDocs);
            Console.WriteLine("=== End Function Documentation ===");
            Console.WriteLine();

            // Set up services with function call support
            var services = new ServiceCollection();
            services.AddLlmFileCacheFromEnvironment(); // Uses environment variables for cache configuration

            // Add function call services
            services.AddFunctionCallServices();

            // Add MCP function providers (this will register available MCP tools from assemblies)
            services.AddMcpFunctionsFromLoadedAssemblies();

            // Build service provider
            var serviceProvider = services.BuildServiceProvider();

            // Create a caching HttpClient for OpenAI
            var httpClient = services.CreateCachingOpenAIClient(apiKey: API_KEY, baseUrl: API_URL);

            // Create an OpenAI client with caching
            var openClient = new OpenClient(httpClient, API_URL);
            var openAgent = new OpenClientAgent("OpenAi", openClient) as IStreamingAgent;

            // Get the function call middleware factory and create middleware
            var middlewareFactory =
                serviceProvider.GetRequiredService<IFunctionCallMiddlewareFactory>();
            var functionCallMiddleware = middlewareFactory.Create("Combined-Functions");

            // Create other middleware components
            var consolePrinterMiddleware = new ConsolePrinterHelperMiddleware();
            var jsonFragmentUpdateMiddleware = new JsonFragmentUpdateMiddleware();

            var theogent = openAgent
                .WithMiddleware(jsonFragmentUpdateMiddleware)
                .WithMiddleware(functionRegistry.BuildMiddleware())
                .WithMiddleware(consolePrinterMiddleware)
                .WithMiddleware(functionCallMiddleware)
                .WithMiddleware(new MessageUpdateJoinerMiddleware());

            var options = new GenerateReplyOptions
            {
                ModelId = "openai/gpt-5-mini", // "x-ai/grok-3-mini-beta", // "openai/gpt-4.1", // "qwen/qwen3-235b-a22b-thinking-2507",// "qwen/qwen3-coder", // "moonshotai/kimi-k2", //"qwen/qwen3-235b-a22b-2507",
                // ModelId = "meta-llama/llama-4-maverick",
                Temperature = 0f,
                MaxToken = 4096 * 2,
                ExtraProperties = new Dictionary<string, object?>()
                {
                    ["parallel_tool_call"] = true,
                    ["reasoning"] = new Dictionary<string, object?>()
                    {
                        ["effort"] = "low",
                        ["max_tokens"] = 768,
                    },
                }.ToImmutableDictionary(),
            };

            Console.WriteLine("Enter a task to complete:");
            // string task = Console.ReadLine()!;
            var task = @"Write me a news blog.";
            string? previousPlan = null;
            string? progress = null;

            var promptReader = new PromptReader(PROMPTS_PATH);

            var dict = new Dictionary<string, object>() { ["task"] = task! };

            if (previousPlan != null)
            {
                dict["previous_plan"] = previousPlan;
            }

            if (progress != null)
            {
                dict["progress"] = progress;
            }

            var plannerPrompt = promptReader.GetPromptChain("NewsBlogger").PromptMessages(dict);

            do
            {
                bool contLoop = true;
                while (contLoop)
                {
                    contLoop = false;
                    var repliesStream = await theogent.GenerateReplyStreamingAsync(
                        plannerPrompt,
                        options
                    );

                    var replyMessages = new List<IMessage>();
                    await foreach (var reply in repliesStream)
                    {
                        WriteToConsole(reply);
                        contLoop = contLoop || reply is ToolsCallAggregateMessage;

                        if (reply is not UsageMessage)
                        {
                            replyMessages.Add(reply);
                        }
                    }

                    if (replyMessages.Count > 1)
                    {
                        plannerPrompt.Add(
                            new CompositeMessage
                            {
                                FromAgent = "UniAgentLoop",
                                GenerationId = replyMessages[0].GenerationId,
                                Role = Role.Assistant,
                                Messages = replyMessages.ToImmutableList(),
                            }
                        );
                    }
                    else if (replyMessages.Count == 1)
                    {
                        plannerPrompt.Add(replyMessages[0]);
                    }
                }

                Console.WriteLine("What's Next (q/quit to quit)?");
                var x = Console.ReadLine()?.Trim() ?? "";

                if (x.ToLowerInvariant() == "quit" || x.ToLowerInvariant() == "q")
                {
                    break;
                }

                plannerPrompt.Add(new TextMessage { Text = x, Role = Role.User });
            } while (true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        await Task.WhenAll(mcpClients.Select(client => client.DisposeAsync().AsTask()));

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadLine();
    }

    public static async Task MainBak()
    {
        // Load environment variables from .env file
        LoadEnvironmentVariables();

        string API_KEY = Environment.GetEnvironmentVariable("LLM_API_KEY")!;
        string API_URL = Environment.GetEnvironmentVariable("LLM_API_BASE_URL")!;
        string KV_STORE_PATH = Environment.GetEnvironmentVariable("KV_STORE_PATH")!;
        string PROMPTS_PATH = Path.Combine(
            GetWorkspaceRootPath(),
            "example",
            "ExamplePythonMCPClient",
            "prompts.yaml"
        );

        Console.WriteLine("Example Python MCP Client Demo - DeepSeek R1 Reasoning");

        // === MCP client setup (identical to Main) ===
        var pythonTransport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "python-mcp",
                Command = $"{GetWorkspaceRootPath()}/McpServers/PythonMCPServer/run.bat",
                Arguments =
                [
                    "--image",
                    "pyexec:latest",
                    "--code-dir",
                    GetWorkspaceRootPath() + "/.code_workspace",
                ],
            }
        );

        var thinkingTransport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "thinking",
                Command = "npx",
                Arguments = ["-y", "@modelcontextprotocol/server-sequential-thinking"],
            }
        );

        var memoryTransport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "memory",
                Command = "npx",
                Arguments = ["-y", "@modelcontextprotocol/server-memory"],
            }
        );

        var transports = new[] { pythonTransport, thinkingTransport, memoryTransport };
        var clientIds = new[] { "python-mcp", "thinking", "memory" };

        // var pythonMcpClients = await Task.WhenAll(transports.Select(transport => McpClientFactory.CreateAsync(transport)));

        try
        {
            // var tools = await Task.WhenAll(pythonMcpClients.Select(client => client.ListToolsAsync().AsTask()));
            // foreach (var tool in tools.SelectMany(t => t))
            // {
            //     Console.WriteLine($"- {tool.Name}: {tool.Description}");
            // }

            // === LLM agent setup ===
            var kvStore = new SqliteKvStore(KV_STORE_PATH);
            // Note: CachingMiddleware may have been renamed or removed
            // var cachingMiddleware = new CachingMiddleware(kvStore);

            var openClient = new OpenClient(API_KEY, API_URL);
            var deepSeekAgent = new OpenClientAgent("DeepSeek", openClient) as IStreamingAgent;
            // deepSeekAgent = deepSeekAgent.WithMiddleware(cachingMiddleware);

            var llmAgent = deepSeekAgent;

            // === Pipeline with MCP middleware ===
            // var mcpClientDictionary = clientIds.Zip(pythonMcpClients.Zip(tools)).ToDictionary(pair => pair.First, pair => pair.Second.First);
            var mcpMiddlewareFactory = new McpMiddlewareFactory();
            var consolePrinterMiddleware = new ConsolePrinterHelperMiddleware();
            var jsonFragmentUpdateMiddleware = new JsonFragmentUpdateMiddleware();

            var theogent = llmAgent
                .WithMiddleware(jsonFragmentUpdateMiddleware)
                .WithMiddleware(consolePrinterMiddleware)
                // .WithMiddleware(await mcpMiddlewareFactory.CreateFromClientsAsync(mcpClientDictionary))
                .WithMiddleware(new MessageUpdateJoinerMiddleware());

            // === DeepSeek reasoning specific options ===
            var options = new GenerateReplyOptions
            {
                ModelId = "deepseek/deepseek-r1-0528:free",
                Temperature = 0f,
                MaxToken = 4096 * 2,
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["reasoning"] = new Dictionary<string, object?>
                    {
                        ["effort"] = "medium",
                        ["max_tokens"] = 1024,
                    },
                }.ToImmutableDictionary(),
            };

            Console.WriteLine("Enter a reasoning task to complete:");
            var task = Console.ReadLine() ?? "Explain the steps to balance a redox reaction.";

            string? previousPlan = null;
            string? progress = null;

            var promptReader = new PromptReader(PROMPTS_PATH);
            var dict = new Dictionary<string, object> { ["task"] = task };

            if (previousPlan != null)
                dict["previous_plan"] = previousPlan;
            if (progress != null)
                dict["progress"] = progress;

            var plannerPrompt = promptReader.GetPromptChain("UniAgentLoop").PromptMessages(dict);

            do
            {
                bool contLoop = false;
                var repliesStream = await theogent.GenerateReplyStreamingAsync(
                    plannerPrompt,
                    options
                );

                var replyMessages = new List<IMessage>();
                await foreach (var reply in repliesStream)
                {
                    WriteToConsole(reply);
                    contLoop = contLoop || reply is ToolsCallAggregateMessage;

                    if (reply is not UsageMessage)
                    {
                        replyMessages.Add(reply);
                    }
                }

                if (replyMessages.Count > 1)
                {
                    plannerPrompt.Add(
                        new CompositeMessage
                        {
                            FromAgent = "UniAgentLoop",
                            GenerationId = replyMessages[0].GenerationId,
                            Role = Role.Assistant,
                            Messages = replyMessages.ToImmutableList(),
                        }
                    );
                }
                else if (replyMessages.Count == 1)
                {
                    plannerPrompt.Add(replyMessages[0]);
                }

                if (!contLoop)
                {
                    Console.WriteLine("What's Next (q/quit to quit)?");
                    var x = Console.ReadLine()?.Trim() ?? "";

                    if (
                        x.Equals("quit", StringComparison.OrdinalIgnoreCase)
                        || x.Equals("q", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        break;
                    }

                    plannerPrompt.Add(new TextMessage { Text = x, Role = Role.User });
                }
            } while (true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // await Task.WhenAll(pythonMcpClients.Select(client => client.DisposeAsync().AsTask()));
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadLine();
    }

    public static string GetWorkspaceRootPath()
    {
        var curPath = Environment.CurrentDirectory;
        while (
            curPath != null
            && !string.IsNullOrEmpty(curPath)
            && !Directory.GetFiles(curPath, "*.sln").Any()
        )
        {
            curPath = Path.GetDirectoryName(curPath);
        }

        return curPath
            ?? throw new DirectoryNotFoundException(
                "Solution root directory not found in the current directory or any parent directories."
            );
    }

    /// <summary>
    /// Loads environment variables from .env file in the project root
    /// </summary>
    /// <remarks>
    /// Tries multiple locations to find the .env file:
    /// 1. Current directory
    /// 2. Project directory
    /// 3. Solution root directory
    /// </remarks>
    private static void LoadEnvironmentVariables()
    {
        var curPath = Environment.CurrentDirectory;
        while (
            curPath != null
            && !string.IsNullOrEmpty(curPath)
            && !File.Exists(Path.Combine(curPath, ".env"))
        )
        {
            curPath = Path.GetDirectoryName(curPath);
        }

        _ =
            curPath != null
            && !string.IsNullOrEmpty(curPath)
            && File.Exists(Path.Combine(curPath, ".env"))
                ? DotNetEnv.Env.Load(Path.Combine(curPath, ".env"))
                : throw new FileNotFoundException(
                    ".env file not found in the current directory or any parent directories."
                );
    }

    public static void WriteToConsole(this IMessage message)
    {
        switch (message)
        {
            case TextMessage textMessage:
                WriteToConsoleInColor(textMessage.Text, ConsoleColor.DarkYellow, null);
                break;
            case UsageMessage usageMessage:
                WriteToConsoleInColor(
                    $"Usage: {usageMessage.Usage}",
                    ConsoleColor.DarkGray,
                    ConsoleColor.White
                );
                break;
            case ReasoningMessage reasoningMessage:
                // WriteToConsoleInColor($"Reasoning: {reasoningMessage.Reasoning}", ConsoleColor.DarkGreen, null);
                break;
            case ImageMessage _:
                WriteToConsoleInColor("Image generated", ConsoleColor.DarkGray, null);
                break;
            case ToolsCallMessage toolsCallMessage:
                toolsCallMessage.ToolCalls.ForEach(toolCall =>
                    WriteToConsoleInColor(
                        $"Tool call: {toolCall.Index} {toolCall.FunctionName} - {toolCall.FunctionArgs}",
                        ConsoleColor.Red,
                        null
                    )
                );
                break;
            case ToolsCallAggregateMessage toolsCallAggregateMessage:
                toolsCallAggregateMessage
                    .ToolsCallMessage.ToolCalls.Zip(
                        toolsCallAggregateMessage.ToolsCallResult.ToolCallResults
                    )
                    .ToImmutableList()
                    .ForEach(
                        (tup) =>
                        {
                            var (toolCall, toolCallResult) = tup;
                            WriteToConsoleInColor(
                                $"Tool call: {toolCall.Index} {toolCall.FunctionName} - {toolCall.FunctionArgs}",
                                ConsoleColor.DarkCyan,
                                null
                            );
                            WriteToConsoleInColor(
                                $"Tool call result: {toolCallResult.Result}",
                                ConsoleColor.DarkMagenta,
                                null
                            );
                        }
                    );
                break;
            default:
                Console.WriteLine(message.ToString());
                break;
        }
    }

    public static void WriteToConsoleInColor(
        string text,
        ConsoleColor fgColor,
        ConsoleColor? bgColor
    )
    {
        var fgColorBak = Console.ForegroundColor;
        var bgColorBak = Console.BackgroundColor;

        try
        {
            Console.ForegroundColor = fgColor;
            Console.BackgroundColor = bgColor ?? Console.BackgroundColor;
            Console.WriteLine(text);
        }
        finally
        {
            Console.ForegroundColor = fgColorBak;
            Console.BackgroundColor = bgColorBak;
        }
    }

    [Description(
        "Ask the user a question and return the answer. Use this tool"
            + "to ask the user for clarifications or to provide more information, this is"
            + "important because you will need to continue work after the user's"
            + "response."
    )]
    public static async Task<string> AskUser(
        [Description("The question to ask the user.")] string question,
        [Description(
            "The options to choose from. If the user doesn't choose any of the options, they can say 'Other' or 'None of the above'."
        )]
            string[] options
    )
    {
        Console.WriteLine(question.Trim());
        foreach (var option in options)
        {
            Console.WriteLine($"- {option.Trim()}");
        }

        return (await Console.In.ReadLineAsync())?.Trim() ?? "";
    }
}
