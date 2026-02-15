using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
///     Middleware for handling function calls using MCP (Model Context Protocol) clients
/// </summary>
public partial class McpMiddleware : IStreamingMiddleware
{
    private readonly FunctionCallMiddleware _functionCallMiddleware;
    private readonly IEnumerable<FunctionContract>? _functions;
    private readonly ILogger<McpMiddleware> _logger;
    private readonly Dictionary<string, McpClient> _mcpClients;

    /// <summary>
    ///     Private constructor for the async factory pattern
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="functions">Collection of function contracts</param>
    /// <param name="functionMap">Function map</param>
    /// <param name="name">Name of the middleware</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="functionCallLogger">Logger for FunctionCallMiddleware</param>
    private McpMiddleware(
        Dictionary<string, McpClient> mcpClients,
        IEnumerable<FunctionContract> functions,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        string name,
        ILogger<McpMiddleware> logger,
        ILogger<FunctionCallMiddleware>? functionCallLogger = null
    )
    {
        _mcpClients = mcpClients;
        _functions = functions;
        Name = name;
        _logger = logger;

        // Initialize the FunctionCallMiddleware with our function map and logger
        _functionCallMiddleware = new FunctionCallMiddleware(functions, functionMap, Name, functionCallLogger);
    }

    /// <summary>
    ///     Gets the name of the middleware
    /// </summary>
    public string? Name { get; }

    /// <summary>
    ///     Invokes the middleware
    /// </summary>
    public Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Delegate to the FunctionCallMiddleware
        return _functionCallMiddleware.InvokeAsync(context, agent, cancellationToken);
    }

    /// <summary>
    ///     Invokes the middleware for streaming responses
    /// </summary>
    public Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Delegate to the FunctionCallMiddleware
        return _functionCallMiddleware.InvokeStreamingAsync(context, agent, cancellationToken);
    }

    /// <summary>
    ///     Creates a new instance of the McpMiddleware asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="functions">Optional collection of function contracts</param>
    /// <param name="name">Name of the middleware</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="functionCallLogger">Optional logger for FunctionCallMiddleware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new instance of McpMiddleware</returns>
    public static async Task<McpMiddleware> CreateAsync(
        Dictionary<string, McpClient> mcpClients,
        IEnumerable<FunctionContract>? functions = null,
        string? name = null,
        ILogger<McpMiddleware>? logger = null,
        ILogger<FunctionCallMiddleware>? functionCallLogger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mcpClients);

        // Use default name if not provided
        name ??= nameof(McpMiddleware);

        // Use NullLogger if no logger provided
        logger ??= NullLogger<McpMiddleware>.Instance;

        logger.LogInformation(
            "MCP client initialization started: ClientCount={ClientCount}, ClientIds={ClientIds}",
            mcpClients.Count,
            string.Join(", ", mcpClients.Keys)
        );

        // Create function delegates map
        var functionMap = await CreateFunctionMapAsync(mcpClients, logger, cancellationToken);

        // If functions weren't provided, extract them from the MCP clients
        functions ??= await ExtractFunctionContractsAsync(mcpClients, logger, cancellationToken);

        logger.LogInformation(
            "MCP middleware initialized: FunctionCount={FunctionCount}, FunctionNames={FunctionNames}",
            functions.Count(),
            string.Join(", ", functions.Select(f => f.Name))
        );

        // Create and return the middleware instance
        return new McpMiddleware(mcpClients, functions, functionMap, name, logger, functionCallLogger);
    }

    /// <summary>
    ///     Creates function delegates for the MCP clients asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of function delegates</returns>
    private static async Task<IDictionary<string, Func<string, Task<string>>>> CreateFunctionMapAsync(
        Dictionary<string, McpClient> mcpClients,
        ILogger<McpMiddleware> logger,
        CancellationToken cancellationToken = default
    )
    {
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();

        logger.LogDebug(
            "Creating function map: ClientCount={ClientCount}, ClientIds={ClientIds}",
            mcpClients.Count,
            string.Join(", ", mcpClients.Keys)
        );

        foreach (var kvp in mcpClients)
        {
            var clientId = kvp.Key;
            var client = kvp.Value;

            try
            {
                // Get available tools from this client asynchronously
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

                logger.LogInformation(
                    "MCP tool discovery completed: ClientId={ClientId}, ToolCount={ToolCount}, ToolNames={ToolNames}",
                    clientId,
                    tools.Count,
                    string.Join(", ", tools.Select(t => t.Name))
                );

                foreach (var tool in tools)
                {
                    var sanitizedClientId = SanitizeToolName(kvp.Key);
                    var sanitizedToolName = SanitizeToolName(tool.Name);
                    var functionName = $"{sanitizedClientId}-{sanitizedToolName}";

                    logger.LogDebug(
                        "Mapping function to client: FunctionName={FunctionName}, ClientId={ClientId}, ToolName={ToolName}, SanitizedName={SanitizedName}",
                        functionName,
                        clientId,
                        tool.Name,
                        functionName
                    );

                    // Create a delegate that calls the appropriate MCP client
                    functionMap[functionName] = async argsJson =>
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            logger.LogDebug(
                                "Tool argument parsing: ToolName={ToolName}, ClientId={ClientId}, ArgsJson={ArgsJson}",
                                tool.Name,
                                clientId,
                                argsJson
                            );

                            // Parse arguments from JSON
                            Dictionary<string, object?> args;
                            try
                            {
                                args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? [];
                            }
                            catch (JsonException jsonEx)
                            {
                                logger.LogError(
                                    jsonEx,
                                    "JSON parsing failed for tool arguments: ToolName={ToolName}, ClientId={ClientId}, InputData={InputData}",
                                    tool.Name,
                                    clientId,
                                    argsJson
                                );
                                throw;
                            }

                            logger.LogDebug(
                                "Tool arguments parsed: ToolName={ToolName}, ArgumentCount={ArgumentCount}, ArgumentKeys={ArgumentKeys}",
                                tool.Name,
                                args.Count,
                                string.Join(", ", args.Keys)
                            );

                            // Call the MCP tool
                            var response = await client.CallToolAsync(tool.Name, args);

                            logger.LogDebug(
                                "Tool response received: ToolName={ToolName}, ContentCount={ContentCount}",
                                tool.Name,
                                response.Content?.Count ?? 0
                            );

                            // Extract and format text response
                            var result = string.Join(
                                Environment.NewLine,
                                response.Content != null
                                    ? response
                                        .Content.Where(c => c?.Type == "text")
                                        .Select(c => c is TextContentBlock tb ? tb.Text : string.Empty)
                                    : []
                            );

                            logger.LogDebug(
                                "Tool response formatted: ToolName={ToolName}, ResultLength={ResultLength}",
                                tool.Name,
                                result.Length
                            );

                            stopwatch.Stop();
                            logger.LogInformation(
                                "MCP tool execution completed: ToolName={ToolName}, ClientId={ClientId}, Duration={Duration}ms, Success={Success}, ResultLength={ResultLength}",
                                tool.Name,
                                clientId,
                                stopwatch.ElapsedMilliseconds,
                                true,
                                result.Length
                            );

                            return result;
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            logger.LogDebug(
                                "Tool execution exception details: ToolName={ToolName}, ClientId={ClientId}, ExceptionType={ExceptionType}",
                                tool.Name,
                                clientId,
                                ex.GetType().Name
                            );
                            logger.LogError(
                                ex,
                                "MCP tool execution failed: ToolName={ToolName}, ClientId={ClientId}, Duration={Duration}ms, Arguments={Arguments}",
                                tool.Name,
                                clientId,
                                stopwatch.ElapsedMilliseconds,
                                argsJson
                            );

                            return $"Error executing MCP tool {tool.Name}: {ex.Message}";
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MCP client tool discovery failed: ClientId={ClientId}", clientId);
                // Continue with other clients even if one fails
            }
        }

        return functionMap;
    }

    /// <summary>
    ///     Extracts function contracts from MCP client tools
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of function contracts</returns>
    private static async Task<IEnumerable<FunctionContract>> ExtractFunctionContractsAsync(
        Dictionary<string, McpClient> mcpClients,
        ILogger<McpMiddleware> logger,
        CancellationToken cancellationToken = default
    )
    {
        var functionContracts = new List<FunctionContract>();

        foreach (var kvp in mcpClients)
        {
            try
            {
                var tools = await kvp.Value.ListToolsAsync(cancellationToken: cancellationToken);

                foreach (var tool in tools)
                {
                    try
                    {
                        var contract = ConvertToFunctionContract(kvp.Key, tool, logger);
                        functionContracts.Add(contract);

                        logger.LogInformation(
                            "Function contract extracted: FunctionName={FunctionName}, ClientId={ClientId}, ParameterCount={ParameterCount}",
                            contract.Name,
                            kvp.Key,
                            contract.Parameters?.Count() ?? 0
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Function contract extraction failed for tool: ClientId={ClientId}, ToolName={ToolName}",
                            kvp.Key,
                            tool.Name
                        );
                        // Continue with other tools even if one fails
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list tools for MCP client: ClientId={ClientId}", kvp.Key);
                // Continue with other clients even if one fails
            }
        }

        return functionContracts;
    }

    /// <summary>
    ///     Sanitizes a tool name to comply with OpenAI's function name requirements
    ///     OpenAI requires function names to match pattern: ^[a-zA-Z0-9_-]+$
    /// </summary>
    private static string SanitizeToolName(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return "unknown_tool";
        }

        // Replace invalid characters with underscores
        var sanitized = MyRegex().Replace(toolName, "_");

        // Ensure it doesn't start with a number (optional, but good practice)
        if (char.IsDigit(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }

        // Ensure it's not empty after sanitization
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "sanitized_tool";
        }

        return sanitized;
    }

    /// <summary>
    ///     Converts an MCP client tool to a function contract
    /// </summary>
    /// <param name="clientName">The client name</param>
    /// <param name="tool">The MCP client tool</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>A function contract</returns>
    private static FunctionContract ConvertToFunctionContract(
        string clientName,
        McpClientTool tool,
        ILogger<McpMiddleware>? logger = null
    )
    {
        var sanitizedToolName = SanitizeToolName(tool.Name);
        var functionName = $"{SanitizeToolName(clientName)}-{sanitizedToolName}";

        return new FunctionContract
        {
            Name = functionName,
            Description = tool.Description,
            Parameters = ExtractParametersFromSchema(tool.JsonSchema, logger),
        };
    }

    /// <summary>
    ///     Extracts function parameters from a JSON schema
    /// </summary>
    /// <param name="inputSchema">The input schema</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>Collection of parameter contracts</returns>
    private static IList<FunctionParameterContract>? ExtractParametersFromSchema(
        object? inputSchema,
        ILogger<McpMiddleware>? logger = null
    )
    {
        if (inputSchema == null)
        {
            logger?.LogDebug("JSON schema processing: InputSchema is null, returning null parameters");
            return null;
        }

        var parameters = new List<FunctionParameterContract>();

        try
        {
            // Convert the schema to JSON element
            var schemaElement = JsonSerializer.SerializeToElement(inputSchema);
            logger?.LogDebug(
                "JSON schema processing: Schema serialized, ValueKind={ValueKind}",
                schemaElement.ValueKind
            );

            // Check if it's a proper JSON schema with properties
            if (
                schemaElement.ValueKind == JsonValueKind.Object
                && schemaElement.TryGetProperty("properties", out var propertiesElement)
                && propertiesElement.ValueKind == JsonValueKind.Object
            )
            {
                logger?.LogDebug(
                    "JSON schema processing: Found properties object with {PropertyCount} properties",
                    propertiesElement.EnumerateObject().Count()
                );

                // Process each property as a parameter
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    var paramName = property.Name;
                    var paramDescription = string.Empty;
                    var paramType = typeof(string); // Default type
                    var isRequired = false;

                    // Extract parameter description
                    if (
                        property.Value.TryGetProperty("description", out var descriptionElement)
                        && descriptionElement.ValueKind == JsonValueKind.String
                    )
                    {
                        paramDescription = descriptionElement.GetString() ?? string.Empty;
                    }

                    // Extract parameter type
                    if (
                        property.Value.TryGetProperty("type", out var typeElement)
                        && typeElement.ValueKind == JsonValueKind.String
                    )
                    {
                        var typeStr = typeElement.GetString();
                        paramType = GetTypeFromJsonSchemaType(typeStr);
                    }

                    // Check if parameter is required
                    if (
                        schemaElement.TryGetProperty("required", out var requiredElement)
                        && requiredElement.ValueKind == JsonValueKind.Array
                    )
                    {
                        isRequired = requiredElement
                            .EnumerateArray()
                            .Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == paramName);
                    }

                    logger?.LogDebug(
                        "Parameter extracted from schema: Name={ParameterName}, Type={ParameterType}, Required={IsRequired}",
                        paramName,
                        paramType.Name,
                        isRequired
                    );

                    parameters.Add(
                        new FunctionParameterContract
                        {
                            Name = paramName,
                            Description = paramDescription,
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(paramType),
                            IsRequired = isRequired,
                        }
                    );
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug("JSON schema processing failed: Error={Error}", ex.Message);
            logger?.LogError(
                ex,
                "Function contract extraction failed: SchemaType={SchemaType}, SchemaContent={SchemaContent}",
                inputSchema?.GetType().Name ?? "null",
                JsonSerializer.Serialize(inputSchema)
            );
            // Log the error or handle it as needed
            Console.Error.WriteLine($"Failed to extract parameters from input schema: {ex.Message}");
        }

        logger?.LogDebug(
            "JSON schema processing completed: ExtractedParameterCount={ParameterCount}",
            parameters.Count
        );
        return parameters;
    }

    /// <summary>
    ///     Maps JSON Schema types to .NET types
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
            _ => typeof(object),
        };
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_-]")]
    private static partial Regex MyRegex();
}
