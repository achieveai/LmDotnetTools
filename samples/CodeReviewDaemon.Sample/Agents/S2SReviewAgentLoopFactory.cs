using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The S2S-backed <see cref="IReviewAgentLoopFactory"/> (selected when
/// <see cref="CodeReviewDaemonOptions.UseS2SReviewAgent"/> is on): instead of assembling an in-process
/// <c>MultiTurnAgentLoop</c> like <see cref="LiveReviewAgentLoopFactory"/>, it returns an
/// <see cref="S2SReviewAgent"/> that drives the review as a real conversation on a running
/// <b>LmStreaming.Sample</b> review host over REST. That is what makes the review a live LmStreaming
/// conversation (parent loop + <c>code-reviewer:*</c> sub-agent tree) reachable from the deep-link the
/// executor posts on the PR.
/// <para>
/// Provision carries <b>no per-request model field</b> (<c>ProvisionConversationRequest</c> is only
/// <c>{WorkspaceId, ProviderId, ModeId}</c>): the review model is whatever the configured
/// <see cref="CodeReviewDaemonOptions.LmStreamingProviderId"/> resolves on the review host, so the
/// per-call <c>modelId</c>/<c>reasoningEffort</c>/<c>toolContext</c> arguments of <see cref="Create"/>
/// are intentionally not forwarded — the hosted workspace-agent conversation owns model selection, tool
/// exposure, and the sub-agent catalog. The load-bearing input is <c>workspaceId</c>: the
/// per-PR LmStreaming workspace <see cref="S2SReviewWorkspacePreparer"/> pointed at the daemon's host
/// clone. <c>profile.SystemPrompt</c> and the review input flow to the host as the conversation's system
/// prompt title and the sent user message respectively (the system prompt is set on the workspace-agent
/// mode host-side; the review body rides <see cref="S2SReviewAgent.ExecuteRunAsync"/>).
/// </para>
/// <para>
/// Like the live factory this does no work at construction (the agent provisions lazily on first run), so
/// registering it cannot affect daemon boot or the route surface.
/// </para>
/// </summary>
internal sealed class S2SReviewAgentLoopFactory : IReviewAgentLoopFactory
{
    private readonly LmStreamingS2SClient _client;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public S2SReviewAgentLoopFactory(
        LmStreamingS2SClient client,
        CodeReviewDaemonOptions options,
        ILoggerFactory loggerFactory)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null,
        PreparedReviewWorkspace? reviewWorkspace = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // The workspace is prepared per run by S2SReviewWorkspacePreparer and threaded through here. Its
        // absence means the executor did not run the preparer (a wiring bug, not a valid diff-only path — the
        // whole point of the S2S factory is a workspace-bound hosted review), so fail loudly rather than
        // silently provisioning against nothing.
        if (reviewWorkspace is null)
        {
            throw new ArgumentException(
                "S2SReviewAgentLoopFactory requires a prepared review workspace; it was not prepared for this run.",
                nameof(reviewWorkspace));
        }

        if (string.IsNullOrWhiteSpace(_options.LmStreamingProviderId))
        {
            throw new InvalidOperationException(
                "UseS2SReviewAgent is on but LmStreamingProviderId is not configured; set it to a review-host "
                + "provider that resolves the intended review model.");
        }

        return new S2SReviewAgent(
            _client,
            reviewWorkspace.WorkspaceId,
            _options.LmStreamingProviderId,
            _options.LmStreamingModeId,
            title: BuildTitle(profile, reviewWorkspace),
            logger: _loggerFactory.CreateLogger<S2SReviewAgent>());
    }

    /// <summary>
    /// A human-readable conversation title for the review-host UI (best-effort; see
    /// <see cref="S2SReviewAgent"/>). Leads with the PR — <c>Review PR #123</c> — because the title is the
    /// ONLY thing identifying the conversation to a judge who arrived from the deep-link posted on that PR;
    /// a title carrying just the agent name leaves several PRs' reviews indistinguishable in the
    /// conversation list. The profile's display name follows so the parent review and its judge/variant
    /// reruns (separate conversations against the same workspace) stay distinguishable.
    /// </summary>
    private static string BuildTitle(AgentProfile profile, PreparedReviewWorkspace workspace)
    {
        var pr = $"Review PR #{workspace.PrId}";
        return string.IsNullOrWhiteSpace(profile.Name) ? pr : $"{pr} — {profile.Name}";
    }
}
