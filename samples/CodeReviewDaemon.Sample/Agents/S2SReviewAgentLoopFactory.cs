using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The S2S-backed <see cref="IReviewAgentLoopFactory"/>: instead of assembling an in-process
/// <c>MultiTurnAgentLoop</c>, it returns an
/// <see cref="S2SReviewAgent"/> that drives the review as a real conversation on a running
/// <b>LmStreaming.Sample</b> review host over REST. That is what makes the review a live LmStreaming
/// conversation (parent loop + <c>code-reviewer:*</c> sub-agent tree) reachable from the deep-link the
/// executor posts on the PR.
/// <para>
/// Provision carries <b>no per-request model field</b> (<c>ProvisionConversationRequest</c> is only
/// <c>{WorkspaceId, ProviderId, ModeId, SystemPromptAppendix}</c>) — but on this host a Copilot-discovered
/// <c>ProviderId</c> <b>IS</b> the model id: the host registers every discovered Copilot model as its own
/// provider keyed by its raw id, persists the provisioned <c>ProviderId</c> as thread metadata, and later
/// builds that thread's agent with <c>GenerateReplyOptions.ModelId = copilotModelInfo.Id</c>. So
/// <see cref="Create"/>'s <c>modelId</c> IS forwarded — as the conversation's provider id, falling back to
/// <see cref="CodeReviewDaemonOptions.LmStreamingProviderId"/> when the caller names none. Without that
/// forwarding the overflow-escalation ladder in <c>DaemonReviewStageExecutor</c> re-ran the SAME model it
/// had just overflowed, on a fresh thread, and called it an escalation.
/// </para>
/// <para>
/// <c>reasoningEffort</c> and <c>toolContext</c> are still <b>not</b> forwarded, and cannot be: the S2S
/// surface has no carrier for either. Neither <c>ProvisionConversationRequest</c> nor
/// <c>SendMessageRequest</c> (<c>{Text, SuppressSubAgentSpawning, IdempotencyKey}</c>) has an effort or a
/// tool field, so there is nowhere to put them; the hosted workspace-agent mode owns tool exposure, the
/// sub-agent catalog and per-turn thinking effort. Adding a parameter here that the wire cannot carry would
/// make a dead knob look live, which is worse than one that is visibly dead — so the omission is explicit
/// and this is the place to start if the host ever grows those fields. The load-bearing inputs are
/// <c>workspaceId</c> — the per-PR
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
    private readonly ILogger<S2SReviewAgentLoopFactory> _logger;
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
        _logger = _loggerFactory.CreateLogger<S2SReviewAgentLoopFactory>();
        _onConversationMinted = onConversationMinted;
        WarnIfReviewModelDisagreesWithProvider(_options, _logger);
    }

    /// <summary>
    /// Startup guard for the two configuration strings that BOTH mean "the review model" and that nothing
    /// makes agree: <see cref="CodeReviewDaemonOptions.ReviewModelId"/> — which becomes the run's
    /// <c>ModelId</c> and is what <c>ReviewProgressReporter</c> prints on the live
    /// <c>reviewing ({model})</c> line — and <see cref="CodeReviewDaemonOptions.LmStreamingProviderId"/>,
    /// the conversation's fallback provider id and therefore the model the hosted review actually runs on
    /// when a run names none. They are equal in every shipped S2S profile, so the progress line has told the
    /// truth only by coincidence; if they ever diverge the log names one model while another does the
    /// reviewing, and nothing anywhere says so. This is a warning rather than a throw because a deployment
    /// may legitimately want the two split — but it can no longer happen quietly. Logged once, at singleton
    /// construction (daemon boot).
    /// </summary>
    private static void WarnIfReviewModelDisagreesWithProvider(
        CodeReviewDaemonOptions options,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.ReviewModelId)
            || string.IsNullOrWhiteSpace(options.LmStreamingProviderId)
            || string.Equals(
                options.ReviewModelId,
                options.LmStreamingProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logger.LogWarning(
            "CodeReviewDaemon:ReviewModelId ({ReviewModelId}) and CodeReviewDaemon:LmStreamingProviderId "
                + "({LmStreamingProviderId}) disagree, and both mean 'the review model'. ReviewModelId is "
                + "what the progress line reports and what a run carries as its model; "
                + "LmStreamingProviderId is only the fallback when a run names no model. Set them to the "
                + "same id unless the split is deliberate.",
            options.ReviewModelId,
            options.LmStreamingProviderId);
    }

    public IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null,
        PreparedReviewWorkspace? reviewWorkspace = null,
        string? resumeHostedThreadId = null)
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

        // The conversation's provider id IS its model on this host (see the class remarks), so a caller that
        // names a model gets that model. Blank/absent falls back to the configured provider — which is the
        // same id in every shipped S2S profile, so the ordinary review is unchanged; this only bites where a
        // caller deliberately names a different model, i.e. the overflow-escalation ladder.
        var providerId = string.IsNullOrWhiteSpace(modelId) ? _options.LmStreamingProviderId : modelId;

        return new S2SReviewAgent(
            _client,
            reviewWorkspace.WorkspaceId,
            providerId,
            _options.LmStreamingModeId,
            systemPrompt: profile.SystemPrompt,
            title: title,
            logger: _loggerFactory.CreateLogger<S2SReviewAgent>(),
            onConversationMinted: recorder is null ? null : minted => recorder(minted, title),
            // A resumed review rejoins the conversation it already minted: no second provision, no second
            // retention row, no second deep-link. Null (the common case) provisions lazily as before.
            existingThreadId: resumeHostedThreadId,
            // The operator's configured model for the review sub-agents (#529). Conversation-scoped, so it
            // only takes effect on a conversation this call actually provisions; a RESUMED review keeps
            // whatever the original provision set, which is correct — its sub-agent tree already exists.
            subAgentModelId: _options.SubAgentModelId);
    }

    /// <summary>
    /// A human-readable conversation title for the review-host UI (best-effort; see
    /// <see cref="S2SReviewAgent"/>). Leads with the PR — <c>Review PR #123</c> — because the title is the
    /// ONLY thing identifying the conversation to a judge who arrived from the deep-link posted on that PR;
    /// a title carrying just the agent name leaves several PRs' reviews indistinguishable in the
    /// conversation list. The profile's display name follows so the parent review and its judge/variant
    /// reruns (separate conversations against the same workspace) stay distinguishable.
    /// </summary>
    /// <summary>
    /// The model the hosted conversation will actually run on — which is now the requested id whenever the
    /// caller names one, because <see cref="Create"/> provisions with it as the conversation's provider id
    /// and a Copilot-discovered provider id IS a model id on this host. A caller that names nothing gets
    /// the configured provider, exactly as before. It stays prefixed so no reader mistakes the answer for a
    /// bare per-call model id: it names what the daemon asked the review host to run.
    /// <para>
    /// Answers <c>null</c> whenever no provider is configured, and that is deliberately independent of the
    /// requested id. <see cref="Create"/> throws in that state rather than provisioning, so no model runs at
    /// all — naming the requested one here would report a measurement that never happened.
    /// </para>
    /// </summary>
    public string? ResolveEffectiveModelId(string? requestedModelId) =>
        string.IsNullOrWhiteSpace(_options.LmStreamingProviderId)
            ? null
            : $"lmstreaming:{(string.IsNullOrWhiteSpace(requestedModelId)
                ? _options.LmStreamingProviderId
                : requestedModelId)}";

    /// <summary>
    /// <c>true</c>, and structurally so rather than by configuration: <see cref="Create"/> forwards its
    /// <c>modelId</c> as the provisioned <c>ProviderId</c>, which on this host selects the model. A caller's
    /// per-call id therefore does leave the daemon process and does decide what runs, so the executor may
    /// record it as the model that ran.
    /// <para>
    /// This was <c>false</c> while the id was discarded, and flipping it is the point of the change rather
    /// than a side effect: the executor gates its persisted model provenance on this answer, so an
    /// escalation used to be checkpointed against a model the review host was never asked for.
    /// </para>
    /// </summary>
    public bool HonoursRequestedModelId => true;

    private static string BuildTitle(AgentProfile profile, PreparedReviewWorkspace workspace)
    {
        var pr = $"Review PR #{workspace.PrId}";
        return string.IsNullOrWhiteSpace(profile.Name) ? pr : $"{pr} — {profile.Name}";
    }
}
