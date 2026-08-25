using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using Scriban;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds the daemon's <see cref="AgentProfile"/> records declaratively (plan §4). A profile is a
/// pure value — identity, system prompt, and tool gating — so this factory is fully unit-testable and
/// carries no provider/sandbox wiring. The live agent loop (provider client + MCP tools) is assembled
/// by the orchestrator's stage executor, which feeds the profile produced here into the shared
/// <see cref="Agents"/> pool; keeping construction out of the profile is what lets the profile stay a
/// plain declaration with no inheritance or runtime override.
/// <para>
/// The three system prompts are YAML/Scriban templates (<c>Prompts/daemon-prompts.yaml</c>, embedded as
/// <c>CodeReviewDaemon.Sample.Prompts.daemon-prompts.yaml</c>) read once via <see cref="IPromptReader"/>,
/// not C# literals — the review prompt's "Workspace layout" section templates each run's concrete
/// checkout/store/notes paths in, so the agent is TOLD where to read from and where it may write instead
/// of guessing.
/// </para>
/// </summary>
internal static class DaemonAgentFactory
{
    /// <summary>Stable id of the review profile (used for logging and agent recreation).</summary>
    public const string ReviewProfileId = "review";

    /// <summary>Stable id of the judge profile.</summary>
    public const string JudgeProfileId = "judge";

    /// <summary>Stable id of the at-close knowledge-extraction profile (design §1/§2).</summary>
    public const string KnowledgeExtractionProfileId = "knowledge-extraction";

    /// <summary>Stable id of the at-close per-developer review-feedback profile.</summary>
    public const string ReviewFeedbackExtractionProfileId = "review-feedback-extraction";

    private static readonly IPromptReader Prompts = new PromptReader(
        typeof(DaemonAgentFactory).Assembly, "CodeReviewDaemon.Sample.Prompts.daemon-prompts.yaml");

    /// <summary>
    /// Builds the review-agent profile with no run-specific workspace variables — the templated
    /// "Workspace layout" section renders blank. Prefer <see cref="CreateReviewProfile(IReadOnlyDictionary{string,object})"/>
    /// so the agent is told this run's concrete checkout/store/notes paths.
    /// </summary>
    public static AgentProfile CreateReviewProfile() =>
        // Seed the display name so the "You are <bot_name>, …" opening renders cleanly even on this
        // variable-less path; the daemon always overrides it with CodeReviewDaemonOptions.BotName.
        CreateReviewProfile(new Dictionary<string, object> { ["bot_name"] = "Revobot" });

    /// <summary>
    /// Builds the review-agent profile, rendering the YAML template's <c>checkout_root</c>/<c>has_store</c>/
    /// <c>store_root</c>/<c>has_notes</c>/<c>notes_dir</c> placeholders from <paramref name="variables"/> so
    /// the agent is told exactly where this run's code is checked out and — when it has a scoped-write
    /// tool context — the one location it may write to. The reviewer needs no provider built-in tools (it
    /// reasons over the diff the executor supplies), so <see cref="AgentProfile.EnabledBuiltInTools"/> is
    /// empty; the MCP tool allow-list (<see cref="AgentProfile.EnabledTools"/>) is left to the
    /// capability-enforcing executor, which is the layer that knows the concrete sandbox tool names.
    /// <para>
    /// The first turn of a review is COLLECT-ONLY by construction: whatever the caller's actual posting
    /// intent is, <c>should_post</c> is forced to <see langword="false"/> here so no rendering of this
    /// profile can ever instruct the agent to deliver. Delivery belongs exclusively to
    /// <see cref="CreateSynthesisPrompt"/>, which is the only turn whose answer is complete.
    /// </para>
    /// </summary>
    public static AgentProfile CreateReviewProfile(IReadOnlyDictionary<string, object> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var collectOnly = new Dictionary<string, object>(variables) { ["should_post"] = false };

        return new AgentProfile(
            Id: ReviewProfileId,
            Name: "Review Agent",
            SystemPrompt: Prompts.GetPrompt("review").PromptText(collectOnly),
            EnabledTools: null,
            EnabledBuiltInTools: []);
    }

    /// <summary>
    /// Renders the synthesis prompt (the <c>synthesis</c> template) — the second and AUTHORITATIVE turn of a
    /// review — from the same <paramref name="variables"/> the review profile used, plus
    /// <paramref name="subAgentInventory"/>: the safe roster of the children that settled behind the
    /// completion barrier (see <see cref="ReviewSubAgentTreeSnapshot.ToSafeInventory"/>).
    /// <para>
    /// It carries the posting instructions too (under <c>should_post</c>, provider-aware via <c>is_ado</c>).
    /// This is the ONE turn that both writes the review of record and delivers it, because the agent
    /// reliably WRITES a review but often SKIPS posting it (observed live) — and because the provisional
    /// turn's answer is by construction incomplete, so posting could never have belonged there.
    /// </para>
    /// </summary>
    public static string CreateSynthesisPrompt(
        IReadOnlyDictionary<string, object> variables, string subAgentInventory)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentInventory);

        var withInventory = new Dictionary<string, object>(variables)
        {
            ["sub_agent_inventory"] = subAgentInventory,
        };
        return Prompts.GetPrompt("synthesis").PromptText(withInventory);
    }

    /// <summary>
    /// Builds the review profile for one A/B <paramref name="variant"/>. The variant's
    /// <see cref="ReviewVariant.SystemPrompt"/> becomes the profile's prompt — the prompt/skill axis of
    /// the comparison — while tool gating stays identical to <see cref="CreateReviewProfile()"/> (no
    /// built-ins; MCP allow-list deferred to the executor). The <see cref="ReviewVariant.ModelId"/> and
    /// the <see cref="ReviewVariant.CanWrite"/> capability are applied by the executor, not the profile,
    /// so the profile stays a plain declaration. When <paramref name="variables"/> is supplied, the variant
    /// prompt is rendered through the same Scriban engine as the primary review template (so a variant
    /// prompt may carry its own <c>{{ checkout_root }}</c>-style placeholders); when omitted, the prompt is
    /// used verbatim.
    /// </summary>
    public static AgentProfile CreateVariantProfile(
        ReviewVariant variant, IReadOnlyDictionary<string, object>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant.SystemPrompt);

        var systemPrompt = variables is null
            ? variant.SystemPrompt
            : Template.Parse(variant.SystemPrompt).Render(new Dictionary<string, object>(variables));

        return new AgentProfile(
            Id: $"{ReviewProfileId}-{variant.VariantId}",
            Name: $"Review Agent ({variant.VariantId})",
            SystemPrompt: systemPrompt,
            EnabledTools: null,
            EnabledBuiltInTools: []);
    }

    /// <summary>
    /// Builds the judge-agent profile (grades a review, emits a bounded JSON verdict). Like the
    /// reviewer it needs no built-in tools and defers any MCP allow-list to the executor.
    /// <para>
    /// The version is PINNED rather than left to default. <c>GetPrompt</c> defaults to
    /// <c>"latest"</c>, which resolves to the highest version number present — so adding the
    /// anchored <c>judge: v2.0</c> alongside it would have rerouted the live judge with no code
    /// change and nothing failing. v2.0 ships dark until #321 switches it on deliberately.
    /// </para>
    /// </summary>
    public static AgentProfile CreateJudgeProfile() =>
        new(
            Id: JudgeProfileId,
            Name: "Judge Agent",
            SystemPrompt: Prompts.GetPrompt("judge", "v1.0").PromptText(),
            EnabledTools: null,
            EnabledBuiltInTools: []);

    /// <summary>
    /// Builds the at-close knowledge-extraction profile (design §1/§2). Its prompt carries the durable
    /// knowledge gate (<c>NO_KNOWLEDGE</c> sentinel) and the header-marker contract
    /// (<c>## SCOPE/TITLE/TAGS/UPDATES</c>) the daemon parses; frontmatter is injected by the daemon, not
    /// the model, so the prompt forbids it. Like the reviewer it needs no built-in tools and defers any
    /// MCP allow-list to the executor.
    /// </summary>
    public static AgentProfile CreateKnowledgeExtractionProfile() =>
        new(
            Id: KnowledgeExtractionProfileId,
            Name: "Knowledge Extraction Agent",
            SystemPrompt: Prompts.GetPrompt("knowledge-extraction").PromptText(),
            EnabledTools: null,
            EnabledBuiltInTools: []);

    /// <summary>
    /// Builds the at-close per-developer review-feedback profile. Its prompt carries the
    /// <c>NO_FEEDBACK</c> gate and the <c>## PATTERNS</c> contract, and — unlike
    /// <see cref="CreateKnowledgeExtractionProfile"/> — emits <b>no path markers at all</b>: the record's
    /// location is derived by the daemon from the provider-supplied PR author, so no component of the
    /// output path ever comes from the model. Frontmatter is daemon-injected, so the prompt forbids it.
    /// Like the reviewer it needs no built-in tools and defers any MCP allow-list to the executor.
    /// </summary>
    public static AgentProfile CreateReviewFeedbackExtractionProfile() =>
        new(
            Id: ReviewFeedbackExtractionProfileId,
            Name: "Review Feedback Extraction Agent",
            SystemPrompt: Prompts.GetPrompt("review-feedback-extraction").PromptText(),
            EnabledTools: null,
            EnabledBuiltInTools: []);
}
