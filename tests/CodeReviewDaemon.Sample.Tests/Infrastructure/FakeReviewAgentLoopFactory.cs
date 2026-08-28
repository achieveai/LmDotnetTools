using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IReviewAgentLoopFactory"/>. Returns a scripted <see cref="FakeMultiTurnAgent"/>
/// per call so the executor's agent logic is verifiable without a live provider loop. The assistant
/// text is chosen per <see cref="AgentProfile.Id"/> (<see cref="TextByProfileId"/>), falling back to
/// <see cref="DefaultText"/>, which lets one test script a JSON judge verdict while the reviewer returns
/// prose. Every created profile id is recorded in <see cref="CreatedProfileIds"/>.
/// </summary>
internal sealed class FakeReviewAgentLoopFactory : IReviewAgentLoopFactory
{
    /// <summary>Assistant text to return for a given <see cref="AgentProfile.Id"/>.</summary>
    public Dictionary<string, string> TextByProfileId { get; } = new(StringComparer.Ordinal);

    /// <summary>Assistant text returned when no per-profile override is set.</summary>
    public string DefaultText { get; set; } = "## Review\nMust: null check missing in Foo.cs:10.";

    /// <summary>Profile ids passed to <see cref="Create"/>, in call order.</summary>
    public List<string> CreatedProfileIds { get; } = [];

    /// <summary>The full <see cref="AgentProfile"/>s passed to <see cref="Create"/>, in call order — lets a
    /// test assert on the rendered <see cref="AgentProfile.SystemPrompt"/> the executor built (e.g. the
    /// templated workspace-layout paths), not just the profile id.</summary>
    public List<AgentProfile> CreatedProfiles { get; } = [];

    /// <summary>Thread ids passed to <see cref="Create"/>, in call order.</summary>
    public List<string> ThreadIds { get; } = [];

    /// <summary>Reasoning-effort values passed to <see cref="Create"/>, in call order (null = default).</summary>
    public List<string?> ReasoningEfforts { get; } = [];

    /// <summary>Tool contexts passed to <see cref="Create"/>, in call order (null = diff-only path).</summary>
    public List<ReviewToolContext?> ToolContexts { get; } = [];

    /// <summary>The scripted agents returned by <see cref="Create"/>, in call order, so a test can
    /// inspect the <see cref="FakeMultiTurnAgent.ReceivedInputs"/> the executor sent each one.</summary>
    public List<FakeMultiTurnAgent> CreatedAgents { get; } = [];

    /// <summary>When set, every scripted agent is passed through this hook before being returned, so a test
    /// can configure its sub-agent surface and/or WRAP it in a decorator — which is what the live
    /// tool-assisted path does (<c>ToolScopedReviewLoop</c>). <see cref="CreatedAgents"/> still records the
    /// scripted agent underneath, not the decorator.</summary>
    public Func<FakeMultiTurnAgent, IMultiTurnAgent>? DecorateCreatedAgent { get; set; }

    /// <summary>When set, a tool-assisted <see cref="Create"/> (non-null <c>toolContext</c>) returns an agent
    /// that THROWS this exception instead of scripted text — models the model API rejecting the accumulated
    /// tool-assisted context (e.g. a context-window 400) so the executor's diff-only degrade is exercised.
    /// The diff-only path (null <c>toolContext</c>) still returns scripted text.</summary>
    public Exception? ThrowWhenToolAssisted { get; set; }

    /// <summary>When set, <see cref="ThrowWhenToolAssisted"/> fires ONLY for a tool-assisted <see cref="Create"/>
    /// whose <c>modelId</c> equals this value — models a smaller model overflowing while the escalation model
    /// (e.g. gpt-5.6-terra) succeeds. When null it fires for every tool-assisted Create regardless of model.</summary>
    public string? ThrowOnlyForModel { get; set; }

    /// <summary>Model ids passed to <see cref="Create"/>, in call order (null = the run's configured model).</summary>
    public List<string?> ModelIds { get; } = [];

    /// <summary>
    /// When set, the effective model is this value NO MATTER what model id the caller passes — which is
    /// what the only production factory does: S2S provision carries no model field, so
    /// <c>S2SReviewAgentLoopFactory.Create</c> discards <c>modelId</c> and the host resolves the model from
    /// the configured provider. Left null the fake honours the requested id, modelling a transport that
    /// can select per call. A caller that reads the requested id instead of the effective one looks
    /// correct against the honouring fake and writes a false claim in production, so the discarding case
    /// has to be expressible here.
    /// </summary>
    public string? EffectiveModelIdOverride { get; set; }

    /// <summary>Workspace ids passed to <see cref="Create"/>, in call order (null = in-process path, no S2S workspace).</summary>
    public List<string?> WorkspaceIds { get; } = [];

    /// <summary>Hosted thread ids passed to <see cref="Create"/>, in call order (null = provision a fresh
    /// hosted conversation rather than resume a persisted one).</summary>
    public List<string?> ResumeHostedThreadIds { get; } = [];

    /// <summary>
    /// When true, every created loop is wrapped in a <see cref="ResumableFakeLoop"/> — the hosted (S2S) path,
    /// whose turns are durable on a process-outliving host. Left false the double is deliberately NON-resumable,
    /// which is exactly what an in-process loop is; the executor must then neither arm nor checkpoint a turn.
    /// The fixture sets it from <c>UseS2SReviewAgent</c> so the two paths differ here as they do in production.
    /// </summary>
    public bool Resumable { get; set; }

    /// <summary>The resumable decorators returned by <see cref="Create"/>, in call order — empty unless
    /// <see cref="Resumable"/>. This is where the turn-checkpointing assertions live, because this is the
    /// object that implements the capability.</summary>
    public List<ResumableFakeLoop> ResumableLoops { get; } = [];

    public IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null,
        PreparedReviewWorkspace? reviewWorkspace = null,
        string? resumeHostedThreadId = null)
    {
        CreatedProfileIds.Add(profile.Id);
        CreatedProfiles.Add(profile);
        ThreadIds.Add(threadId);
        ReasoningEfforts.Add(reasoningEffort);
        ToolContexts.Add(toolContext);
        ModelIds.Add(modelId);
        WorkspaceIds.Add(reviewWorkspace?.WorkspaceId);
        ResumeHostedThreadIds.Add(resumeHostedThreadId);

        if (toolContext is not null && ThrowWhenToolAssisted is not null
            && (ThrowOnlyForModel is null || string.Equals(modelId, ThrowOnlyForModel, StringComparison.Ordinal)))
        {
            var throwing = FakeMultiTurnAgent.Throwing($"run-{profile.Id}-overflow", ThrowWhenToolAssisted);
            CreatedAgents.Add(throwing);
            return Decorate(throwing, threadId, resumeHostedThreadId);
        }

        var text = TextByProfileId.TryGetValue(profile.Id, out var scripted) ? scripted : DefaultText;
        var runId = $"run-{profile.Id}";
        var agent = new FakeMultiTurnAgent(runId, new TextMessage { Text = text, Role = Role.Assistant, RunId = runId });
        CreatedAgents.Add(agent);
        return Decorate(agent, threadId, resumeHostedThreadId);
    }

    /// <summary>Models a transport that substitutes its own selection when <see cref="EffectiveModelIdOverride"/>
    /// is set, and one that answers with the request otherwise. Note the real S2S factory does neither exactly:
    /// it forwards the request but answers a <c>lmstreaming:</c>-prefixed id, so a test that turns on the exact
    /// string production returns belongs against the real factory rather than here.</summary>
    public string? ResolveEffectiveModelId(string? requestedModelId) =>
        EffectiveModelIdOverride ?? requestedModelId;

    /// <summary>
    /// Whether <see cref="Create"/> is modelled as RUNNING the model id it is handed. Default <c>true</c> —
    /// a transport that can select per call, which is what the production S2S factory now is — so a test
    /// about attribution has to say which of the two it is exercising.
    /// <para>
    /// Deliberately a knob of its own rather than derived from <see cref="EffectiveModelIdOverride"/>: the
    /// interface keeps "does Create run what it was asked for?" separate from "what identity can the
    /// transport name?" precisely because those come apart, and a double that welds them together could not
    /// tell a test which of the two a passing assertion actually turned on.
    /// </para>
    /// </summary>
    public bool HonoursRequestedModelId { get; set; } = true;

    /// <summary>The minted conversation id is derived from the daemon-local thread id, so it is deterministic
    /// (a test can predict it) yet distinct per A/B arm and escalation rung, exactly as a real host's would be.</summary>
    private IMultiTurnAgent Decorate(FakeMultiTurnAgent agent, string threadId, string? resumeHostedThreadId)
    {
        var decorated = DecorateCreatedAgent?.Invoke(agent) ?? agent;
        if (!Resumable)
        {
            return decorated;
        }

        var loop = new ResumableFakeLoop(decorated, resumeHostedThreadId, $"hosted-{threadId}");
        ResumableLoops.Add(loop);
        return loop;
    }
}
