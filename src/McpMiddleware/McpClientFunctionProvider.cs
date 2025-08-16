using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Function provider that provides functions from MCP clients
/// </summary>
public class McpClientFunctionProvider : IFunctionProvider
{
  private readonly Dictionary<string, IMcpClient> _mcpClients;
  private readonly List<FunctionDescriptor> _functions;
  private readonly ILogger<McpClientFunctionProvider> _logger;

  /// <summary>
  /// Private constructor for async factory pattern
  /// </summary>
  private McpClientFunctionProvider(
    Dictionary<string, IMcpClient> mcpClients,
    List<FunctionDescriptor> functions,
    string? providerName = null,
    ILogger<McpClientFunctionProvider>? logger = null)
  {
    _mcpClients = mcpClients;
    _functions = functions;
    ProviderName = providerName ?? "McpClient";
    _logger = logger ?? NullLogger<McpClientFunctionProvider>.Instance;
  }

  /// <summary>
  /// Creates a new instance of McpClientFunctionProvider asynchronously
  /// </summary>
  /// <param name="mcpClients">Dictionary of MCP clients</param>
  /// <param name="providerName">Optional provider name</param>
  /// <param name="logger">Optional logger instance</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A new instance of McpClientFunctionProvider</returns>
  public static async Task<McpClientFunctionProvider> CreateAsync(
    Dictionary<string, IMcpClient> mcpClients,
    string? providerName = null,
    ILogger<McpClientFunctionProvider>? logger = null,
    CancellationToken cancellationToken = default)
  {
    logger ??= NullLogger<McpClientFunctionProvider>.Instance;

    logger.LogInformation("Creating MCP client function provider: ClientCount={ClientCount}, ClientIds={ClientIds}",
      mcpClients.Count, string.Join(", ", mcpClients.Keys));

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
        functions.Add(new FunctionDescriptor
        {
          Contract = contract,
          Handler = handler,
          ProviderName = providerName ?? "McpClient"
        });
      }
    }

    logger.LogInformation("MCP client function provider created: FunctionCount={FunctionCount}, FunctionNames={FunctionNames}",
      functions.Count, string.Join(", ", functions.Select(f => f.Contract.Name)));

    return new McpClientFunctionProvider(mcpClients, functions, providerName, logger);
  }

  /// <summary>
  /// Creates a new instance from a single MCP client
  /// </summary>
  /// <param name="mcpClient">The MCP client</param>
  /// <param name="clientId">Client identifier</param>
  /// <param name="providerName">Optional provider name</param>
  /// <param name="logger">Optional logger instance</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A new instance of McpClientFunctionProvider</returns>
  public static async Task<McpClientFunctionProvider> CreateAsync(
    IMcpClient mcpClient,
    string clientId,
    string? providerName = null,
    ILogger<McpClientFunctionProvider>? logger = null,
    CancellationToken cancellationToken = default)
  {
    var clients = new Dictionary<string, IMcpClient> { { clientId, mcpClient } };
    return await CreateAsync(clients, providerName, logger, cancellationToken);
  }

  public string ProviderName { get; }

  /// <summary>
  /// MCP client functions have medium priority (100)
  /// </summary>
  public int Priority => 100;

  public IEnumerable<FunctionDescriptor> GetFunctions() => _functions;

  /// <summary>
  /// Extracts function contracts from MCP client tools
  /// (Reused from McpMiddleware with minor adaptations)
  /// </summary>
  private static async Task<IEnumerable<FunctionContract>> ExtractFunctionContractsAsync(
    Dictionary<string, IMcpClient> mcpClients,
    ILogger<McpClientFunctionProvider> logger,
    CancellationToken cancellationToken = default)
  {
    var functionContracts = new List<FunctionContract>();

    foreach (var kvp in mcpClients)
    {
      try
      {
        var tools = await kvp.Value.ListToolsAsync();

        foreach (var tool in tools)
        {
          try
          {
            var contract = ConvertToFunctionContract(kvp.Key, tool, logger);
            functionContracts.Add(contract);

            logger.LogDebug("Function contract extracted: FunctionName={FunctionName}, ClientId={ClientId}, ParameterCount={ParameterCount}",
              contract.Name, kvp.Key, contract.Parameters?.Count() ?? 0);
          }
          catch (Exception ex)
          {
            logger.LogError(ex, "Function contract extraction failed for tool: ClientId={ClientId}, ToolName={ToolName}",
              kvp.Key, tool.Name);
            // Continue with other tools even if one fails
            continue;
          }
        }
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Failed to list tools for MCP client: ClientId={ClientId}", kvp.Key);
        // Continue with other clients even if one fails
        continue;
      }
    }

    return functionContracts;
  }

  /// <summary>
  /// Creates function delegates for the MCP clients asynchronously
  /// (Reused from McpMiddleware with minor adaptations)
  /// </summary>
  private static async Task<IDictionary<string, Func<string, Task<string>>>> CreateFunctionMapAsync(
    Dictionary<string, IMcpClient> mcpClients,
    ILogger<McpClientFunctionProvider> logger,
    CancellationToken cancellationToken = default)
  {
    var functionMap = new Dictionary<string, Func<string, Task<string>>>();

    logger.LogDebug("Creating function map: ClientCount={ClientCount}, ClientIds={ClientIds}",
      mcpClients.Count, string.Join(", ", mcpClients.Keys));

    foreach (var kvp in mcpClients)
    {
      var clientId = kvp.Key;
      var client = kvp.Value;

      try
      {
        // Get available tools from this client asynchronously
        var tools = await client.ListToolsAsync();

        logger.LogDebug("MCP tool discovery completed: ClientId={ClientId}, ToolCount={ToolCount}, ToolNames={ToolNames}",
          clientId, tools.Count, string.Join(", ", tools.Select(t => t.Name)));

        foreach (var tool in tools)
        {
          var functionName = $"{kvp.Key}-{tool.Name}";

          logger.LogDebug("Mapping function to client: FunctionName={FunctionName}, ClientId={ClientId}, ToolName={ToolName}",
            functionName, clientId, tool.Name);

          // Create a delegate that calls the appropriate MCP client
          functionMap[functionName] = async (argsJson) =>
          {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
              logger.LogDebug("Tool argument parsing: ToolName={ToolName}, ClientId={ClientId}, ArgsJson={ArgsJson}",
                tool.Name, clientId, argsJson);

              // Parse arguments from JSON
              Dictionary<string, object?> args;
              try
              {
                args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)
                  ?? new Dictionary<string, object?>();
              }
              catch (JsonException jsonEx)
              {
                logger.LogError(jsonEx, "JSON parsing failed for tool arguments: ToolName={ToolName}, ClientId={ClientId}, InputData={InputData}",
                  tool.Name, clientId, argsJson);
                throw;
              }

              logger.LogDebug("Tool arguments parsed: ToolName={ToolName}, ArgumentCount={ArgumentCount}, ArgumentKeys={ArgumentKeys}",
                tool.Name, args.Count, string.Join(", ", args.Keys));

              // Call the MCP tool
              var response = await client.CallToolAsync(tool.Name, args);

              logger.LogDebug("Tool response received: ToolName={ToolName}, ContentCount={ContentCount}",
                tool.Name, response.Content?.Count ?? 0);

              // Extract and format text response
              string result = string.Join(Environment.NewLine,
                response.Content != null
                  ? response.Content
                    .Where(c => c?.Type == "text")
                    .Select(c => (c is TextContentBlock tb) ? tb.Text : string.Empty)
                  : Array.Empty<string>());

              logger.LogDebug("Tool response formatted: ToolName={ToolName}, ResultLength={ResultLength}",
                tool.Name, result.Length);

              stopwatch.Stop();
              logger.LogDebug("MCP tool execution completed: ToolName={ToolName}, ClientId={ClientId}, Duration={Duration}ms, Success={Success}, ResultLength={ResultLength}",
                tool.Name, clientId, stopwatch.ElapsedMilliseconds, true, result.Length);

              return result;
            }
            catch (Exception ex)
            {
              stopwatch.Stop();
              logger.LogDebug("Tool execution exception details: ToolName={ToolName}, ClientId={ClientId}, ExceptionType={ExceptionType}",
                tool.Name, clientId, ex.GetType().Name);
              logger.LogError(ex, "MCP tool execution failed: ToolName={ToolName}, ClientId={ClientId}, Duration={Duration}ms, Arguments={Arguments}",
                tool.Name, clientId, stopwatch.ElapsedMilliseconds, argsJson);

              return $"Error executing MCP tool {tool.Name}: {ex.Message}";
            }
          };
        }
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "MCP client tool discovery failed: ClientId={ClientId}", clientId);
        // Continue with other clients even if one fails
        continue;
      }
    }

    return functionMap;
  }

  /// <summary>
  /// Converts an MCP client tool to a function contract
  /// (Reused from McpMiddleware)
  /// </summary>
  private static FunctionContract ConvertToFunctionContract(
    string clientName,
    McpClientTool tool,
    ILogger<McpClientFunctionProvider>? logger = null)
  {
    return new FunctionContract
    {
      Name = $"{clientName}-{tool.Name}",
      Description = tool.Description,
      Parameters = ExtractParametersFromSchema(tool.JsonSchema, logger)
    };
  }

  /// <summary>
  /// Extracts function parameters from a JSON schema
  /// (Reused from McpMiddleware)
  /// </summary>
  private static IList<FunctionParameterContract>? ExtractParametersFromSchema(object? inputSchema, ILogger<McpClientFunctionProvider>? logger = null)
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
      logger?.LogDebug("JSON schema processing: Schema serialized, ValueKind={ValueKind}", schemaElement.ValueKind);

      // Check if it's a proper JSON schema with properties
      if (schemaElement.ValueKind == JsonValueKind.Object &&
        schemaElement.TryGetProperty("properties", out var propertiesElement) &&
        propertiesElement.ValueKind == JsonValueKind.Object)
      {
        logger?.LogDebug("JSON schema processing: Found properties object with {PropertyCount} properties",
          propertiesElement.EnumerateObject().Count());

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

          logger?.LogDebug("Parameter extracted from schema: Name={ParameterName}, Type={ParameterType}, Required={IsRequired}",
            paramName, paramType.Name, isRequired);

          parameters.Add(new FunctionParameterContract
          {
            Name = paramName,
            Description = paramDescription,
            ParameterType = SchemaHelper.CreateJsonSchemaFromType(paramType),
            IsRequired = isRequired
          });
        }
      }
    }
    catch (Exception ex)
    {
      logger?.LogDebug("JSON schema processing failed: Error={Error}", ex.Message);
      logger?.LogError(ex, "Function contract extraction failed: SchemaType={SchemaType}, SchemaContent={SchemaContent}",
        inputSchema?.GetType().Name ?? "null", JsonSerializer.Serialize(inputSchema));
      // Log the error or handle it as needed
      Console.Error.WriteLine($"Failed to extract parameters from input schema: {ex.Message}");
    }

    logger?.LogDebug("JSON schema processing completed: ExtractedParameterCount={ParameterCount}", parameters.Count);
    return parameters;
  }

  /// <summary>
  /// Maps JSON Schema types to .NET types
  /// (Reused from McpMiddleware)
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
      _ => typeof(object)
    };
  }
}
