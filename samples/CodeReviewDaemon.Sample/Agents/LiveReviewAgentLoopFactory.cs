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
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using CodeReviewDaemon.Sample.Configuration;
using ModelContextProtocol.Client;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The production <see cref="IReviewAgentLoopFactory"/>: assembles an Anthropic Messages
/// <see cref="MultiTurnAgentLoop"/> routed through the GitHub Copilot backend (mirrors the Copilot-backed
/// Anthropic wiring in <c>LmStreaming.Sample</c>). The bearer token comes from the local GitHub Copilot /
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
/// <b>Lifetime.</b> The Copilot-backed <see cref="AnthropicAgent"/> (and the transport
/// <see cref="System.Net.Http.HttpClient"/> it owns) is created once on first use and <b>shared</b> across
/// every loop, then disposed with this singleton. <see cref="MultiTurnAgentLoop"/> disposal does not dispose
/// its provider agent, so a fresh agent per run would leak one client per run; sharing avoids that. The
/// daemon drives runs serially (the <c>ReviewStore</c> singleton is documented single-accessor), so one
/// shared agent across the per-run loops is safe.
/// </para>
/// </summary>
internal sealed class LiveReviewAgentLoopFactory : IReviewAgentLoopFactory, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ICopilotTokenProvider _tokenProvider = new CliCredentialCopilotTokenProvider();
    private readonly CopilotSessionContext _session = new();
    private readonly object _agentGate = new();
    private AnthropicAgent? _agent;

    public LiveReviewAgentLoopFactory(ILoggerFactory loggerFactory, CodeReviewDaemonOptions options)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// The same shared, lazily-created Copilot-backed agent every review loop is given (see the Lifetime
    /// remarks on this type), exposed as an <see cref="IStreamingAgent"/> factory so a discovered
    /// <c>code-reviewer:*</c> sub-agent (Task 12) is driven by the identical provider agent instead of
    /// standing up a second one.
    /// </summary>
    public Func<IStreamingAgent> SharedAgentFactory => GetSharedAgent;

    public IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        // Anthropic Messages agent over the Copilot backend (POST {copilot-host}/v1/messages); shared across
        // loops. The model id (e.g. claude-sonnet-5) is supplied per run via defaultOptions below.
        var providerAgent = GetSharedAgent();

        // Resolve the adaptive-thinking effort: an explicit per-call value wins, else the configured
        // default. Attach output_config ONLY when the effort is non-empty — an adaptive model (sonnet)
        // needs it to keep reasoning bounded, but a non-adaptive model (haiku) REJECTS an effort it does
        // not support, so it must be omitted for those.
        var effort = reasoningEffort ?? _options.ReviewReasoningEffort;
        var extraProperties = string.IsNullOrWhiteSpace(effort)
            ? ImmutableDictionary<string, object?>.Empty
            : ImmutableDictionary<string, object?>.Empty.Add(
                "OutputConfig", new AnthropicOutputConfig { Effort = effort });

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
                AdditionalHeaders = new Dictionary<string, string> { ["X-Session-ID"] = toolContext.SessionId },
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
            logger: _loggerFactory.CreateLogger<MultiTurnAgentLoop>(),
            subAgentOptions: toolContext?.SubAgentOptions,
            loggerFactory: _loggerFactory);

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

    private AnthropicAgent GetSharedAgent()
    {
        if (_agent is not null)
        {
            return _agent;
        }

        lock (_agentGate)
        {
            _agent ??= CopilotAnthropicAgentFactory.Create(
                "CopilotAnthropic",
                _tokenProvider,
                _session,
                logger: _loggerFactory.CreateLogger<AnthropicAgent>());
            return _agent;
        }
    }

    public void Dispose() => _agent?.Dispose();
}
