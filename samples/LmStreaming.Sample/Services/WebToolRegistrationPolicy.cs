using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Narrow sample-only helper that registers the Jina-backed <c>WebFetch</c>/<c>WebSearch</c> fallback
/// function tools into a per-conversation <see cref="FunctionRegistry" />, but only for providers that
/// lack a native web capability. It is deliberately NOT a general capability framework — it encodes a
/// single allow-list and the two registration rules this sample needs.
/// </summary>
internal static class WebToolRegistrationPolicy
{
    /// <summary>
    /// <see cref="ProviderRegistry" /> ids that have NO native web capability and therefore receive
    /// the Jina fallback tools. Plain <c>copilot</c> is intentionally excluded: it returns early on
    /// the <c>CopilotAgentLoop</c> (CLI) path before the per-conversation registry is built, so it
    /// never reaches this seam. Dynamically discovered Copilot models (Anthropic/OpenAI) route through
    /// the normal middleware agent loop and are included via the <c>isCopilotBackedModel</c> flag the
    /// caller passes, not by literal id.
    /// </summary>
    private static readonly HashSet<string> FallbackProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
    };

    /// <summary>
    /// Registers the Jina <c>WebFetch</c>/<c>WebSearch</c> function tools into <paramref name="registry" />
    /// when the resolved provider has no native web capability.
    /// </summary>
    /// <param name="registry">The per-conversation registry to mutate (already populated with the
    /// sample/MCP tools so collisions can be detected).</param>
    /// <param name="providerId">The resolved provider id (normalized internally, mirroring
    /// <c>ProviderRegistry.NormalizeId</c>).</param>
    /// <param name="enabledTools">The mode's function-tool allow-list (<c>EnabledTools</c>); <c>null</c>
    /// means "all tools enabled". The server-side built-in list (<c>EnabledBuiltInTools</c>) is handled
    /// elsewhere and is intentionally not consulted here.</param>
    /// <param name="provider">The shared Jina provider, or <c>null</c> when web tools are disabled
    /// (invalid configuration). When <c>null</c>, nothing is registered.</param>
    /// <param name="options">Web tools configuration (used for the API-key gate and tool construction).</param>
    /// <param name="loggerFactory">Factory used to create tool/diagnostic loggers.</param>
    /// <param name="isCopilotBackedModel">
    /// <c>true</c> when the provider id resolves to a dynamically discovered Copilot model (Anthropic or
    /// OpenAI). Those models lack a native web capability and so receive the Jina fallback tools,
    /// alongside the statically allow-listed ids in <see cref="FallbackProviderIds" />.</param>
    /// <returns>A small list of human-readable status strings describing what was registered, skipped,
    /// or disabled (for diagnostics/logging). Never contains secret values.</returns>
    public static IReadOnlyList<string> Apply(
        FunctionRegistry registry,
        string providerId,
        IReadOnlyList<string>? enabledTools,
        JinaWebProvider? provider,
        WebToolsOptions options,
        ILoggerFactory loggerFactory,
        bool isCopilotBackedModel = false
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var statuses = new List<string>();

        // No backend provider (e.g. invalid configuration): register nothing.
        if (provider is null)
        {
            return statuses;
        }

        // Mirror ProviderRegistry.NormalizeId: trim + lowercase, case-insensitive allow-list lookup.
        var normalized = string.IsNullOrWhiteSpace(providerId)
            ? string.Empty
            : providerId.Trim().ToLowerInvariant();
        if (!isCopilotBackedModel && !FallbackProviderIds.Contains(normalized))
        {
            return statuses;
        }

        var logger = loggerFactory.CreateLogger("LmStreaming.Sample.WebToolRegistrationPolicy");

        // Case-insensitive snapshot of already-registered contract names (sample tools + TaskManager +
        // MCP tools) so a fallback tool that would shadow an existing one is skipped rather than added.
        var (existingContracts, _) = registry.Build();
        var existingNames = new HashSet<string>(
            existingContracts.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase
        );

        // EnabledTools is the function-tool allow-list (null = all). Use ordinal Contains to match the
        // per-conversation filter loop in Program.cs.
        bool ModeEnables(string name) => enabledTools is null || enabledTools.Contains(name);

        // WebFetch works with or without an API key.
        if (ModeEnables(WebFetchTool.ToolName))
        {
            if (existingNames.Contains(WebFetchTool.ToolName))
            {
                logger.LogWarning(
                    "Skipped registering {Tool}: a tool with that name is already registered.",
                    WebFetchTool.ToolName
                );
                statuses.Add($"{WebFetchTool.ToolName} skipped: name already registered");
            }
            else
            {
                var tool = new WebFetchTool(provider, options, loggerFactory.CreateLogger<WebFetchTool>());
                _ = registry.AddFunction(tool.Contract, tool.Handler, "WebTools");
                statuses.Add($"{WebFetchTool.ToolName} registered");
            }
        }

        // WebSearch requires the Jina API key; without it the tool is not advertised at all.
        if (string.IsNullOrWhiteSpace(options.JinaApiKey))
        {
            logger.LogInformation("WebSearch disabled: JINA_API_KEY not set");
            statuses.Add("WebSearch disabled: JINA_API_KEY not set");
        }
        else if (ModeEnables(WebSearchTool.ToolName))
        {
            if (existingNames.Contains(WebSearchTool.ToolName))
            {
                logger.LogWarning(
                    "Skipped registering {Tool}: a tool with that name is already registered.",
                    WebSearchTool.ToolName
                );
                statuses.Add($"{WebSearchTool.ToolName} skipped: name already registered");
            }
            else
            {
                var tool = new WebSearchTool(provider, options, loggerFactory.CreateLogger<WebSearchTool>());
                _ = registry.AddFunction(tool.Contract, tool.Handler, "WebTools");
                statuses.Add($"{WebSearchTool.ToolName} registered");
            }
        }

        return statuses;
    }
}
