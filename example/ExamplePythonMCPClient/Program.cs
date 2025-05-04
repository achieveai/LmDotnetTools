using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Misc.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient;

public static class Program
{
    public static async Task Main()
    {
        // Load environment variables from .env file
        LoadEnvironmentVariables();

        string API_KEY = Environment.GetEnvironmentVariable("LLM_API_KEY")!;
        string API_URL = Environment.GetEnvironmentVariable("LLM_API_BASE_URL")!;
        string ANTHRPIC_API_KEY = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;
        string KV_STORE_PATH = Environment.GetEnvironmentVariable("KV_STORE_PATH")!;
        string PROMPTS_PATH = Path.Combine(
            GetWorkspaceRootPath(),
            "example",
            "ExamplePythonMCPClient",
            "prompts.yaml");

        Console.WriteLine("Example Python MCP Client Demo");

        // Create the MCP client to connect to the Python server
        List<McpServerConfig> clientsConfigs = [
            new McpServerConfig {
                Id = "python-mcp",
                Name = "Python MCP Server",
                Arguments = [
                    "--image",
                    "pyexec:latest",
                    "--code-dir",
                    GetWorkspaceRootPath() + "/.code_workspace"
                ],
                Location = $"{GetWorkspaceRootPath()}/McpServers/PythonMCPServer/run.bat",
                TransportType = TransportTypes.StdIo,
                TransportOptions = new Dictionary<string, string> {
                    ["command"] = $"{GetWorkspaceRootPath()}/McpServers/PythonMCPServer/run.bat --image pyexec:latest --code-dir {GetWorkspaceRootPath()}/.code_workspace"
                }
            },
            new McpServerConfig {
                Id = "thinking",
                Name = "Sequential Thinking",
                TransportType = TransportTypes.StdIo,
                TransportOptions = new Dictionary<string, string> {
                    ["command"] = "npx -y @modelcontextprotocol/server-sequential-thinking",
                }
            },
            new McpServerConfig {
                Id = "memory",
                Name = "Memory",
                TransportType = TransportTypes.StdIo,
                TransportOptions = new Dictionary<string, string> {
                    ["command"] = "npx -y @modelcontextprotocol/server-memory",
                    ["env"] = JsonSerializer.Serialize(new Dictionary<string, string> {
                        ["MEMORY_FILE_PATH"] = "analyst_memory.json"
                    })
                }
            },
        ];


        var pythonMcpClients = await Task.WhenAll(clientsConfigs.Select(client => McpClientFactory.CreateAsync(client)));

        try
        {
            var tools = await Task.WhenAll(pythonMcpClients.Select(client => client.ListToolsAsync()));

            foreach (var tool in tools.SelectMany(tools => tools))
            {
                Console.WriteLine($"- {tool.Name}: {tool.Description}");
            }

            var kvStore = new SqliteKvStore(KV_STORE_PATH, CachingMiddleware.S_jsonSerializerOptions);
            var cachingMiddleware = new CachingMiddleware(kvStore);

            // Create an OpenAI client
            var openClient = new OpenClient(API_KEY, API_URL);
            var openAgent = new OpenClientAgent("OpenAi", openClient) as IStreamingAgent;
            openAgent = openAgent.WithMiddleware(cachingMiddleware);

            var anthropicClient = new AnthropicClient(ANTHRPIC_API_KEY);
            var anthropicAgent = new AnthropicAgent("Claude", anthropicClient) as IStreamingAgent;
            anthropicAgent = anthropicAgent.WithMiddleware(cachingMiddleware);

            var llmAgent = anthropicAgent;

            // Create the agent pipeline with MCP middleware
            var mcpClientDictionary = clientsConfigs.Zip(pythonMcpClients.Zip(tools)).ToDictionary(pair => pair.First.Id, pair => pair.Second.First);

            // Create the middleware using the factory
            var mcpMiddlewareFactory = new McpMiddlewareFactory();
            var consolePrinterMiddleware = new ConsolePrinterHelperMiddleware();

            var theogent = llmAgent
                .WithMiddleware(consolePrinterMiddleware)
                .WithMiddleware(await mcpMiddlewareFactory.CreateFromClientsAsync(mcpClientDictionary))
                .WithMiddleware(new MessageUpdateJoinerMiddleware());

            var options = new GenerateReplyOptions
            {
                ModelId = "claude-3-7-sonnet-20250219",
                // ModelId = "meta-llama/llama-4-maverick",
                Temperature = 0f,
                MaxToken = 4096,
                ExtraProperties = ImmutableDictionary<string, object?>.Empty
            };

            Console.WriteLine("Enter a task to complete:");
            // string task = Console.ReadLine()!;
            var task = @"There is a file `data.xlsx` in the /code directory. Read the
file, analyze schema, data it contains, and then write a summary of what
data can be used and few insights based on this data.";
            string? previousPlan = null;
            string? progress = null;

            var promptReader = new PromptReader(PROMPTS_PATH);

            var dict = new Dictionary<string, object>()
            {
                ["task"] = task!,
            };

            if (previousPlan != null)
            {
                dict["previous_plan"] = previousPlan;
            }

            if (progress != null)
            {
                dict["progress"] = progress;
            }

            var plannerPrompt = promptReader
              .GetPromptChain("UniAgentLoop")
              .PromptMessages(dict);

            do
            {
                bool contLoop = true;
                while (contLoop)
                {
                    contLoop = false;
                    var repliesStream = await theogent.GenerateReplyStreamingAsync(
                        plannerPrompt,
                        options);

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
                            });
                    }
                    else if (replyMessages.Count == 1)
                    {
                        plannerPrompt.Add(replyMessages[0]);
                    }
                }

                Console.WriteLine("What's Next (q/quit to quit)?");
                var x = Console.ReadLine().Trim();

                if (x.ToLowerInvariant() == "quit" || x.ToLowerInvariant() == "q")
                {
                    break;
                }

                plannerPrompt.Add(new TextMessage { Text = x, Role = Role.User });
            }
            while (true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        await Task.WhenAll(
          pythonMcpClients.Select(client => client.DisposeAsync().AsTask()));

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadLine();
    }

    public static string GetWorkspaceRootPath()
    {
        var curPath = Environment.CurrentDirectory;
        while (curPath != null && !string.IsNullOrEmpty(curPath) && !Directory.GetFiles(curPath, "*.sln").Any())
        {
            curPath = Path.GetDirectoryName(curPath);
        }

        return curPath ?? throw new DirectoryNotFoundException("Solution root directory not found in the current directory or any parent directories.");
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
        while (curPath != null && !string.IsNullOrEmpty(curPath) && !File.Exists(Path.Combine(curPath, ".env")))
        {
            curPath = Path.GetDirectoryName(curPath);
        }

        _ = curPath != null && !string.IsNullOrEmpty(curPath) && File.Exists(Path.Combine(curPath, ".env"))
          ? DotNetEnv.Env.Load(Path.Combine(curPath, ".env"))
          : throw new FileNotFoundException(".env file not found in the current directory or any parent directories.");
    }

    public static void WriteToConsole(this IMessage message)
    {
        switch (message)
        {
            case TextMessage textMessage:
                WriteToConsoleInColor(textMessage.Text, ConsoleColor.DarkYellow, null);
                break;
            case UsageMessage usageMessage:
                WriteToConsoleInColor($"Usage: {usageMessage.Usage}", ConsoleColor.DarkGray, ConsoleColor.White);
                break;
            case ImageMessage _:
                WriteToConsoleInColor("Image generated", ConsoleColor.DarkGray, null);
                break;
            case ToolsCallMessage toolsCallMessage:
                toolsCallMessage.ToolCalls.ForEach(
                    toolCall => WriteToConsoleInColor($"Tool call: {toolCall.Index} {toolCall.FunctionName} - {toolCall.FunctionArgs}", ConsoleColor.Red, null));
                break;
            case ToolsCallAggregateMessage toolsCallAggregateMessage:
                toolsCallAggregateMessage.ToolsCallMessage.ToolCalls.Zip(toolsCallAggregateMessage.ToolsCallResult.ToolCallResults)
                    .ToImmutableList()
                    .ForEach((tup) =>
                        {
                            var (toolCall, toolCallResult) = tup;
                            WriteToConsoleInColor($"Tool call: {toolCall.Index} {toolCall.FunctionName} - {toolCall.FunctionArgs}", ConsoleColor.DarkCyan, null);
                            WriteToConsoleInColor($"Tool call result: {toolCallResult.Result}", ConsoleColor.DarkMagenta, null);
                        });
                break;
            default:
                Console.WriteLine(message.ToString());
                break;
        }
    }

    public static void WriteToConsoleInColor(
        string text,
        ConsoleColor fgColor,
        ConsoleColor? bgColor)
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
}
