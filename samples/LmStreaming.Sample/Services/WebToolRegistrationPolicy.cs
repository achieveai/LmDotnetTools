using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
    /// the Jina fallback tools. Plain <c>copilot</c> and dynamically discovered Copilot models are
    /// included through the <c>isCopilotBackedModel</c> flag the caller passes, not by literal id.
    /// Keeping them out of this static set ensures the caller explicitly establishes Copilot context
    /// before enabling fallback tools.
    /// </summary>
    private static readonly HashSet<string> FallbackProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        // Deterministic mock providers exercise the same function-tool surface in manual/E2E validation.
        "test",
        "test-anthropic",
    };

    /// <summary>
    /// Resolves function-tool intent for web-capable modes. A dedicated built-in <c>web_search</c>
    /// declaration also enables the function-shaped hosted/Jina search and Jina fetch equivalents.
    /// </summary>
    public static IReadOnlyList<string>? ResolveEnabledTools(
        IReadOnlyList<string>? enabledTools,
        IReadOnlyList<string>? enabledBuiltInTools
    )
    {
        if (enabledBuiltInTools is null || !enabledBuiltInTools.Contains("web_search"))
        {
            return enabledTools;
        }

        var resolved = new HashSet<string>(enabledTools ?? [], StringComparer.Ordinal)
        {
            "web_search",
            WebSearchTool.ToolName,
            WebFetchTool.ToolName,
        };
        return [.. resolved];
    }

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
    /// <param name="isAnthropicCompatModel">
    /// <c>true</c> when the provider id resolves to a dynamically discovered Anthropic-compatible
    /// provider-family model (e.g. DeepSeek). Those models likewise lack a native web capability and so
    /// receive the Jina fallback tools, alongside <paramref name="isCopilotBackedModel" /> and the
    /// statically allow-listed ids in <see cref="FallbackProviderIds" />.</param>
    /// <param name="suppressWebSearch"><c>true</c> when Copilot hosted <c>web_search</c> was registered;
    /// Jina <c>WebSearch</c> is then skipped while Jina <c>WebFetch</c> remains eligible.</param>
    /// <returns>A small list of human-readable status strings describing what was registered, skipped,
    /// or disabled (for diagnostics/logging). Never contains secret values.</returns>
    public static IReadOnlyList<string> Apply(
        FunctionRegistry registry,
        string providerId,
        IReadOnlyList<string>? enabledTools,
        JinaWebProvider? provider,
        WebToolsOptions options,
        ILoggerFactory loggerFactory,
        bool isCopilotBackedModel = false,
        bool isAnthropicCompatModel = false,
        bool suppressWebSearch = false
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
        var normalized = string.IsNullOrWhiteSpace(providerId) ? string.Empty : providerId.Trim().ToLowerInvariant();
        if (!isCopilotBackedModel && !isAnthropicCompatModel && !FallbackProviderIds.Contains(normalized))
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

        // Copilot's hosted web_search takes precedence; Jina remains the fallback.
        if (suppressWebSearch)
        {
            statuses.Add("WebSearch skipped: Copilot web_search registered");
        }
        // WebSearch requires the Jina API key; without it the tool is not advertised at all.
        else if (string.IsNullOrWhiteSpace(options.JinaApiKey))
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

/// <summary>
/// The Jina web tools a model-fallback hop needs when its own provider has no native search. They are
/// registered once, so the conversation can run them, but hidden: only the request sent to such a hop
/// lists them, so the primary's request and prompt cache stay as they were.
/// </summary>
/// <param name="registry">The conversation's registry, after the primary's own tools were added.</param>
/// <param name="enabledTools">The mode's resolved function-tool allow-list; <c>null</c> means all tools.</param>
/// <param name="provider">The shared Jina provider, or <c>null</c> when web tools are disabled.</param>
/// <param name="options">Web tools configuration; only the key's presence is checked.</param>
/// <param name="loggerFactory">Creates the tool loggers.</param>
internal sealed class FallbackWebTools(
    FunctionRegistry registry,
    IReadOnlyList<string>? enabledTools,
    JinaWebProvider? provider,
    WebToolsOptions options,
    ILoggerFactory loggerFactory
)
{
    private HiddenWebTools? _hidden;

    /// <summary>The mode names web search, so a target that cannot search must not answer it.</summary>
    public bool SearchRequired =>
        enabledTools is not null
        && (enabledTools.Contains("web_search") || enabledTools.Contains(WebSearchTool.ToolName));

    /// <summary>The mode allows web search; a mode with no tool list allows it without promising it.</summary>
    public bool ModeEnablesSearch => enabledTools is null || SearchRequired;

    /// <summary>A target with no native search would still have a working function search.</summary>
    public bool CanSearch => ModeEnablesSearch && (HasBackend || RegisteredNames().Contains(WebSearchTool.ToolName));

    private bool HasBackend => provider is not null && !string.IsNullOrWhiteSpace(options.JinaApiKey);

    /// <summary>
    /// Registers the hidden tools the mode allows, once, and returns the contracts a hop should list.
    /// A name already registered is left alone: the primary's own tool keeps working and stays advertised.
    /// </summary>
    public IReadOnlyList<FunctionContract> RegisterHidden()
    {
        if (_hidden is not null)
        {
            return _hidden.Contracts;
        }

        var taken = RegisteredNames();
        var functions = new List<FunctionDescriptor>();
        if (provider is not null && CanSearch && !taken.Contains(WebSearchTool.ToolName))
        {
            var tool = new WebSearchTool(provider, options, loggerFactory.CreateLogger<WebSearchTool>());
            functions.Add(Describe(tool.Contract, tool.Handler));
        }

        if (
            provider is not null
            && (enabledTools is null || enabledTools.Contains(WebFetchTool.ToolName))
            && !taken.Contains(WebFetchTool.ToolName)
        )
        {
            var tool = new WebFetchTool(provider, options, loggerFactory.CreateLogger<WebFetchTool>());
            functions.Add(Describe(tool.Contract, tool.Handler));
        }

        _hidden = new HiddenWebTools(functions);
        if (functions.Count > 0)
        {
            _ = registry.AddProvider(_hidden);
        }

        return _hidden.Contracts;
    }

    /// <summary>
    /// The tool list for one hop: a target with native search drops the function search and fetch, so it
    /// never gets both; any other target gains the hidden tools its request does not already list.
    /// </summary>
    public static FunctionContract[]? ShapeFunctions(
        FunctionContract[]? functions,
        bool hasNativeSearch,
        IReadOnlyList<FunctionContract> hidden
    )
    {
        var listed = functions ?? [];
        FunctionContract[] shaped = hasNativeSearch
            ? [.. listed.Where(f => f.Name is not (WebSearchTool.ToolName or WebFetchTool.ToolName))]
            :
            [
                .. listed,
                .. hidden.Where(h =>
                    !listed.Any(f => string.Equals(f.Name, h.Name, StringComparison.OrdinalIgnoreCase))
                ),
            ];
        return shaped.Length == 0 && functions is null ? null : shaped;
    }

    /// <summary>Keeps sub-agents from inheriting the hidden tools, which only a fallback hop may list.</summary>
    public SubAgentOptions? WithoutHiddenTools(SubAgentOptions? subAgents) =>
        subAgents is null || _hidden is not { Contracts.Count: > 0 }
            ? subAgents
            : subAgents with
            {
                NonInheritedToolNames =
                [
                    .. subAgents.NonInheritedToolNames ?? [],
                    .. _hidden.Contracts.Select(c => c.Name),
                ],
            };

    private HashSet<string> RegisteredNames() =>
        registry.Build().Item1.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static FunctionDescriptor Describe(FunctionContract contract, ToolHandler handler) =>
        new()
        {
            Contract = contract,
            Handler = handler,
            ProviderName = "WebTools",
        };

    /// <summary>Keeps its tools' handlers registered and leaves them out of the request's tool list.</summary>
    private sealed class HiddenWebTools(IReadOnlyList<FunctionDescriptor> functions) : IDeferredToolProvider
    {
        public IReadOnlyList<FunctionContract> Contracts { get; } = [.. functions.Select(f => f.Contract)];

        public string ProviderName => "WebTools";

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() => functions;

        public bool IsAdvertised(string functionName) => Contracts.All(c => c.Name != functionName);

        public ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson) => null;
    }
}
