using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The Jina web tools a model-fallback hop needs when its own provider has no native search. They are
/// registered once, so the conversation can run them, but hidden: only the request sent to such a hop
/// lists them, so the primary's request and prompt cache stay as they were.
/// </summary>
/// <remarks>
/// Copilot's hosted search is not usable on a hop: an outage that sends a Copilot primary to its fallback
/// takes the hosted search down with it. A hop never lists it, so the hop searches through Jina or, when
/// the mode lists search and there is no Jina key, is skipped.
/// </remarks>
/// <param name="registry">The conversation's registry, after the primary's own tools were added.</param>
/// <param name="enabledTools">The mode's resolved function-tool allow-list; <c>null</c> means all tools.</param>
/// <param name="provider">The shared Jina provider, or <c>null</c> when web tools are disabled.</param>
/// <param name="options">Web tools configuration: the key decides whether search is possible, and the
/// tools built here read the rest.</param>
/// <param name="loggerFactory">Creates the tool loggers.</param>
/// <param name="searchIsHosted"><c>true</c> when the <c>WebSearch</c> in <paramref name="registry" /> is
/// Copilot's hosted search.</param>
internal sealed class FallbackWebTools(
    FunctionRegistry registry,
    IReadOnlyList<string>? enabledTools,
    JinaWebProvider? provider,
    WebToolsOptions options,
    ILoggerFactory loggerFactory,
    bool searchIsHosted
)
{
    /// <summary>The name Jina search takes on a hop when the hosted search already holds <c>WebSearch</c>.</summary>
    internal const string JinaSearchName = "JinaWebSearch";

    private HiddenWebTools? _hidden;

    /// <summary>
    /// The mode names web search, so a target that cannot search must not answer it. This deliberately
    /// accepts the built-in name too, which the primary's Jina gate does not: a mode that lists only
    /// <c>web_search</c> still promised its user a search, and the hop has to keep that promise.
    /// </summary>
    public bool SearchRequired =>
        enabledTools is not null
        && (
            enabledTools.Contains(CopilotWebSearchRegistration.ToolName)
            || enabledTools.Contains(WebSearchTool.ToolName)
        );

    /// <summary>The mode allows web search; a mode with no tool list allows it without promising it.</summary>
    public bool ModeEnablesSearch => enabledTools is null || SearchRequired;

    /// <summary>A target with no native search would still have a function search that works in an outage.</summary>
    public bool CanSearch =>
        ModeEnablesSearch && (HasBackend || (!searchIsHosted && RegisteredNames().Contains(WebSearchTool.ToolName)));

    private bool HasBackend => provider is not null && !string.IsNullOrWhiteSpace(options.JinaApiKey);

    private string SearchName => searchIsHosted ? JinaSearchName : WebSearchTool.ToolName;

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
        if (provider is not null && CanSearch && !taken.Contains(SearchName))
        {
            var tool = new WebSearchTool(provider, options, loggerFactory.CreateLogger<WebSearchTool>());

            // Each tool builds its own contract, so renaming this one touches nothing else.
            tool.Contract.Name = SearchName;
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
    /// never gets both; any other target loses the hosted search and gains the hidden tools its request
    /// does not already list.
    /// </summary>
    public FunctionContract[]? ShapeFunctions(
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
                .. listed.Where(f => !searchIsHosted || f.Name != WebSearchTool.ToolName),
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
