namespace CodeReviewDaemon.Sample.Configuration;

/// <summary>
/// Operator-facing feature flags for the daemon, bound from the <c>CodeReviewDaemon</c> configuration
/// section. Every flag defaults to its <b>conservative</b> setting so a freshly-deployed daemon is
/// safe-by-default: it collects review output without posting, runs only the primary review agent
/// against GitHub, and reviews nothing until a repo is explicitly allow-listed. Each flag is a
/// deliberate operator opt-in to a higher-blast-radius behavior.
/// </summary>
internal sealed class CodeReviewDaemonOptions
{
    /// <summary>Configuration section name: <c>CodeReviewDaemon</c>.</summary>
    public const string SectionName = "CodeReviewDaemon";

    /// <summary>
    /// When <c>false</c> (default) the daemon is <b>collect-only</b>: review output is persisted but no
    /// comments are posted to the PR. Posting to a live PR is an outward-facing action, so it stays off
    /// until an operator explicitly enables it.
    /// </summary>
    public bool EnableCommentPosting { get; init; }

    /// <summary>When <c>false</c> (default) the knowledge-base agent does not run.</summary>
    public bool EnableKnowledgeAgent { get; init; }

    /// <summary>When <c>false</c> (default) the judge agent does not run (no grading is persisted).</summary>
    public bool EnableJudgeAgent { get; init; }

    /// <summary>
    /// When <c>false</c> (default) only the primary review variant runs. Enabling it adds the
    /// collect-only A/B variant (which never posts or pushes — see the capability-enforced A/B design).
    /// </summary>
    public bool EnableABVariants { get; init; }

    /// <summary>
    /// When <c>false</c> (default) the Azure DevOps provider is not registered, so the daemon is
    /// GitHub-only and an <c>ado</c> webhook call is denied as an unknown provider. Enabling it
    /// registers the ADO OAuth provider and (later) its poller.
    /// </summary>
    public bool EnableAdoProvider { get; init; }

    /// <summary>
    /// Allow-list of <c>owner/repo</c> (GitHub) or <c>org/project/repo</c> (ADO) identifiers the daemon
    /// is permitted to review. Empty (default) means <b>review nothing</b> — a repo must be explicitly
    /// added before the daemon will poll or review it.
    /// </summary>
    public IReadOnlyList<string> EnabledRepos { get; init; } = [];

    /// <summary>
    /// Path to the SQLite orchestration database. When unset (default) the daemon uses
    /// <c>review.db</c> under <see cref="AppContext.BaseDirectory"/>. Tests override it to a throwaway
    /// file so the store's migrate-on-construction side effect stays isolated.
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// Model id the primary review agent runs with (the id sent to the Copilot-backed Anthropic Messages
    /// backend, e.g. <c>claude-sonnet-5</c>). The poller stamps it onto each review run so the primary
    /// review has a concrete model — an empty id would be rejected by the provider. The A/B comparison
    /// (B) variant keeps its own bounded model id and is unaffected by this knob.
    /// </summary>
    public string ReviewModelId { get; init; } = "claude-sonnet-5";

    /// <summary>
    /// Model id for the collect-only A/B comparison (B) variant (<see cref="EnableABVariants"/>). Must be a
    /// model the configured backend accepts — the Copilot backend rejects OpenRouter-style slugs
    /// (e.g. <c>anthropic/claude-haiku-4-5</c>) with <c>model_not_supported</c>; its haiku id is
    /// <c>claude-haiku-4.5</c>. The B variant is the model axis of the A/B, so it defaults to a cheaper
    /// model than the primary <see cref="ReviewModelId"/>.
    /// </summary>
    public string VariantModelId { get; init; } = "claude-haiku-4.5";

    /// <summary>
    /// Adaptive-thinking effort (<c>output_config.effort</c>) for the A/B (B) variant. Empty (default) omits
    /// it — the default variant model (<c>claude-haiku-4.5</c>) is not an adaptive-thinking model and
    /// rejects an effort it does not support. Set this only if <see cref="VariantModelId"/> is pointed at
    /// an adaptive model that needs its reasoning bounded.
    /// </summary>
    public string VariantReasoningEffort { get; init; } = "";

    /// <summary>
    /// Max output tokens for a review turn. Copilot's adaptive Claude models emit reasoning before the
    /// answer, and that reasoning counts against the token budget — the provider default (4096) is easily
    /// exhausted by reasoning over a large diff, leaving no room for the review text (an empty review).
    /// The generous default gives both the reasoning and the answer room. It is a cap, not a target, so a
    /// single value suits the review, judge, and knowledge agents alike. Raised from the diff-only-era
    /// default because the tool-assisted path (<see cref="EnableToolAssistedReview"/>) is a multi-turn
    /// loop that also dispatches <c>code-reviewer:*</c> sub-agents — each turn's reasoning + tool-call
    /// scaffolding consumes more of the budget than a single-pass diff review.
    /// </summary>
    public int ReviewMaxTokens { get; init; } = 32000;

    /// <summary>
    /// Reasoning effort for the review agent's adaptive-thinking model (<c>output_config.effort</c>:
    /// <c>low</c> / <c>medium</c> / <c>high</c>). GitHub Copilot's adaptive Claude models reason before
    /// answering and, left uncapped, spend the whole token budget reasoning over a large diff and emit no
    /// review text. A low effort keeps reasoning short so the answer lands. Default <c>low</c>. This is
    /// the diff-only single-pass default; see <see cref="ToolAssistedReasoningEffort"/> for the
    /// tool-assisted path's default.
    /// </summary>
    public string ReviewReasoningEffort { get; init; } = "low";

    /// <summary>
    /// Reasoning effort for the review agent's adaptive-thinking model when
    /// <see cref="EnableToolAssistedReview"/> is on (<c>output_config.effort</c>: <c>low</c> /
    /// <c>medium</c> / <c>high</c>). A multi-turn loop that reads across repos, loads the
    /// <c>code-reviewer</c> skill, and dispatches sub-agents needs more reasoning headroom per turn than
    /// the single-pass diff-only reviewer, so this defaults above <see cref="ReviewReasoningEffort"/>'s
    /// <c>low</c>. Default <c>medium</c>.
    /// </summary>
    public string ToolAssistedReasoningEffort { get; init; } = "medium";

    /// <summary>
    /// Remote URL of the ReviewBot workspace repository (seeded once via <c>reviewbot init</c>). When set,
    /// a completed primary review's artifacts (<c>PRs/...</c> + the regenerated <c>KnowledgeBase/...</c>)
    /// are durably persisted onto its default branch via the one-commit retention sequence (AC#6). When
    /// unset (default) retention is <b>skipped</b>, keeping a freshly-deployed daemon inert — review
    /// output still lands in SQLite, but nothing is pushed to a git remote until an operator points the
    /// daemon at an initialized ReviewBot repo.
    /// </summary>
    public string? ReviewBotRepoUrl { get; init; }

    /// <summary>
    /// Bounds on sandbox command output, persisted artifacts, and per-command timeout (PR #121 H4). The
    /// defaults are conservative; an operator may tighten/loosen them via the
    /// <c>CodeReviewDaemon:Limits</c> sub-section.
    /// </summary>
    public SandboxLimits Limits { get; init; } = new();

    /// <summary>
    /// Maximum number of provider pages a single poll fetches before stopping (PR #121 M5). Bounds the
    /// work one poll cycle does when a repo has many open PRs; the next poll resumes from the advanced
    /// cursor. Default 10.
    /// </summary>
    public int MaxPagesPerPoll { get; init; } = 10;

    /// <summary>
    /// When <c>false</c> (default) the daemon runs the diff-only review (empty tool registry, no
    /// sub-agents, boot-lifetime sandbox session) exactly as before. Enabling it provisions a per-run
    /// sandbox session, exposes the read-only MCP tools + <c>Skill</c>, and dispatches the
    /// <c>code-reviewer:*</c> sub-agents. Opt-in because it is materially more expensive per review.
    /// </summary>
    public bool EnableToolAssistedReview { get; init; }

    /// <summary>
    /// Host directory that per-run sandbox workspaces are created under (one subdirectory per run, removed
    /// on completion). When unset (default) the daemon uses <c>workspaces</c> beside the binary.
    /// </summary>
    public string? WorkspaceHostRoot { get; init; }

    /// <summary>Plugin-marketplace aliases enabled on the per-run session. Default <c>gb-plugins</c>.</summary>
    public IReadOnlyList<string> Marketplaces { get; init; } = ["gb-plugins"];

    /// <summary>
    /// The read-only MCP tool names the review agent may call. The daemon owns all writes, so this must
    /// never include <c>Write</c>/<c>Edit</c>. Default <c>Read</c>/<c>Grep</c>/<c>Glob</c>/<c>Skill</c>.
    /// </summary>
    public IReadOnlyList<string> ReadOnlyToolAllowList { get; init; } = ["Read", "Grep", "Glob", "Skill"];
}
