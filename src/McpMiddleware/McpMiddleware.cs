using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using ModelContextProtocol.Client;
using System.Text.Json;

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
}
