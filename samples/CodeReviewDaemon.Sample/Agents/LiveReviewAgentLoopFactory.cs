using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The production <see cref="IReviewAgentLoopFactory"/>: assembles an Anthropic Messages
/// <see cref="MultiTurnAgentLoop"/> routed through the GitHub Copilot backend (mirrors the Copilot-backed
/// Anthropic wiring in <c>LmStreaming.Sample</c>). The bearer token comes from the local GitHub Copilot /
/// <c>gh</c> CLI login via <see cref="CliCredentialCopilotTokenProvider"/> — no API key / base URL config
/// knob. The registry is empty — the daemon's agents are collect-only text agents that reason over the diff
/// the executor supplies and never call tools.
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

    public IMultiTurnAgent Create(AgentProfile profile, string? modelId, string threadId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        // Anthropic Messages agent over the Copilot backend (POST {copilot-host}/v1/messages); shared across
        // loops. The model id (e.g. claude-sonnet-5) is supplied per run via defaultOptions below.
        var providerAgent = GetSharedAgent();

        var loop = new MultiTurnAgentLoop(
            providerAgent,
            new FunctionRegistry(),
            threadId,
            systemPrompt: profile.SystemPrompt,
            // MaxToken must leave room for BOTH the adaptive-model reasoning and the answer, and the
            // effort (output_config.effort) caps the reasoning so the review text is not starved — without
            // a low effort the adaptive model spends the whole budget reasoning over a large diff and
            // returns no text.
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = modelId ?? string.Empty,
                MaxToken = _options.ReviewMaxTokens,
                ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add(
                    "OutputConfig", new AnthropicOutputConfig { Effort = _options.ReviewReasoningEffort }),
            },
            logger: _loggerFactory.CreateLogger<MultiTurnAgentLoop>());

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

        return loop;
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
