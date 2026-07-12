using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using ModelContextProtocol.Client;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The production <see cref="IReviewAgentLoopFactory"/>: assembles either a Copilot <b>Anthropic Messages</b>
/// or Copilot <b>OpenAI Responses</b> <see cref="MultiTurnAgentLoop"/> — selected per run by the effective
/// review model's vendor (the per-call model override, else the configured
/// <see cref="CodeReviewDaemonOptions.ReviewModelId"/>: <c>claude-*</c> → Anthropic, <c>gpt-*</c>/
/// <c>o1|o3|o4</c> → OpenAI Responses) — routed through the GitHub Copilot backend (mirrors the Copilot-backed
/// wiring in <c>LmStreaming.Sample</c>). The bearer token comes from the local GitHub Copilot /
/// <c>gh</c> CLI login via <see cref="CliCredentialCopilotTokenProvider"/> — no API key / base URL config
/// knob. When <see cref="Create"/> is called without a <see cref="ReviewToolContext"/> the registry stays
/// empty — the daemon's agents are collect-only text agents that reason over the diff the executor
/// supplies and never call tools. Supplying a <see cref="ReviewToolContext"/> connects the gateway MCP
/// client and exposes its tools filtered to the read-only allow-list instead.
/// <para>
/// This path is <b>dead by default</b>: with the repo allow-list empty the poller has no targets, so
/// the executor is never invoked and this factory is never called. It does no work at construction
/// (lazy per run), so registering it cannot affect daemon boot or the route surface.
/// </para>
/// <para>
/// <b>Lifetime.</b> Each Copilot-backed provider agent (the <see cref="AnthropicAgent"/> and/or
/// <see cref="OpenAiResponsesAgent"/>, and the transport <see cref="System.Net.Http.HttpClient"/> it owns)
/// is created once on first use <b>for its vendor</b> and <b>shared</b> across every loop of that vendor,
/// then disposed with this singleton. A single factory instance can serve both vendors (a claude primary
/// with a gpt variant, or the reverse), so up to two agents are cached — one per backend.
/// <see cref="MultiTurnAgentLoop"/> disposal does not dispose its provider agent, so a fresh agent per run
/// would leak one client per run; sharing avoids that. The daemon drives runs serially (the
/// <c>ReviewStore</c> singleton is documented single-accessor), so one shared agent per vendor across the
/// per-run loops is safe.
/// </para>
/// </summary>
internal sealed class LiveReviewAgentLoopFactory : IReviewAgentLoopFactory, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly CodeReviewDaemonOptions _options;
    private readonly IConversationStore? _conversationStore;
    private readonly ICopilotTokenProvider _tokenProvider = new CliCredentialCopilotTokenProvider();
    private readonly CopilotSessionContext _session = new();
    private readonly object _agentGate = new();
    private IStreamingAgent? _anthropicAgent;
    private IStreamingAgent? _openAiAgent;

    public LiveReviewAgentLoopFactory(
        ILoggerFactory loggerFactory,
        CodeReviewDaemonOptions options,
        IConversationStore? conversationStore = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _conversationStore = conversationStore;
    }

    /// <summary>
    /// The shared, lazily-created Copilot-backed agent for the primary configured
    /// <see cref="CodeReviewDaemonOptions.ReviewModelId"/>'s vendor (see the Lifetime remarks on this type),
    /// exposed as an <see cref="IStreamingAgent"/> factory so a discovered <c>code-reviewer:*</c> sub-agent
    /// (Task 12) is driven by that same provider agent instead of standing up a second one.
    /// </summary>
    public Func<IStreamingAgent> SharedAgentFactory => () => GetSharedAgent(IsOpenAiModel(_options.ReviewModelId));

    public IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        // Route by the EFFECTIVE per-call model (the per-run override callers pass — run.ModelId for the
        // primary/knowledge/judge paths, VariantModelId for the A/B B arm — else the configured
        // ReviewModelId), not the configured model, so a claude variant under a gpt-* primary (or the
        // reverse) is driven by the matching backend with the matching request shape. The reasoning shape:
        // the Copilot Anthropic Messages agent takes an adaptive-thinking output_config.effort (and a
        // non-adaptive model like haiku REJECTS an effort it does not support, so it is omitted when empty);
        // the Copilot OpenAI Responses agent (gpt-5.5) takes a reasoning request (effort + summary).
        var effort = reasoningEffort ?? _options.ReviewReasoningEffort;
        var (isOpenAi, extraProperties) = ResolveReasoning(modelId, _options.ReviewModelId, effort);

        // Anthropic Messages agent (claude-*) or OpenAI Responses agent (gpt-*/o-series) over the Copilot
        // backend, chosen by that same effective-model vendor; one shared agent per vendor across loops. The
        // model id is supplied per run via defaultOptions below.
        var providerAgent = GetSharedAgent(isOpenAi);

        // Only on the tool-assisted path (toolContext is not null) do we connect the gateway MCP client and
        // filter its tools down to the read-only allow-list — the diff-only path keeps today's empty
        // registry exactly as before.
        var registry = new FunctionRegistry();
        IReadOnlyList<McpClient> ownedClients = [];
        if (toolContext is not null)
        {
            var scratch = new FunctionRegistry();
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "sandbox",
                Endpoint = new Uri($"{toolContext.GatewayBaseUrl}/mcp"),
                AdditionalHeaders = DaemonMcpTransportHeaders.BuildTransportHeaders(toolContext.SessionId, toolContext.Credential),
            });
            var client = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
            ownedClients = [client];
            _ = scratch.AddMcpClientsAsync(
                    new Dictionary<string, McpClient> { ["sandbox"] = client }, "sandbox", omitServerPrefix: true)
                .GetAwaiter().GetResult();
            var (srcContracts, _) = scratch.Build();

            // The pooled scoped-writable reviewer (Layer 1) keeps the read-only tools AND adds scoped
            // Write/Edit/Bash (code stays read-only; Write/Edit are path-wrapped to the PR notes dir +
            // scratch). Absent that config the reviewer stays hard read-only exactly as before.
            var scopedWrite = toolContext.EnableReviewerWrites
                && toolContext.WritableToolAllowList is { Count: > 0 }
                && !string.IsNullOrWhiteSpace(toolContext.NotesDir)
                && !string.IsNullOrWhiteSpace(toolContext.ScratchDir);
            if (scopedWrite)
            {
                ScopedToolFilter.Apply(
                    scratch,
                    registry,
                    toolContext.ReadOnlyToolAllowList,
                    toolContext.WritableToolAllowList!,
                    toolContext.NotesDir!,
                    toolContext.ScratchDir!);
            }
            else
            {
                ReadOnlyToolFilter.Apply(scratch, registry, toolContext.ReadOnlyToolAllowList);
            }

            var (keptContracts, _) = registry.Build();
            _loggerFactory.CreateLogger<LiveReviewAgentLoopFactory>().LogInformation(
                "Tool-assisted loop {ThreadId} (session {SessionId}): gateway tools=[{Src}] ro-allow=[{Allow}] "
                    + "writable=[{Writable}] kept=[{Kept}]",
                threadId,
                toolContext.SessionId,
                string.Join(",", srcContracts.Select(c => c.Name)),
                string.Join(",", toolContext.ReadOnlyToolAllowList),
                scopedWrite ? string.Join(",", toolContext.WritableToolAllowList!) : "(read-only)",
                string.Join(",", keptContracts.Select(c => c.Name)));
        }

        // Persist the conversation (primary + sub-agents) when a store is configured, so every review's tool
        // calls — Skill loads and sub-agent Task dispatches — are auditable after the run. Sub-agents share the
        // same store (keyed per thread) via DefaultConversationStoreFactory unless one is already supplied.
        var subAgentOptions = toolContext?.SubAgentOptions;
        if (_conversationStore is not null && subAgentOptions is { DefaultConversationStoreFactory: null })
        {
            subAgentOptions = subAgentOptions with { DefaultConversationStoreFactory = _ => _conversationStore };
        }

        var loop = new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId,
            systemPrompt: profile.SystemPrompt,
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = modelId ?? string.Empty,
                MaxToken = _options.ReviewMaxTokens,
                ExtraProperties = extraProperties,
            },
            store: _conversationStore,
            logger: _loggerFactory.CreateLogger<MultiTurnAgentLoop>(),
            subAgentOptions: subAgentOptions,
            loggerFactory: _loggerFactory,
            persistRunLedger: _conversationStore is not null);

        // Start the loop's background processing task before returning: ExecuteRunAsync only enqueues input
        // and reads the output channel — it does NOT start RunLoopAsync — so without this the caller's
        // ExecuteRunAsync would block forever waiting on a loop that never drains the input (the same
        // fire-and-forget start SubAgentManager and the README use). The executor await-usings the loop,
        // whose DisposeAsync → StopAsync cancels this task, so it is not leaked.
        var logger = _loggerFactory.CreateLogger<LiveReviewAgentLoopFactory>();
        _ = loop.RunAsync().ContinueWith(
            t => logger.LogError(t.Exception, "Review agent loop '{ThreadId}' faulted.", threadId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return ownedClients.Count > 0 ? new ToolScopedReviewLoop(loop, ownedClients) : loop;
    }

    /// <summary>
    /// The shared Copilot-backed provider agent for the requested vendor, lazily created on first use and
    /// reused thereafter. A single factory instance can serve both a claude and a gpt run, so the two vendors
    /// get independent cache slots (guarded by <see cref="_agentGate"/>); each is disposed with this singleton.
    /// </summary>
    private IStreamingAgent GetSharedAgent(bool isOpenAi)
    {
        var cached = isOpenAi ? _openAiAgent : _anthropicAgent;
        if (cached is not null)
        {
            return cached;
        }

        lock (_agentGate)
        {
            if (isOpenAi)
            {
                return _openAiAgent ??= CopilotResponsesAgentFactory.Create(
                    "CopilotResponses",
                    _tokenProvider,
                    CopilotResponsesTransport.Sse,
                    _session,
                    logger: _loggerFactory.CreateLogger<OpenAiResponsesAgent>());
            }

            return _anthropicAgent ??= CopilotAnthropicAgentFactory.Create(
                "CopilotAnthropic",
                _tokenProvider,
                _session,
                logger: _loggerFactory.CreateLogger<AnthropicAgent>());
        }
    }

    /// <summary>
    /// The provider routing decision for one review turn: resolves the effective model (the per-call
    /// <paramref name="modelId"/> override, else the configured <paramref name="configuredModelId"/>), picks
    /// the vendor, and builds the matching reasoning-request shape — an OpenAI Responses <c>Reasoning</c>
    /// (effort + summary) for <c>gpt-*</c>/<c>o1|o3|o4</c>, or an Anthropic Messages <c>OutputConfig</c>
    /// (effort, omitted when empty for a non-adaptive model like haiku) for <c>claude-*</c>. Both the provider
    /// agent selection and the request shape are driven off this single decision so they can never diverge.
    /// </summary>
    internal static (bool IsOpenAi, ImmutableDictionary<string, object?> ExtraProperties) ResolveReasoning(
        string? modelId, string? configuredModelId, string? effort)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(modelId) ? configuredModelId : modelId;
        var isOpenAi = IsOpenAiModel(effectiveModelId);
        var extraProperties = ImmutableDictionary<string, object?>.Empty;
        if (isOpenAi)
        {
            extraProperties = extraProperties.Add(
                "Reasoning",
                new ResponseReasoningOptions
                {
                    Effort = string.IsNullOrWhiteSpace(effort) ? null : effort,
                    Summary = "auto",
                });
        }
        else if (!string.IsNullOrWhiteSpace(effort))
        {
            extraProperties = extraProperties.Add("OutputConfig", new AnthropicOutputConfig { Effort = effort });
        }

        return (isOpenAi, extraProperties);
    }

    /// <summary>
    /// Whether <paramref name="modelId"/> is served by the Copilot <b>OpenAI Responses</b> backend
    /// (<c>gpt-*</c>, <c>o1/o3/o4</c>) rather than the Anthropic Messages backend (<c>claude-*</c>). Drives
    /// both which provider agent is created and which reasoning-request shape is attached.
    /// </summary>
    private static bool IsOpenAiModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        (_anthropicAgent as IDisposable)?.Dispose();
        (_openAiAgent as IDisposable)?.Dispose();
    }
}
