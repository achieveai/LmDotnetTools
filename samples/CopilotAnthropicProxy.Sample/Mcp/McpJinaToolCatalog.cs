using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;

internal sealed record McpLocalTool(string Name, JsonObject Definition, ToolHandler Handler);

internal sealed class McpJinaToolCatalog
{
    public const string WebSearchName = "web_search";
    public const string WebFetchName = "web_fetch";

    public McpJinaToolCatalog(JinaWebProvider provider, WebToolsOptions options, ILoggerFactory loggerFactory)
    {
        var validationErrors = options.Validate();
        IsEnabled = !string.IsNullOrWhiteSpace(options.JinaApiKey) && validationErrors.Count == 0;
        if (!IsEnabled)
        {
            if (!string.IsNullOrWhiteSpace(options.JinaApiKey) && validationErrors.Count > 0)
            {
                loggerFactory
                    .CreateLogger<McpJinaToolCatalog>()
                    .LogWarning(
                        "Local MCP web tools are disabled because configuration is invalid: {Errors}",
                        string.Join("; ", validationErrors)
                    );
            }

            Tools = new Dictionary<string, McpLocalTool>();
            return;
        }

        var search = new WebSearchTool(provider, options, loggerFactory.CreateLogger<WebSearchTool>());
        var fetch = new WebFetchTool(provider, options, loggerFactory.CreateLogger<WebFetchTool>());
        Tools = new Dictionary<string, McpLocalTool>(StringComparer.Ordinal)
        {
            [WebSearchName] = BuildTool(WebSearchName, search.Contract, search.Handler),
            [WebFetchName] = BuildTool(WebFetchName, fetch.Contract, fetch.Handler),
        };
    }

    public bool IsEnabled { get; }

    public IReadOnlyDictionary<string, McpLocalTool> Tools { get; }

    public IReadOnlyList<McpLocalTool> SelectInjectable(IHeaderDictionary headers, IEnumerable<string> githubNames)
    {
        if (!IsEnabled || IsTruthy(headers["X-MCP-Lockdown"].FirstOrDefault()))
        {
            return [];
        }

        var github = githubNames.ToHashSet(StringComparer.Ordinal);
        var allowlist = ParseNames(headers["X-MCP-Tools"]);
        var exclusions = ParseNames(headers["X-MCP-Exclude-Tools"]);
        var hasAllowlist = headers.ContainsKey("X-MCP-Tools");

        return
        [
            .. Tools.Values
                .Where(tool => !github.Contains(tool.Name))
                .Where(tool => !hasAllowlist || allowlist.Contains(tool.Name))
                .Where(tool => !exclusions.Contains(tool.Name)),
        ];
    }

    private static McpLocalTool BuildTool(string name, AchieveAi.LmDotnetTools.LmCore.Core.FunctionContract contract, ToolHandler handler)
    {
        var schema = contract.GetJsonSchema();
        var schemaNode = schema is null
            ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            : JsonNode.Parse(JsonSerializer.Serialize(schema, JsonSchemaValidator.SchemaSerializationOptions)) as JsonObject ?? [];
        var definition = new JsonObject
        {
            ["name"] = name,
            ["description"] = contract.Description,
            ["inputSchema"] = schemaNode,
        };
        return new McpLocalTool(name, definition, handler);
    }

    private static HashSet<string> ParseNames(Microsoft.Extensions.Primitives.StringValues values) =>
        values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim() is not ("0" or "false" or "f" or "no" or "n" or "off");
}
