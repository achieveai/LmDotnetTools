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
/// exposure, and the sub-agent catalog. The load-bearing inputs are <c>workspaceId</c> — the per-PR
/// LmStreaming workspace <see cref="S2SReviewWorkspacePreparer"/> pointed at the daemon's host clone — and
/// <c>profile.SystemPrompt</c>, which rides provision as the conversation's <b>system prompt appendix</b>
/// (the host appends it to the workspace-agent mode's own prompt) while the review body rides the sent user
/// message. Both halves are required: the hosted mode supplies the workspace, tools and sub-agent catalog,
/// and the profile prompt supplies the review methodology, the "dispatch the <c>code-reviewer:*</c>
/// sub-agents" instruction and the output contract. Dropping the prompt yields a run that reads the diff and
/// answers generically — it looks like a working review and is not one.
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
    private readonly Action<string, string?>? _onConversationMinted;

    /// <summary>
    /// Builds the factory. <c>onConversationMinted</c> is the optional deep-link retention recorder, invoked
    /// with <c>(threadId, title)</c> the moment a hosted conversation is provisioned. Every arm this factory
    /// builds — the review, the judge, each A/B variant — mints its own conversation, and only the review's
    /// thread id ever reaches a persisted artifact, so this is the one hook that sees them all. Null (the
    /// default) keeps every conversation forever.
    /// </summary>
    public S2SReviewAgentLoopFactory(
        LmStreamingS2SClient client,
        CodeReviewDaemonOptions options,
        ILoggerFactory loggerFactory,
        Action<string, string?>? onConversationMinted = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _onConversationMinted = onConversationMinted;
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

        if (string.IsNullOrWhiteSpace(profile.SystemPrompt))
        {
            throw new ArgumentException(
                "The review profile carries no system prompt; the hosted conversation would run the review "
                + "under a generic workspace-agent prompt with no methodology, sub-agent dispatch or output "
                + "contract.",
                nameof(profile));
        }

        var title = BuildTitle(profile, reviewWorkspace);
        var recorder = _onConversationMinted;

        return new S2SReviewAgent(
            _client,
            reviewWorkspace.WorkspaceId,
            _options.LmStreamingProviderId,
            _options.LmStreamingModeId,
            systemPrompt: profile.SystemPrompt,
            title: title,
            logger: _loggerFactory.CreateLogger<S2SReviewAgent>(),
            onConversationMinted: recorder is null ? null : minted => recorder(minted, title));
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
