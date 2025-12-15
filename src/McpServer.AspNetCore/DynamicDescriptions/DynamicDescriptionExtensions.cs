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
}
