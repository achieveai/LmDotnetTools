using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using ModelContextProtocol;
// using ModelContextProtocol.Protocol.Types;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Middleware for handling function calls using MCP (Model Context Protocol) clients
/// </summary>
public class McpMiddleware : IStreamingMiddleware
{
    private readonly Dictionary<string, IMcpClient> _mcpClients;
    private readonly IEnumerable<FunctionContract>? _functions;
    private readonly FunctionCallMiddleware _functionCallMiddleware;

    /// <summary>
    /// Creates a new instance of the McpMiddleware
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="functions">Additional functions to include</param>
    /// <param name="name">Name of the middleware</param>
    public McpMiddleware(
        Dictionary<string, IMcpClient> mcpClients,
        IEnumerable<FunctionContract>? functions = null,
        string? name = null)
    {
        _mcpClients = mcpClients;
        _functions = functions;
        Name = name ?? nameof(McpMiddleware);

        // Create function delegates from MCP clients
        var functionMap = CreateFunctionMap(mcpClients);
        
        // Initialize the FunctionCallMiddleware with our function map
        _functionCallMiddleware = new FunctionCallMiddleware(
            functions: functions,
            functionMap: functionMap,
            name: Name
        );
    }
    
    /// <summary>
    /// Creates function delegates for the MCP clients
    /// </summary>
    private IDictionary<string, Func<string, Task<string>>> CreateFunctionMap(
        Dictionary<string, IMcpClient> mcpClients)
    {
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();
        
        foreach (var (clientId, client) in mcpClients)
        {
            // Get available tools from this client
            var tools = client.ListToolsAsync().GetAwaiter().GetResult();
            
            foreach (var tool in tools)
            {
                // Create a delegate that calls the appropriate MCP client
                functionMap[tool.Name] = async (argsJson) => 
                {
                    try 
                    {
                        // Parse arguments from JSON
                        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) 
                            ?? new Dictionary<string, object?>();
                        
                        // Call the MCP tool
                        var response = await client.CallToolAsync(tool.Name, args);
                        
                        // Extract and format text response
                        string result = string.Join(Environment.NewLine, 
                            response.Content != null
                                ? response.Content
                                    .Where(c => c?.Type == "text")
                                    .Select(c => c?.Text ?? string.Empty)
                                : Array.Empty<string>());
                        
                        return result;
                    }
                    catch (Exception ex)
                    {
                        return $"Error executing MCP tool {tool.Name}: {ex.Message}";
                    }
                };
            }
        }
        
        return functionMap;
    }

    /// <summary>
    /// Gets the name of the middleware
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Invokes the middleware
    /// </summary>
    public Task<IMessage> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default)
    {
        // Delegate to the FunctionCallMiddleware
        return _functionCallMiddleware.InvokeAsync(context, agent, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming responses
    /// </summary>
    public Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        // Delegate to the FunctionCallMiddleware
        return _functionCallMiddleware.InvokeStreamingAsync(context, agent, cancellationToken);
    }

    private IEnumerable<FunctionContract>? CombineFunctions(IEnumerable<FunctionContract>? optionFunctions)
    {
        if (_functions == null && optionFunctions == null)
        {
            return null;
        }
        
        if (_functions == null)
        {
            return optionFunctions;
        }
        
        if (optionFunctions == null)
        {
            return _functions;
        }
        
        return _functions.Concat(optionFunctions);
    }

    private async Task<IMessage> ProcessToolCallsAsync(
        IEnumerable<ToolCall> toolCalls,
        IAgent agent,
        CancellationToken cancellationToken)
    {
        var toolCallResults = new List<ToolCallResult>();
        
        foreach (var toolCall in toolCalls)
        {
            try
            {
                // Extract function name and arguments from the tool call
                string? functionName = null;
                JsonElement? arguments = null;
                
                // Check if this is a function call
                if (toolCall.Type == "function")
                {
                    // Get the function name and arguments
                    functionName = toolCall.Name;
                    arguments = toolCall.Arguments;
                }
                
                if (string.IsNullOrEmpty(functionName) || arguments == null)
                {
                    continue;
                }

                // Parse the function name to determine which MCP client to use
                // Format: clientId.toolName
                var parts = functionName.Split('.');
                if (parts.Length != 2)
                {
                    var errorMessage = $"Error: Invalid function name format '{functionName}'. Expected format: 'clientId.toolName'";
                    // Convert McpMiddleware.ToolCall to LmCore.Messages.ToolCall
                    var lmCoreToolCall = new LmCore.Messages.ToolCall(
                        toolCall.Name ?? string.Empty,
                        arguments?.ToString() ?? string.Empty)
                    {
                        ToolCallId = toolCall.Id
                    };
                    toolCallResults.Add(new ToolCallResult(lmCoreToolCall, errorMessage));
                    continue;
                }

                var clientId = parts[0];
                var toolName = parts[1];

                // Find the appropriate MCP client
                if (!_mcpClients.TryGetValue(clientId, out var mcpClient))
                {
                    var errorMessage = $"Error: MCP client '{clientId}' not found";
                    // Convert McpMiddleware.ToolCall to LmCore.Messages.ToolCall
                    var lmCoreToolCall = new LmCore.Messages.ToolCall(
                        toolCall.Name ?? string.Empty,
                        arguments?.ToString() ?? string.Empty)
                    {
                        ToolCallId = toolCall.Id
                    };
                    toolCallResults.Add(new ToolCallResult(lmCoreToolCall, errorMessage));
                    continue;
                }

                // Convert arguments to dictionary
                var argDict = ConvertJsonElementToDictionary(arguments.Value);

                // Call the MCP tool
                var result = await mcpClient.CallToolAsync(toolName, argDict, cancellationToken);
                
                // Process the result into a string
                var resultBuilder = new StringBuilder();
                if (result.Content != null)
                {
                    foreach (var content in result.Content)
                    {
                        if (content.Type == "text" && !string.IsNullOrEmpty(content.Text))
                        {
                            resultBuilder.AppendLine(content.Text);
                        }
                    }
                }
                
                // Add the result to toolCallResults - convert to LmCore.Messages.ToolCall first
                var coreToolCall = new LmCore.Messages.ToolCall(
                    toolCall.Name ?? string.Empty,
                    arguments?.ToString() ?? string.Empty)
                {
                    ToolCallId = toolCall.Id
                };
                toolCallResults.Add(new ToolCallResult(coreToolCall, resultBuilder.ToString()));
            }
            catch (Exception ex)
            {
                // Convert to LmCore.Messages.ToolCall for error reporting
                var coreToolCall = new LmCore.Messages.ToolCall(
                    toolCall.Name ?? "unknown",
                    toolCall.Arguments?.ToString() ?? string.Empty)
                {
                    ToolCallId = toolCall.Id ?? Guid.NewGuid().ToString()
                };
                var errorMessage = $"Error executing function: {ex.Message}";
                toolCallResults.Add(new ToolCallResult(coreToolCall, errorMessage));
            }
        }

        // Return ToolsCallResultMessage with the results
        return new ToolsCallResultMessage
        {
            ToolCallResults = toolCallResults.ToImmutableList(),
            Role = Role.Assistant
        };
    }

    private async IAsyncEnumerable<IMessage> TransformStreamWithBuilder(
        IAsyncEnumerable<IMessage> streamingResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var textBuilder = new StringBuilder();
        var pendingToolCalls = new List<ToolCall>();

        await foreach (var message in streamingResponse.WithCancellation(cancellationToken))
        {
            // If the message contains tool calls, process them
            var toolCallMessage = message as ICanGetToolCalls;
            var toolCalls = toolCallMessage?.GetToolCalls();
            
            if (toolCalls != null && toolCalls.Any())
            {
                // Add to pending tool calls
                pendingToolCalls.AddRange(ToolCallConverter.Convert(toolCalls));
                
                // Process tool calls if they appear to be complete (have function args)
                var readyToProcess = pendingToolCalls.All(tc => tc.Type == "function" && tc.Name != null && tc.Arguments.HasValue);
                if (readyToProcess && pendingToolCalls.Any())
                {
                    var result = await ProcessToolCallsAsync(pendingToolCalls, null!, cancellationToken);
                    pendingToolCalls.Clear();
                    yield return result;
                }
                continue;
            }

            // For text messages, update the builder
            if (message is TextMessage textMessage)
            {
                textBuilder.Append(textMessage.Text);
                yield return new TextMessage { 
                    Text = textBuilder.ToString(),
                    Role = textMessage.Role,
                    FromAgent = textMessage.FromAgent
                };
            }
            else
            {
                // For other message types, just pass them through
                yield return message;
            }
        }

        // Process any remaining pending tool calls at the end of the stream
        if (pendingToolCalls.Any())
        {
            var result = await ProcessToolCallsAsync(pendingToolCalls, null!, cancellationToken);
            yield return result;
        }
    }

    /// <summary>
    /// Converts a JsonElement to a dictionary
    /// </summary>
    private Dictionary<string, object?> ConvertJsonElementToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>();
        }

        var result = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertJsonElementToObject(property.Value);
        }
        return result;
    }

    private object? ConvertJsonElementToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var objResult = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                {
                    objResult[property.Name] = ConvertJsonElementToObject(property.Value);
                }
                return objResult;
            case JsonValueKind.Array:
                var arrayResult = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    arrayResult.Add(ConvertJsonElementToObject(item));
                }
                return arrayResult;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt32(out int intValue))
                {
                    return intValue;
                }
                if (element.TryGetInt64(out long longValue))
                {
                    return longValue;
                }
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return null;
        }
    }
}

/// <summary>
/// Extension method to convert an array to IAsyncEnumerable
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this T[] array)
    {
        return ToAsyncEnumerableInternal(array);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerableInternal<T>(T[] array)
    {
        foreach (var item in array)
        {
            yield return item;
        }

        await Task.CompletedTask; // To make the compiler happy with the async keyword
    }
}
