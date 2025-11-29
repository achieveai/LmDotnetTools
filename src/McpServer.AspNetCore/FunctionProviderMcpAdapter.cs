using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore;

/// <summary>
/// Adapter for converting IFunctionProvider instances to MCP server handlers
/// </summary>
public static class FunctionProviderMcpAdapter
{
    /// <summary>
    /// Configures MCP server handlers from all registered IFunctionProvider instances
    /// </summary>
    internal static IServiceCollection AddMcpServerHandlers(
        this IServiceCollection services)
    {
        // Configure MCP server options with dynamic handlers using AddOptions pattern
        services.AddOptions<McpServerOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                var functionProviders = sp.GetServices<IFunctionProvider>();
                var logger = sp.GetService<ILogger<McpServerOptions>>();

                logger?.LogInformation("Configuring MCP handlers from function providers");

                // Collect all functions from all providers
                var allFunctions = functionProviders
                    .OrderBy(p => p.Priority)
                    .SelectMany(p => p.GetFunctions())
                    .ToList();

                // Build a lookup dictionary: toolName -> (contract, handler)
                var toolLookup = new Dictionary<string, (FunctionContract Contract, Func<string, Task<string>> Handler)>();

                foreach (var functionDescriptor in allFunctions)
                {
                    var toolName = GetToolName(functionDescriptor.Contract);

                    if (toolLookup.ContainsKey(toolName))
                    {
                        logger?.LogWarning(
                            "Duplicate tool name '{ToolName}' found. Skipping function from provider '{Provider}'",
                            toolName,
                            functionDescriptor.ProviderName);
                        continue;
                    }

                    toolLookup[toolName] = (functionDescriptor.Contract, functionDescriptor.Handler);

                    logger?.LogDebug(
                        "Registered MCP tool '{ToolName}' from provider '{Provider}'",
                        toolName,
                        functionDescriptor.ProviderName);
                }

                logger?.LogInformation(
                    "Registered {Count} MCP tools from {ProviderCount} function providers",
                    toolLookup.Count,
                    functionProviders.Count());

                // Create handlers
                var handlers = new McpServerHandlers
                {
                    ListToolsHandler = (request, cancellationToken) =>
                    {
                        logger?.LogDebug("ListTools request received");

                        var tools = toolLookup.Select(kvp =>
                        {
                            var (contract, _) = kvp.Value;
                            var schemaJson = BuildInputSchema(contract);
                            return new Tool
                            {
                                Name = kvp.Key,
                                Description = contract.Description,
                                InputSchema = JsonSerializer.Deserialize<JsonElement>(schemaJson.ToJsonString())
                            };
                        }).ToList();

                        logger?.LogDebug("Returning {Count} tools", tools.Count);

                        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
                    },

                    CallToolHandler = async (request, cancellationToken) =>
                    {
                        if (request.Params is null)
                        {
                            logger?.LogError("[McpAdapter] CallTool request has null Params");
                            return new CallToolResult
                            {
                                Content = [new TextContentBlock { Text = "Invalid request: missing parameters" }],
                                IsError = true
                            };
                        }

                        var toolName = request.Params.Name;

                        // Convert arguments dictionary to JSON string
                        var argumentsJson = request.Params.Arguments != null
                            ? JsonSerializer.Serialize(request.Params.Arguments)
                            : "{}";

                        logger?.LogInformation(
                            "[McpAdapter] CallTool request for '{ToolName}' with arguments: {Arguments}",
                            toolName,
                            argumentsJson);

                        if (!toolLookup.TryGetValue(toolName, out var toolInfo))
                        {
                            logger?.LogError("[McpAdapter] Tool '{ToolName}' not found", toolName);
                            return new CallToolResult
                            {
                                Content = [new TextContentBlock { Text = $"Tool '{toolName}' not found" }],
                                IsError = true
                            };
                        }

                        try
                        {
                            var (_, handler) = toolInfo;

                            logger?.LogInformation("[McpAdapter] Calling handler for '{ToolName}'", toolName);

                            // Execute the function handler (returns JSON string)
                            var resultJson = await handler(argumentsJson);

                            logger?.LogInformation(
                                "[McpAdapter] Handler returned for '{ToolName}': {ResultJson}",
                                toolName,
                                resultJson);

                            // Return the result as text content
                            return new CallToolResult
                            {
                                Content = [new TextContentBlock { Text = resultJson }],
                                IsError = false
                            };
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError(
                                ex,
                                "[McpAdapter] ERROR executing tool '{ToolName}': {ErrorMessage}",
                                toolName,
                                ex.Message);

                            return new CallToolResult
                            {
                                Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                                IsError = true
                            };
                        }
                    }
                };

                // Set the handlers on the options
                options.Handlers = handlers;
            });

        return services;
    }

    /// <summary>
    /// Builds JSON schema for tool input parameters
    /// </summary>
    private static JsonObject BuildInputSchema(FunctionContract contract)
    {
        var schema = new JsonObject
        {
            ["type"] = "object"
        };

        if (contract.Parameters == null || !contract.Parameters.Any())
        {
            schema["properties"] = new JsonObject();
            schema["required"] = new JsonArray();
            return schema;
        }

        var properties = new JsonObject();
        var requiredParams = new JsonArray();

        foreach (var param in contract.Parameters)
        {
            properties[param.Name] = ConvertJsonSchemaToJsonNode(param.ParameterType, param.Description);

            if (param.IsRequired)
            {
                requiredParams.Add(param.Name);
            }
        }

        schema["properties"] = properties;
        schema["required"] = requiredParams;

        return schema;
    }

    /// <summary>
    /// Converts JsonSchemaObject to JsonNode for MCP serialization
    /// </summary>
    private static JsonNode ConvertJsonSchemaToJsonNode(JsonSchemaObject schema, string? description)
    {
        var result = new JsonObject();

        // Handle Union<string, IReadOnlyList<string>> Type
        if (schema.Type.Is<string>())
        {
            result["type"] = schema.Type.Get<string>();
        }
        else if (schema.Type.Is<IReadOnlyList<string>>())
        {
            var typeArray = new JsonArray();
            foreach (var t in schema.Type.Get<IReadOnlyList<string>>())
            {
                typeArray.Add(t);
            }
            result["type"] = typeArray;
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            result["description"] = description;
        }

        if (schema.Properties != null && schema.Properties.Any())
        {
            var props = new JsonObject();
            foreach (var prop in schema.Properties)
            {
                props[prop.Key] = ConvertJsonSchemaToJsonNode(prop.Value, null);
            }
            result["properties"] = props;
        }

        if (schema.Items != null)
        {
            result["items"] = ConvertJsonSchemaToJsonNode(schema.Items, null);
        }

        if (schema.Required != null && schema.Required.Any())
        {
            var required = new JsonArray();
            foreach (var req in schema.Required)
            {
                required.Add(req);
            }
            result["required"] = required;
        }

        if (schema.Enum != null && schema.Enum.Any())
        {
            var enumValues = new JsonArray();
            foreach (var enumValue in schema.Enum)
            {
                enumValues.Add(JsonValue.Create(enumValue));
            }
            result["enum"] = enumValues;
        }

        return result;
    }

    /// <summary>
    /// Gets the tool name for MCP (handles class name prefix if present)
    /// </summary>
    private static string GetToolName(FunctionContract contract)
    {
        // Use MCP convention: ClassName-FunctionName if ClassName is present
        if (!string.IsNullOrWhiteSpace(contract.ClassName))
        {
            return $"{contract.ClassName}-{contract.Name}";
        }

        return contract.Name;
    }
}
