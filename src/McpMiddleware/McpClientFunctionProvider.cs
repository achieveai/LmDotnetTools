using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
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
///     Function provider that provides functions from MCP clients
/// </summary>
public partial class McpClientFunctionProvider : IFunctionProvider
{
    private readonly List<FunctionDescriptor> _functions;
    private readonly ILogger<McpClientFunctionProvider> _logger;
    private readonly Dictionary<string, McpClient> _mcpClients;

    /// <summary>
    ///     Private constructor for async factory pattern
    /// </summary>
    private McpClientFunctionProvider(
        Dictionary<string, McpClient> mcpClients,
        List<FunctionDescriptor> functions,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null
    )
    {
        _mcpClients = mcpClients;
        _functions = functions;
        ProviderName = providerName ?? "McpClient";
        _logger = logger ?? NullLogger<McpClientFunctionProvider>.Instance;
    }

    public string ProviderName { get; }

    /// <summary>
    ///     MCP client functions have medium priority (100)
    /// </summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        return _functions;
    }

    /// <summary>
    ///     Creates a new instance of McpClientFunctionProvider asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new instance of McpClientFunctionProvider</returns>
    public static async Task<McpClientFunctionProvider> CreateAsync(
        Dictionary<string, McpClient> mcpClients,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mcpClients);

        logger ??= NullLogger<McpClientFunctionProvider>.Instance;

        logger.LogInformation(
            "Creating MCP client function provider: ClientCount={ClientCount}, ClientIds={ClientIds}",
            mcpClients.Count,
            string.Join(", ", mcpClients.Keys)
        );

        // Extract function contracts and create handlers
        var functionContracts = await ExtractFunctionContractsAsync(mcpClients, logger, cancellationToken);
        var functionMap = await CreateFunctionMapAsync(mcpClients, logger, cancellationToken);

        // Create function descriptors
        var functions = new List<FunctionDescriptor>();
        foreach (var contract in functionContracts)
        {
            var key = contract.ClassName != null ? $"{contract.ClassName}-{contract.Name}" : contract.Name;
            if (functionMap.TryGetValue(key, out var handler))
            {
                functions.Add(
                    new FunctionDescriptor
                    {
                        Contract = contract,
                        Handler = handler,
                        ProviderName = providerName ?? "McpClient",
                    }
                );
            }
        }

        logger.LogInformation(
            "MCP client function provider created: FunctionCount={FunctionCount}, FunctionNames={FunctionNames}",
            functions.Count,
            string.Join(", ", functions.Select(f => f.Contract.Name))
        );

        return new McpClientFunctionProvider(mcpClients, functions, providerName, logger);
    }

    /// <summary>
    ///     Creates a new instance from a single MCP client
    /// </summary>
    /// <param name="mcpClient">The MCP client</param>
    /// <param name="clientId">Client identifier</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new instance of McpClientFunctionProvider</returns>
    public static async Task<McpClientFunctionProvider> CreateAsync(
        McpClient mcpClient,
        string clientId,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        var clients = new Dictionary<string, McpClient> { { clientId, mcpClient } };
        return await CreateAsync(clients, providerName, logger, cancellationToken);
    }

    /// <summary>
    ///     Creates a new instance with configuration for tool filtering and collision handling
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="toolFilterConfig">Tool filtering configuration</param>
    /// <param name="serverConfigs">Per-server configurations</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new instance of McpClientFunctionProvider</returns>
    public static async Task<McpClientFunctionProvider> CreateAsync(
        Dictionary<string, McpClient> mcpClients,
        FunctionFilterConfig? toolFilterConfig,
        Dictionary<string, ProviderFilterConfig>? serverConfigs,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mcpClients);

        logger ??= NullLogger<McpClientFunctionProvider>.Instance;

        logger.LogInformation(
            "Creating MCP client function provider with configuration: ClientCount={ClientCount}, ClientIds={ClientIds}, FilteringEnabled={FilteringEnabled}",
            mcpClients.Count,
            string.Join(", ", mcpClients.Keys),
            toolFilterConfig?.EnableFiltering ?? false
        );

        // Get tools from all clients
        var toolsByServer = new Dictionary<string, List<McpClientTool>>();
        foreach (var (serverId, client) in mcpClients)
        {
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                toolsByServer[serverId] = [.. tools];
                logger.LogDebug("Retrieved tools for server {ServerId}: ToolCount={ToolCount}", serverId, tools.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list tools for MCP client: ClientId={ClientId}", serverId);
                toolsByServer[serverId] = [];
            }
        }

        // Use collision detector to resolve naming
        // Convert MCP tools to function descriptors
        var descriptors = new List<FunctionDescriptor>();
        var toolToDescriptorMap = new Dictionary<(string serverId, string toolName), FunctionDescriptor>();

        foreach (var (serverId, tools) in toolsByServer)
        {
            foreach (var tool in tools)
            {
                var descriptor = new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = tool.Name,
                        Description = tool.Description ?? string.Empty,
                    },
                    Handler = _ => Task.FromResult(string.Empty), // Dummy handler
                    ProviderName = serverId,
                };

                descriptors.Add(descriptor);
                toolToDescriptorMap[(serverId, tool.Name)] = descriptor;
            }
        }

        // Use the generalized collision detector
        var collisionConfig = new FunctionFilterConfig
        {
            UsePrefixOnlyForCollisions = toolFilterConfig?.UsePrefixOnlyForCollisions ?? true,
        };

        var collisionDetector = new FunctionCollisionDetector(logger);
        var descriptorNamingMap = collisionDetector.DetectAndResolveCollisions(descriptors, collisionConfig);

        // Convert back to the expected format
        var namingMap = new Dictionary<(string serverId, string toolName), string>();
        foreach (var ((serverId, toolName), descriptor) in toolToDescriptorMap)
        {
            if (descriptorNamingMap.TryGetValue(descriptor.Key, out var registeredName))
            {
                namingMap[(serverId, toolName)] = registeredName;
            }
        }

        // Apply filtering if configured
        FunctionFilter? toolFilter = null;
        if (toolFilterConfig?.EnableFiltering == true)
        {
            // Convert server configs to provider configs if needed
            if (serverConfigs != null && serverConfigs.Count > 0)
            {
                toolFilterConfig.ProviderConfigs = serverConfigs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            toolFilter = new FunctionFilter(toolFilterConfig, logger);
        }

        // Extract function contracts and create handlers using naming map
        var functionContracts = await ExtractFunctionContractsWithNamingMapAsync(
            mcpClients,
            toolsByServer,
            namingMap,
            toolFilter,
            logger,
            cancellationToken
        );
        var functionMap = await CreateFunctionMapWithNamingMapAsync(
            mcpClients,
            toolsByServer,
            namingMap,
            toolFilter,
            logger,
            cancellationToken
        );

        // Create function descriptors
        var functions = new List<FunctionDescriptor>();
        foreach (var contract in functionContracts)
        {
            var key = contract.ClassName != null ? $"{contract.ClassName}-{contract.Name}" : contract.Name;
            if (functionMap.TryGetValue(key, out var handler))
            {
                functions.Add(
                    new FunctionDescriptor
                    {
                        Contract = contract,
                        Handler = handler,
                        ProviderName = providerName ?? "McpClient",
                    }
                );
            }
        }

        logger.LogInformation(
            "MCP client function provider created with configuration: FunctionCount={FunctionCount}, FunctionNames={FunctionNames}",
            functions.Count,
            string.Join(", ", functions.Select(f => f.Contract.Name))
        );

        return new McpClientFunctionProvider(mcpClients, functions, providerName, logger);
    }

    /// <summary>
    ///     Extracts function contracts from MCP client tools
    ///     (Reused from McpMiddleware with minor adaptations)
    /// </summary>
    private static async Task<IEnumerable<FunctionContract>> ExtractFunctionContractsAsync(
        Dictionary<string, McpClient> mcpClients,
        ILogger<McpClientFunctionProvider> logger,
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

                        logger.LogDebug(
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
    ///     Creates function delegates for the MCP clients asynchronously
    ///     (Reused from McpMiddleware with minor adaptations)
    /// </summary>
    private static async Task<IDictionary<string, Func<string, Task<string>>>> CreateFunctionMapAsync(
        Dictionary<string, McpClient> mcpClients,
        ILogger<McpClientFunctionProvider> logger,
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

                logger.LogDebug(
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
                            logger.LogDebug(
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
    ///     Creates a multi-modal function map that returns ToolCallResult with content blocks.
    ///     This preserves image content from MCP tool responses.
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients keyed by client ID</param>
    /// <param name="logger">Logger for debugging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A function map that returns ToolCallResult with text and image content blocks</returns>
    public static async Task<IDictionary<string, Func<string, Task<ToolCallResult>>>> CreateMultiModalFunctionMapAsync(
        Dictionary<string, McpClient> mcpClients,
        ILogger<McpClientFunctionProvider> logger,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mcpClients);

        var functionMap = new Dictionary<string, Func<string, Task<ToolCallResult>>>();

        logger.LogDebug(
            "Creating multi-modal function map: ClientCount={ClientCount}, ClientIds={ClientIds}",
            mcpClients.Count,
            string.Join(", ", mcpClients.Keys)
        );

        foreach (var kvp in mcpClients)
        {
            var clientId = kvp.Key;
            var client = kvp.Value;

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

                logger.LogDebug(
                    "MCP multi-modal tool discovery: ClientId={ClientId}, ToolCount={ToolCount}",
                    clientId,
                    tools.Count
                );

                foreach (var tool in tools)
                {
                    var sanitizedClientId = SanitizeToolName(kvp.Key);
                    var sanitizedToolName = SanitizeToolName(tool.Name);
                    var functionName = $"{sanitizedClientId}-{sanitizedToolName}";

                    functionMap[functionName] = async argsJson =>
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            // Parse arguments from JSON
                            Dictionary<string, object?> args;
                            try
                            {
                                args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? [];
                            }
                            catch (JsonException jsonEx)
                            {
                                logger.LogError(jsonEx, "JSON parsing failed for multi-modal tool: {ToolName}", tool.Name);
                                throw;
                            }

                            // Call the MCP tool
                            var response = await client.CallToolAsync(tool.Name, args);

                            // Extract text content
                            var textResult = string.Join(
                                Environment.NewLine,
                                response.Content != null
                                    ? response.Content
                                        .Where(c => c?.Type == "text")
                                        .Select(c => c is TextContentBlock tb ? tb.Text : string.Empty)
                                    : []
                            );

                            // Extract image content blocks with MIME type detection from bytes
                            // Use fully qualified name to avoid ambiguity with MCP's ToolResultContentBlock
                            var imageBlocks = new List<LmCore.Messages.ToolResultContentBlock>();
                            var imageBlocksInResponse = response.Content?.Count(c => c?.Type == "image") ?? 0;
                            logger.LogTrace(
                                "MCP response content analysis: ToolName={ToolName}, TotalBlocks={TotalBlocks}, ImageBlocks={ImageBlocks}",
                                tool.Name,
                                response.Content?.Count ?? 0,
                                imageBlocksInResponse);

                            if (response.Content != null)
                            {
                                var imageIndex = 0;
                                foreach (var content in response.Content.Where(c => c?.Type == "image"))
                                {
                                    if (content is ImageContentBlock imgBlock && !string.IsNullOrEmpty(imgBlock.Data))
                                    {
                                        try
                                        {
                                            // Detect MIME type from actual bytes (don't trust the header)
                                            var bytes = Convert.FromBase64String(imgBlock.Data);
                                            var detectedMimeType = DetectImageMimeType(bytes, imgBlock.MimeType, logger);

                                            // Log MIME type correction at info level for visibility
                                            if (detectedMimeType != imgBlock.MimeType)
                                            {
                                                logger.LogInformation(
                                                    "MIME type corrected in MCP response: ToolName={ToolName}, ImageIndex={ImageIndex}, Header={HeaderMimeType}, Detected={DetectedMimeType}, ByteLength={ByteLength}",
                                                    tool.Name,
                                                    imageIndex,
                                                    imgBlock.MimeType,
                                                    detectedMimeType,
                                                    bytes.Length);
                                            }
                                            else
                                            {
                                                logger.LogTrace(
                                                    "Image processed: ToolName={ToolName}, ImageIndex={ImageIndex}, MimeType={MimeType}, ByteLength={ByteLength}",
                                                    tool.Name,
                                                    imageIndex,
                                                    detectedMimeType,
                                                    bytes.Length);
                                            }

                                            imageBlocks.Add(new ImageToolResultBlock
                                            {
                                                Data = imgBlock.Data,
                                                MimeType = detectedMimeType
                                            });
                                        }
                                        catch (FormatException ex)
                                        {
                                            logger.LogWarning(
                                                ex,
                                                "Invalid base64 data in MCP image response: ToolName={ToolName}, ImageIndex={ImageIndex}, DataLength={DataLength}",
                                                tool.Name,
                                                imageIndex,
                                                imgBlock.Data?.Length ?? 0);
                                        }
                                        catch (Exception ex)
                                        {
                                            logger.LogWarning(
                                                ex,
                                                "Failed to process image from MCP response: ToolName={ToolName}, ImageIndex={ImageIndex}",
                                                tool.Name,
                                                imageIndex);
                                        }
                                    }
                                    else
                                    {
                                        logger.LogDebug(
                                            "Skipping image block with missing data: ToolName={ToolName}, ImageIndex={ImageIndex}, IsImageContentBlock={IsImageContentBlock}",
                                            tool.Name,
                                            imageIndex,
                                            content is ImageContentBlock);
                                    }
                                    imageIndex++;
                                }
                            }

                            stopwatch.Stop();
                            logger.LogDebug(
                                "Multi-modal MCP tool execution completed: ToolName={ToolName}, Duration={Duration}ms, ImageCount={ImageCount}",
                                tool.Name,
                                stopwatch.ElapsedMilliseconds,
                                imageBlocks.Count
                            );

                            return new ToolCallResult(
                                null, // ToolCallId will be set by executor
                                textResult,
                                imageBlocks.Count > 0 ? imageBlocks : null
                            );
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            logger.LogError(
                                ex,
                                "Multi-modal MCP tool execution failed: ToolName={ToolName}, Duration={Duration}ms",
                                tool.Name,
                                stopwatch.ElapsedMilliseconds
                            );

                            return new ToolCallResult(null, $"Error executing MCP tool {tool.Name}: {ex.Message}");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MCP client multi-modal tool discovery failed: ClientId={ClientId}", clientId);
            }
        }

        return functionMap;
    }

    /// <summary>
    ///     Detects the MIME type of an image from its byte content.
    ///     Uses magic bytes to identify common image formats.
    /// </summary>
    private static string DetectImageMimeType(byte[] bytes, string fallbackMimeType, ILogger logger)
    {
        if (bytes.Length >= 8)
        {
            // PNG: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            // GIF: 47 49 46 38
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return "image/gif";
            }

            // WebP: 52 49 46 46 ... 57 45 42 50
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }
        }

        // Log if we're using fallback
        if (!string.IsNullOrEmpty(fallbackMimeType))
        {
            logger.LogDebug(
                "Could not detect MIME type from bytes, using fallback: {FallbackMimeType}",
                fallbackMimeType
            );
            return fallbackMimeType;
        }

        logger.LogWarning("Could not detect MIME type from bytes and no fallback provided");
        return "application/octet-stream";
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
    ///     (Reused from McpMiddleware)
    /// </summary>
    private static FunctionContract ConvertToFunctionContract(
        string clientName,
        McpClientTool tool,
        ILogger<McpClientFunctionProvider>? logger = null
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
    ///     (Reused from McpMiddleware)
    /// </summary>
    private static IList<FunctionParameterContract>? ExtractParametersFromSchema(
        object? inputSchema,
        ILogger<McpClientFunctionProvider>? logger = null
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
    ///     (Reused from McpMiddleware)
    /// </summary>
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

    /// <summary>
    ///     Extracts function contracts using the naming map from collision detection
    /// </summary>
    private static Task<IEnumerable<FunctionContract>> ExtractFunctionContractsWithNamingMapAsync(
        Dictionary<string, McpClient> mcpClients,
        Dictionary<string, List<McpClientTool>> toolsByServer,
        Dictionary<(string serverId, string toolName), string> namingMap,
        FunctionFilter? toolFilter,
        ILogger<McpClientFunctionProvider> logger,
        CancellationToken cancellationToken = default
    )
    {
        var functionContracts = new List<FunctionContract>();

        foreach (var (serverId, tools) in toolsByServer)
        {
            foreach (var tool in tools)
            {
                try
                {
                    // Get the registered name from the naming map
                    if (!namingMap.TryGetValue((serverId, tool.Name), out var registeredName))
                    {
                        logger.LogWarning(
                            "Tool not found in naming map: ServerId={ServerId}, ToolName={ToolName}",
                            serverId,
                            tool.Name
                        );
                        continue;
                    }

                    // Apply filtering if configured
                    if (toolFilter != null)
                    {
                        var descriptor = new FunctionDescriptor
                        {
                            Contract = new FunctionContract { Name = tool.Name },
                            Handler = _ => Task.FromResult(string.Empty), // Dummy handler
                            ProviderName = serverId,
                        };

                        if (toolFilter.ShouldFilterFunctionWithReason(descriptor, registeredName).IsFiltered)
                        {
                            logger.LogDebug(
                                "Tool filtered out: ServerId={ServerId}, ToolName={ToolName}, RegisteredName={RegisteredName}",
                                serverId,
                                tool.Name,
                                registeredName
                            );
                            continue;
                        }
                    }

                    var contract = new FunctionContract
                    {
                        Name = registeredName,
                        Description = tool.Description,
                        Parameters = ExtractParametersFromSchema(tool.JsonSchema, logger),
                    };

                    functionContracts.Add(contract);
                    logger.LogDebug(
                        "Function contract extracted: FunctionName={FunctionName}, ServerId={ServerId}, ParameterCount={ParameterCount}",
                        contract.Name,
                        serverId,
                        contract.Parameters?.Count() ?? 0
                    );
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Function contract extraction failed for tool: ServerId={ServerId}, ToolName={ToolName}",
                        serverId,
                        tool.Name
                    );
                }
            }
        }

        return Task.FromResult<IEnumerable<FunctionContract>>(functionContracts);
    }

    /// <summary>
    ///     Creates function delegates using the naming map from collision detection
    /// </summary>
    private static Task<IDictionary<string, Func<string, Task<string>>>> CreateFunctionMapWithNamingMapAsync(
        Dictionary<string, McpClient> mcpClients,
        Dictionary<string, List<McpClientTool>> toolsByServer,
        Dictionary<(string serverId, string toolName), string> namingMap,
        FunctionFilter? toolFilter,
        ILogger<McpClientFunctionProvider> logger,
        CancellationToken cancellationToken = default
    )
    {
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();

        foreach (var (serverId, tools) in toolsByServer)
        {
            if (!mcpClients.TryGetValue(serverId, out var client))
            {
                logger.LogWarning("Client not found for server: ServerId={ServerId}", serverId);
                continue;
            }

            foreach (var tool in tools)
            {
                // Get the registered name from the naming map
                if (!namingMap.TryGetValue((serverId, tool.Name), out var registeredName))
                {
                    logger.LogWarning(
                        "Tool not found in naming map: ServerId={ServerId}, ToolName={ToolName}",
                        serverId,
                        tool.Name
                    );
                    continue;
                }

                // Apply filtering if configured
                if (toolFilter != null)
                {
                    var descriptor = new FunctionDescriptor
                    {
                        Contract = new FunctionContract { Name = tool.Name },
                        Handler = _ => Task.FromResult(string.Empty), // Dummy handler
                        ProviderName = serverId,
                    };

                    if (toolFilter.ShouldFilterFunctionWithReason(descriptor, registeredName).IsFiltered)
                    {
                        logger.LogDebug(
                            "Tool filtered out from function map: ServerId={ServerId}, ToolName={ToolName}, RegisteredName={RegisteredName}",
                            serverId,
                            tool.Name,
                            registeredName
                        );
                        continue;
                    }
                }

                logger.LogDebug(
                    "Mapping function to client: FunctionName={FunctionName}, ServerId={ServerId}, ToolName={ToolName}",
                    registeredName,
                    serverId,
                    tool.Name
                );

                // Create a delegate that calls the appropriate MCP client
                functionMap[registeredName] = async argsJson =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        logger.LogDebug(
                            "Tool argument parsing: ToolName={ToolName}, ServerId={ServerId}, ArgsJson={ArgsJson}",
                            tool.Name,
                            serverId,
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
                                "JSON parsing failed for tool arguments: ToolName={ToolName}, ServerId={ServerId}, InputData={InputData}",
                                tool.Name,
                                serverId,
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

                        // Call the MCP tool with the original tool name
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
                        logger.LogDebug(
                            "MCP tool execution completed: ToolName={ToolName}, ServerId={ServerId}, Duration={Duration}ms, Success={Success}, ResultLength={ResultLength}",
                            tool.Name,
                            serverId,
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
                            "Tool execution exception details: ToolName={ToolName}, ServerId={ServerId}, ExceptionType={ExceptionType}",
                            tool.Name,
                            serverId,
                            ex.GetType().Name
                        );
                        logger.LogError(
                            ex,
                            "MCP tool execution failed: ToolName={ToolName}, ServerId={ServerId}, Duration={Duration}ms, Arguments={Arguments}",
                            tool.Name,
                            serverId,
                            stopwatch.ElapsedMilliseconds,
                            argsJson
                        );

                        return $"Error executing MCP tool {tool.Name}: {ex.Message}";
                    }
                };
            }
        }

        return Task.FromResult<IDictionary<string, Func<string, Task<string>>>>(functionMap);
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_-]")]
    private static partial Regex MyRegex();
}
