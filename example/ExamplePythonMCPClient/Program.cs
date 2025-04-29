using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmCore.Storage;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient;

class Program
{
    static async Task Main()
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
                "--image pyexec",
                $"--code-dir {GetWorkspaceRootPath()}/.code_workspace"
                ],
                Location = $"{GetWorkspaceRootPath()}/McpServers/PythonMCPServer/run.bat",
                TransportType = TransportTypes.StdIo,
            }
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
            var openAgent = new OpenClientAgent("meta-llama/llama-4-maverick", openClient);

            var anthropicClient = new AnthropicClient(ANTHRPIC_API_KEY);
            var anthropicAgent = new AnthropicAgent("meta-llama/llama-4-maverick", anthropicClient);

            var llmAgent = anthropicAgent;

            // Create the agent pipeline with MCP middleware
            var mcpClientDictionary = clientsConfigs.Zip(pythonMcpClients.Zip(tools)).ToDictionary(pair => pair.First.Id, pair => pair.Second.First);

            // Create the middleware using the factory
            var mcpMiddlewareFactory = new McpMiddlewareFactory();

            var codeExecutionAgent = llmAgent
              .WithMiddleware(await mcpMiddlewareFactory.CreateFromClientsAsync(mcpClientDictionary))
              .WithMiddleware(new MessageUpdateJoinerMiddleware());

            var normalAgent = llmAgent.WithMiddleware(new MessageUpdateJoinerMiddleware());

            var options = new GenerateReplyOptions
            {
                ModelId = "claude-3-7-sonnet-20250219",
                // ModelId = "meta-llama/llama-4-maverick",
                Temperature = 0.7f,
                MaxToken = 4096,
                ExtraProperties = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            };

            Console.WriteLine("Enter a task to complete:");
            // string task = Console.ReadLine()!;
            var task = @"There is a file `data.xlsx` in the /code directory. Read the
file, analyze schema, data it contains, and then write a summary of what
data can be used and few insights based on this data.";
            string? previousPlan = null;
            string? progress = null;

            while (!string.IsNullOrEmpty(task))
            {
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
                  .GetPromptChain("Planner")
                  .PromptMessages(dict);

                var repliesStream = await normalAgent.GenerateReplyStreamingAsync(plannerPrompt, options);

                string? plan = null;
                await foreach (var reply in repliesStream)
                {
                    if (reply is TextMessage textMessage)
                    {
                        plan = textMessage.Text;
                        Console.WriteLine(textMessage.Text);
                    }
                }

                dict["tool_goal"] = plan!;

                string? toolProgress = null;
                string? insights = null;
                do
                {
                    var toolPrompt = promptReader.GetPromptChain("ToolExecutor").PromptMessages(dict);
                    if (insights != null)
                    {
                        dict["insights"] = insights;
                    }

                    var toolRepliesStream = await codeExecutionAgent.GenerateReplyStreamingAsync(toolPrompt, options);

                    ToolsCallAggregateMessage? toolCallAggregateMessage = null;
                    await foreach (var reply in toolRepliesStream)
                    {
                        if (reply is TextMessage textMessage)
                        {
                            toolProgress = textMessage.Text;
                            Console.WriteLine(textMessage.Text);
                        }

                        if (reply is ToolsCallAggregateMessage toolsCallAggregateMessage)
                        {
                            toolCallAggregateMessage = toolsCallAggregateMessage;

                            var toolCallResults = toolsCallAggregateMessage.ToolsCallResult.ToolCallResults;
                            var toolCalls = toolsCallAggregateMessage.ToolsCallMessage.ToolCalls;
                            foreach (var (toolCall, toolCallResult) in toolCalls.Zip(toolCallResults))
                            {
                                Console.WriteLine($"Tool Call: {toolCall.FunctionName}");
                                Console.WriteLine($"Tool Call Arguments: {toolCall.FunctionArgs}");
                                Console.WriteLine("----");
                                Console.WriteLine($"Tool Call Result: {toolCallResult.Result}");
                                Console.WriteLine("----");
                            }
                        }
                    }

                    if (toolCallAggregateMessage == null)
                    {
                        break;
                    }

                    var toolSummarizerPrompt = promptReader.GetPromptChain("ToolSummarizer").PromptMessages(dict);
                    toolSummarizerPrompt.Add(toolCallAggregateMessage!);

                    var toolSummarizerRepliesStream = await normalAgent.GenerateReplyStreamingAsync(
                        toolSummarizerPrompt,
                        options);

                    await foreach (var reply in toolSummarizerRepliesStream)
                    {
                        if (reply is TextMessage textMessage)
                        {
                            insights = insights == null
                              ? textMessage.Text
                              : $"{insights}\n\n{textMessage.Text}";

                            Console.WriteLine(textMessage.Text);
                        }
                    }

                } while (!toolProgress?.Contains("Final Summary:") ?? false);

                dict["progress"] = toolProgress!;
                var progressReporterPrompt = promptReader.GetPromptChain("ProgressReporter")
                  .PromptMessages(dict);

                var progressReporterRepliesStream = await normalAgent.GenerateReplyStreamingAsync(progressReporterPrompt, options);

                string? finalSummary = null;
                await foreach (var reply in progressReporterRepliesStream)
                {
                    if (reply is TextMessage textMessage)
                    {
                        finalSummary = textMessage.Text;
                        Console.WriteLine(textMessage.Text);
                    }
                }

                previousPlan = progress;
                progress = finalSummary;

                dict["summary"] = finalSummary!;
                var continueLoopPrompt = promptReader.GetPromptChain("ContinueLoop")
                  .PromptMessages(dict);

                var continueLoopRepliesStream = await normalAgent.GenerateReplyStreamingAsync(continueLoopPrompt, options);

                string? continueLoop = null;
                await foreach (var reply in continueLoopRepliesStream)
                {
                    if (reply is TextMessage textMessage)
                    {
                        continueLoop = textMessage.Text;
                        Console.WriteLine(textMessage.Text);
                    }
                }

                if (continueLoop == "DONE")
                {
                    break;
                }
            }
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
}
