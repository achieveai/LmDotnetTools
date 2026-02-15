using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore.DynamicDescriptions;

/// <summary>
/// Extension methods for configuring dynamic MCP tool descriptions.
/// </summary>
public static class DynamicDescriptionExtensions
{
    /// <summary>
    /// Adds the infrastructure for dynamic MCP tool descriptions.
    /// Call this before <see cref="UseDynamicToolDescriptions"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMcpDynamicDescriptions(this IServiceCollection services)
    {
        _ = services.AddHttpContextAccessor();
        services.TryAddSingleton<IToolDescriptionContextResolver, HttpHeaderContextResolver>();
        return services;
    }

    /// <summary>
    /// Registers a tool description provider for dynamic descriptions.
    /// </summary>
    /// <typeparam name="TProvider">The provider type implementing <see cref="IToolDescriptionProvider"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddToolDescriptionProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IToolDescriptionProvider
    {
        _ = services.AddSingleton<IToolDescriptionProvider, TProvider>();
        return services;
    }

    /// <summary>
    /// Registers a tool description provider instance for dynamic descriptions.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="provider">The provider instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddToolDescriptionProvider(
        this IServiceCollection services,
        IToolDescriptionProvider provider)
    {
        _ = services.AddSingleton(provider);
        return services;
    }

    /// <summary>
    /// Enables dynamic tool descriptions by intercepting the MCP ListToolsHandler.
    /// Must be called after <see cref="AddMcpDynamicDescriptions"/> and after MCP server registration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection UseDynamicToolDescriptions(this IServiceCollection services)
    {
        _ = services.AddOptions<McpServerOptions>()
            .PostConfigure<IServiceProvider>((options, sp) =>
            {
                var originalHandler = options.Handlers?.ListToolsHandler;
                if (originalHandler == null)
                {
                    // No handler registered yet, nothing to intercept
                    return;
                }

                var logger = sp.GetService<ILogger<McpServerOptions>>();

                options.Handlers!.ListToolsHandler = async (request, cancellationToken) =>
                {
                    // Get original tools first
                    var result = await originalHandler(request, cancellationToken);

                    // Resolve description providers and context
                    var contextResolver = sp.GetService<IToolDescriptionContextResolver>();
                    var providers = sp.GetServices<IToolDescriptionProvider>()
                        .OrderBy(p => p.Priority)
                        .ToList();

                    if (providers.Count == 0)
                    {
                        // No providers registered, return original result
                        return result;
                    }

                    var contextKey = contextResolver?.GetContextKey();

                    logger?.LogDebug(
                        "Dynamic descriptions: contextKey={ContextKey}, providers={ProviderCount}",
                        contextKey ?? "(none)",
                        providers.Count);

                    // Modify tool descriptions based on providers
                    var modifiedTools = result.Tools.Select(tool =>
                    {
                        var provider = providers.FirstOrDefault(p => p.SupportsToolName(tool.Name));
                        if (provider == null)
                        {
                            return tool;
                        }

                        // Get dynamic tool description
                        var newDescription = provider.GetToolDescription(tool.Name, contextKey);

                        // Modify input schema descriptions
                        var newSchema = ModifySchemaDescriptions(
                            tool.InputSchema,
                            tool.Name,
                            provider,
                            contextKey,
                            logger);

                        return new Tool
                        {
                            Name = tool.Name,
                            Description = newDescription ?? tool.Description,
                            InputSchema = newSchema
                        };
                    }).ToList();

                    logger?.LogDebug("Dynamic descriptions applied to {Count} tools", modifiedTools.Count);

                    return new ListToolsResult { Tools = modifiedTools };
                };
            });

        return services;
    }

    /// <summary>
    /// Modifies parameter descriptions in the JSON schema based on the provider.
    /// </summary>
    private static JsonElement ModifySchemaDescriptions(
        JsonElement originalSchema,
        string toolName,
        IToolDescriptionProvider provider,
        string? contextKey,
        ILogger? logger)
    {
        try
        {
            // Parse the original schema
            var schemaJson = originalSchema.GetRawText();
            var schemaNode = JsonNode.Parse(schemaJson);

            if (schemaNode is not JsonObject schemaObj)
            {
                return originalSchema;
            }

            // Get the properties object
            if (!schemaObj.TryGetPropertyValue("properties", out var propertiesNode) ||
                propertiesNode is not JsonObject propertiesObj)
            {
                return originalSchema;
            }

            // Modify each property's description
            var modified = false;
            foreach (var property in propertiesObj.ToList())
            {
                var paramName = property.Key;
                var paramNode = property.Value;

                if (paramNode is not JsonObject paramObj)
                {
                    continue;
                }

                var newDescription = provider.GetParameterDescription(toolName, paramName, contextKey);
                if (newDescription != null)
                {
                    paramObj["description"] = newDescription;
                    modified = true;

                    logger?.LogTrace(
                        "Updated description for {ToolName}.{ParamName}",
                        toolName,
                        paramName);
                }
            }

            if (!modified)
            {
                return originalSchema;
            }

            // Serialize back to JsonElement
            var modifiedJson = schemaObj.ToJsonString();
            return JsonSerializer.Deserialize<JsonElement>(modifiedJson);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Failed to modify schema descriptions for tool {ToolName}",
                toolName);
            return originalSchema;
        }
    }

    /// <summary>
    /// Registers MCP tools from the specified assembly with dynamic description support.
    /// This method replaces <c>WithToolsFromAssembly()</c> - do NOT use both together.
    /// Descriptions are resolved per-request based on context (e.g., X-Exam-Type header).
    /// </summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="assembly">The assembly to scan for [McpServerToolType] classes.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// This method scans for classes with [McpServerToolType] and methods with [McpServerTool].
    /// At request time, it queries registered IToolDescriptionProvider instances for
    /// context-specific descriptions, falling back to [Description] attributes.
    /// </remarks>
    public static IMcpServerBuilder WithDynamicToolsFromAssembly(
        this IMcpServerBuilder builder,
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(assembly);

        // Scan assembly at registration time
        var metadataCache = McpToolMetadataCache.ScanAssembly(assembly);

        // Register the cache as a singleton for potential future use
        _ = builder.Services.AddSingleton(metadataCache);

        // Configure MCP server options with our custom handlers
        _ = builder.Services.AddOptions<McpServerOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                var logger = sp.GetService<ILogger<McpServerOptions>>();

                logger?.LogInformation(
                    "Configuring MCP with dynamic descriptions: {ToolCount} tools from {Assembly}",
                    metadataCache.Tools.Count,
                    assembly.GetName().Name);

                options.Handlers = new McpServerHandlers
                {
                    // Use request.Services for scoped access to IHttpContextAccessor
                    // Fall back to root sp if request.Services is null
                    ListToolsHandler = (request, cancellationToken) =>
                        HandleListToolsAsync(request.Services ?? sp, metadataCache, logger),

                    CallToolHandler = async (request, cancellationToken) =>
                        await HandleCallToolAsync(request.Services ?? sp, metadataCache, request.Params, logger, cancellationToken)
                };
            });

        return builder;
    }

    /// <summary>
    /// Handles ListTools requests with dynamic description resolution.
    /// </summary>
    private static ValueTask<ListToolsResult> HandleListToolsAsync(
        IServiceProvider sp,
        McpToolMetadataCache metadataCache,
        ILogger? logger)
    {
        // Get context key from resolver (e.g., X-Exam-Type header)
        var contextResolver = sp.GetService<IToolDescriptionContextResolver>();
        var contextKey = contextResolver?.GetContextKey();

        // Get description providers
        var providers = sp.GetServices<IToolDescriptionProvider>()
            .OrderBy(p => p.Priority)
            .ToList();

        logger?.LogDebug(
            "ListTools: contextKey={ContextKey}, providers={ProviderCount}, tools={ToolCount}",
            contextKey ?? "(none)",
            providers.Count,
            metadataCache.Tools.Count);

        var tools = new List<Tool>();

        foreach (var toolMeta in metadataCache.Tools)
        {
            // Find provider for this tool
            var provider = providers.FirstOrDefault(p => p.SupportsToolName(toolMeta.Name));

            // Get tool description (dynamic or fallback to default)
            var description = provider?.GetToolDescription(toolMeta.Name, contextKey)
                ?? toolMeta.DefaultDescription
                ?? $"Tool: {toolMeta.Name}";

            // Build input schema with dynamic parameter descriptions
            var inputSchema = BuildDynamicInputSchema(toolMeta, provider, contextKey, logger);

            tools.Add(new Tool
            {
                Name = toolMeta.Name,
                Description = description,
                InputSchema = JsonSerializer.Deserialize<JsonElement>(inputSchema.ToJsonString())
            });
        }

        logger?.LogDebug("Returning {Count} tools with dynamic descriptions", tools.Count);

        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    /// <summary>
    /// Builds input schema with dynamic parameter descriptions.
    /// </summary>
    private static JsonObject BuildDynamicInputSchema(
        ToolMetadata toolMeta,
        IToolDescriptionProvider? provider,
        string? contextKey,
        ILogger? logger)
    {
        // Clone the base schema
        var schema = JsonNode.Parse(toolMeta.InputSchema.ToJsonString()) as JsonObject
            ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() };

        if (provider == null)
        {
            return schema;
        }

        // Update parameter descriptions from provider
        if (schema.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject props)
        {
            foreach (var param in toolMeta.Parameters)
            {
                var dynamicDesc = provider.GetParameterDescription(toolMeta.Name, param.Name, contextKey);
                if (dynamicDesc != null && props.TryGetPropertyValue(param.Name, out var paramNode) && paramNode is JsonObject paramObj)
                {
                    paramObj["description"] = dynamicDesc;
                    logger?.LogTrace(
                        "Applied dynamic description for {ToolName}.{ParamName}",
                        toolMeta.Name,
                        param.Name);
                }
            }
        }

        return schema;
    }

    /// <summary>
    /// Handles CallTool requests by invoking the appropriate method.
    /// </summary>
    private static async ValueTask<CallToolResult> HandleCallToolAsync(
        IServiceProvider sp,
        McpToolMetadataCache metadataCache,
        CallToolRequestParams? requestParams,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (requestParams is null)
        {
            logger?.LogError("CallTool request has null Params");
            return CreateErrorResult("Invalid request: missing parameters");
        }

        var toolName = requestParams.Name;

        if (string.IsNullOrEmpty(toolName))
        {
            logger?.LogError("CallTool request missing tool name");
            return CreateErrorResult("Invalid request: missing tool name");
        }

        var toolMeta = metadataCache.GetTool(toolName);
        if (toolMeta == null)
        {
            logger?.LogError("Tool '{ToolName}' not found", toolName);
            return CreateErrorResult($"Tool '{toolName}' not found");
        }

        logger?.LogInformation("Calling tool '{ToolName}'", toolName);

        try
        {
            // Get arguments from request - convert dictionary to JsonElement
            JsonElement? arguments = null;
            if (requestParams.Arguments != null)
            {
                var argsJson = JsonSerializer.Serialize(requestParams.Arguments);
                arguments = JsonSerializer.Deserialize<JsonElement>(argsJson);
            }

            // Resolve tool instance (for instance methods)
            object? toolInstance = null;
            if (!toolMeta.Method.IsStatic)
            {
                // Try to resolve from DI container using a scope
                using var scope = sp.CreateScope();
                toolInstance = scope.ServiceProvider.GetService(toolMeta.DeclaringType);

                if (toolInstance == null)
                {
                    logger?.LogError(
                        "Cannot resolve instance of {Type} for tool '{ToolName}'. Ensure it's registered in DI.",
                        toolMeta.DeclaringType.Name,
                        toolName);
                    return CreateErrorResult($"Cannot resolve tool instance for '{toolName}'");
                }

                // Invoke the method with the scoped instance
                return await InvokeToolMethodAsync(toolMeta, toolInstance, arguments, cancellationToken, logger);
            }

            // Static method - invoke directly
            return await InvokeToolMethodAsync(toolMeta, null, arguments, cancellationToken, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error executing tool '{ToolName}'", toolName);
            return CreateErrorResult($"Error executing tool: {ex.Message}");
        }
    }

    /// <summary>
    /// Invokes a tool method with the given arguments.
    /// </summary>
    private static async ValueTask<CallToolResult> InvokeToolMethodAsync(
        ToolMetadata toolMeta,
        object? instance,
        JsonElement? arguments,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        var methodParams = toolMeta.Method.GetParameters();
        var invokeArgs = new object?[methodParams.Length];

        for (var i = 0; i < methodParams.Length; i++)
        {
            var param = methodParams[i];

            // Handle CancellationToken specially
            if (param.ParameterType == typeof(CancellationToken))
            {
                invokeArgs[i] = cancellationToken;
                continue;
            }

            // Try to get value from arguments
            object? value = null;
            if (arguments.HasValue && arguments.Value.TryGetProperty(param.Name!, out var argElement))
            {
                value = DeserializeArgument(argElement, param.ParameterType);
            }
            else if (param.HasDefaultValue)
            {
                value = param.DefaultValue;
            }
            else if (IsNullableParameter(param))
            {
                value = null;
            }
            else
            {
                logger?.LogError(
                    "Missing required argument '{ParamName}' for tool '{ToolName}'",
                    param.Name,
                    toolMeta.Name);
                return CreateErrorResult($"Missing required argument: {param.Name}");
            }

            invokeArgs[i] = value;
        }

        // Invoke the method
        var result = toolMeta.Method.Invoke(instance, invokeArgs);

        // Handle async methods
        if (result is Task task)
        {
            await task.ConfigureAwait(false);

            // Get the result if it's a Task<T>
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                var resultProperty = taskType.GetProperty("Result");
                result = resultProperty?.GetValue(task);
            }
            else
            {
                result = null;
            }
        }
        else if (result is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            result = null;
        }

        // If the result is already a CallToolResult, return it directly
        if (result is CallToolResult callToolResult)
        {
            return callToolResult;
        }

        // Otherwise wrap in a success result
        var jsonResult = result != null
            ? JsonSerializer.Serialize(result)
            : "{}";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = jsonResult }],
            IsError = false
        };
    }

    /// <summary>
    /// Deserializes a JSON element to the target type.
    /// </summary>
    private static object? DeserializeArgument(JsonElement element, Type targetType)
    {
        try
        {
            return JsonSerializer.Deserialize(element.GetRawText(), targetType);
        }
        catch
        {
            // Try simple conversions
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when targetType == typeof(int) => element.GetInt32(),
                JsonValueKind.Number when targetType == typeof(long) => element.GetInt64(),
                JsonValueKind.Number when targetType == typeof(double) => element.GetDouble(),
                JsonValueKind.Number when targetType == typeof(float) => element.GetSingle(),
                JsonValueKind.Number when targetType == typeof(decimal) => element.GetDecimal(),
                JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
                _ => null
            };
        }
    }

    /// <summary>
    /// Checks if a parameter is nullable.
    /// </summary>
    private static bool IsNullableParameter(ParameterInfo param)
    {
        if (Nullable.GetUnderlyingType(param.ParameterType) != null)
        {
            return true;
        }

        var nullabilityContext = new NullabilityInfoContext();
        var nullabilityInfo = nullabilityContext.Create(param);
        return nullabilityInfo.WriteState == NullabilityState.Nullable;
    }

    /// <summary>
    /// Creates an error CallToolResult.
    /// </summary>
    private static CallToolResult CreateErrorResult(string message)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Error: {message}" }],
            IsError = true
        };
    }
}
