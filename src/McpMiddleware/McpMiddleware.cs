using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using ModelContextProtocol.Client;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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
    /// Private constructor for the async factory pattern
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="functions">Collection of function contracts</param>
    /// <param name="functionMap">Function map</param>
    /// <param name="name">Name of the middleware</param>
    private McpMiddleware(
        Dictionary<string, IMcpClient> mcpClients,
        IEnumerable<FunctionContract> functions,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        string name)
    {
        _mcpClients = mcpClients;
        _functions = functions;
        Name = name;

        // Initialize the FunctionCallMiddleware with our function map
        _functionCallMiddleware = new FunctionCallMiddleware(
            functions: functions,
            functionMap: functionMap,
            name: Name
        );
    }

    /// <summary>
    /// Creates a new instance of the McpMiddleware asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="functions">Optional collection of function contracts</param>
    /// <param name="name">Name of the middleware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new instance of McpMiddleware</returns>
    public static async Task<McpMiddleware> CreateAsync(
        Dictionary<string, IMcpClient> mcpClients,
        IEnumerable<FunctionContract>? functions = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        // Use default name if not provided
        name ??= nameof(McpMiddleware);
        
        // Create function delegates map
        var functionMap = await CreateFunctionMapAsync(mcpClients, cancellationToken);
        
        // If functions weren't provided, extract them from the MCP clients
        if (functions == null)
        {
            functions = await ExtractFunctionContractsAsync(mcpClients, cancellationToken);
        }
        
        // Create and return the middleware instance
        return new McpMiddleware(mcpClients, functions, functionMap, name);
    }
    
    /// <summary>
    /// Creates function delegates for the MCP clients asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of function delegates</returns>
    private static async Task<IDictionary<string, Func<string, Task<string>>>> CreateFunctionMapAsync(
        Dictionary<string, IMcpClient> mcpClients,
        CancellationToken cancellationToken = default)
    {
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();
        
        foreach (var kvp in mcpClients)
        {
            var clientId = kvp.Key;
            var client = kvp.Value;
            
            // Get available tools from this client asynchronously
            var tools = await client.ListToolsAsync(cancellationToken);
            
            foreach (var tool in tools)
            {
                // Create a delegate that calls the appropriate MCP client
                functionMap[$"{kvp.Key}.{tool.Name}"] = async (argsJson) => 
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
    /// Extracts function contracts from MCP client tools
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of function contracts</returns>
    private static async Task<IEnumerable<FunctionContract>> ExtractFunctionContractsAsync(
        Dictionary<string, IMcpClient> mcpClients,
        CancellationToken cancellationToken = default)
    {
        var functionContracts = new List<FunctionContract>();
        
        foreach (var kvp in mcpClients)
        {
            var tools = await kvp.Value.ListToolsAsync(cancellationToken);
            
            foreach (var tool in tools)
            {
                functionContracts.Add(ConvertToFunctionContract(kvp.Key, tool));
            }
        }
        
        return functionContracts;
    }

    /// <summary>
    /// Converts an MCP client tool to a function contract
    /// </summary>
    /// <param name="tool">The MCP client tool</param>
    /// <returns>A function contract</returns>
    private static FunctionContract ConvertToFunctionContract(
        string clientName,
        McpClientTool tool)
    {
        return new FunctionContract
        {
            Name = $"{clientName}.{tool.Name}",
            Description = tool.Description,
            Parameters = ExtractParametersFromSchema(tool.JsonSchema)
        };
    }

    /// <summary>
    /// Extracts function parameters from a JSON schema
    /// </summary>
    /// <param name="inputSchema">The input schema</param>
    /// <returns>Collection of parameter contracts</returns>
    private static IList<FunctionParameterContract>? ExtractParametersFromSchema(object? inputSchema)
    {
        if (inputSchema == null)
        {
            return null;
        }

        var parameters = new List<FunctionParameterContract>();

        try
        {
            // Convert the schema to JSON element
            var schemaElement = JsonSerializer.SerializeToElement(inputSchema);
            
            // Check if it's a proper JSON schema with properties
            if (schemaElement.ValueKind == JsonValueKind.Object &&
                schemaElement.TryGetProperty("properties", out var propertiesElement) &&
                propertiesElement.ValueKind == JsonValueKind.Object)
            {
                // Process each property as a parameter
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    var paramName = property.Name;
                    var paramDescription = string.Empty;
                    var paramType = typeof(string); // Default type
                    var isRequired = false;

                    // Extract parameter description
                    if (property.Value.TryGetProperty("description", out var descriptionElement) &&
                        descriptionElement.ValueKind == JsonValueKind.String)
                    {
                        paramDescription = descriptionElement.GetString() ?? string.Empty;
                    }

                    // Extract parameter type
                    if (property.Value.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind == JsonValueKind.String)
                    {
                        var typeStr = typeElement.GetString();
                        paramType = GetTypeFromJsonSchemaType(typeStr);
                    }

                    // Check if parameter is required
                    if (schemaElement.TryGetProperty("required", out var requiredElement) &&
                        requiredElement.ValueKind == JsonValueKind.Array)
                    {
                        isRequired = requiredElement.EnumerateArray()
                            .Any(item => item.ValueKind == JsonValueKind.String && 
                                       item.GetString() == paramName);
                    }

                    parameters.Add(new FunctionParameterContract
                    {
                        Name = paramName,
                        Description = paramDescription,
                        ParameterType = paramType,
                        IsRequired = isRequired
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Log the error or handle it as needed
            Console.Error.WriteLine($"Failed to extract parameters from input schema: {ex.Message}");
        }

        return parameters;
    }

    /// <summary>
    /// Maps JSON Schema types to .NET types
    /// </summary>
    /// <param name="jsonSchemaType">The JSON Schema type</param>
    /// <returns>The corresponding .NET type</returns>
    private static Type GetTypeFromJsonSchemaType(string? jsonSchemaType)
    {
        return jsonSchemaType?.ToLowerInvariant() switch
        {
            "string" => typeof(string),
            "number" => typeof(double),
            "integer" => typeof(int),
            "boolean" => typeof(bool),
            "array" => typeof(IEnumerable<object>),
            "object" => typeof(Dictionary<string, object>),
            _ => typeof(object)
        };
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
