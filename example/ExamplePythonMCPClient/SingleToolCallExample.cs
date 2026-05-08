using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient;

/// <summary>
///     Demonstrates a simple example with a single tool call
/// </summary>
public class SingleToolCallExample
{
    private readonly IStreamingAgent _agentWithMcp;
    private readonly IAgent _llmAgent;
    private readonly McpClient _mcpClient;

    public SingleToolCallExample(McpClient mcpClient, IAgent llmAgent, IStreamingAgent agentWithMcp)
    {
        _mcpClient = mcpClient;
        _llmAgent = llmAgent;
        _agentWithMcp = agentWithMcp;
    }

    /// <summary>
    ///     Runs the single tool call example
    /// </summary>
    public async Task RunAsync()
    {
        // Example messages for the agent
        var messages = new List<IMessage>
        {
            new TextMessage
            {
                Role = Role.System,
                Text =
                    "You are a helpful assistant that can use tools to help users. When you need to execute Python code, use the python-mcp.execute_python_in_container tool.",
            },
            new TextMessage
            {
                Role = Role.User,
                Text =
                    "Write a Python function to calculate the Fibonacci sequence up to n terms and then call it for n=10.",
            },
        };

        // Generate a response with the agent using MCP middleware for tool calls
        Console.WriteLine("\nGenerating a response with the agent...");

        var options = new GenerateReplyOptions
        {
            ModelId = "meta-llama/llama-4-maverick",
            Temperature = 0.7f,
            MaxToken = 2000,
            ExtraProperties = ImmutableDictionary<string, object?>.Empty,
        };

        var replies = await _agentWithMcp.GenerateReplyAsync(messages, options);
        var reply = replies.FirstOrDefault();
        if (reply != null)
        {
            Utils.HandleReply(reply);
            messages.Add(reply);
        }

        var replies2 = await _agentWithMcp.GenerateReplyAsync(messages, options);
        var reply2 = replies2.FirstOrDefault();
        Console.WriteLine("\nAgent Response:\n");

        if (reply2 is ICanGetText txtReply)
        {
            Console.WriteLine(txtReply.GetText());
        }
        else if (reply2 is ToolsCallResultMessage toolsCallMessage)
        {
            Console.WriteLine(string.Join("\n", toolsCallMessage.ToolCallResults.Select(tc => tc.Result)));
        }
        else if (reply2 != null)
        {
            Console.WriteLine(reply2.ToString());
        }
    }
}
