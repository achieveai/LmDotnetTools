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
    /// Directory the review agents persist their full <c>MultiTurnAgentLoop</c> conversation to (one
    /// <c>&lt;threadId&gt;/messages.json</c> per primary and sub-agent thread, via
    /// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.FileConversationStore"/>) so every review's
    /// tool calls — Skill loads and sub-agent Task dispatches — are auditable after the fact (the JSON is
    /// DuckDB-queryable). When unset (default) conversations are NOT persisted: the loop streams and discards
    /// them, exactly as before.
    /// </summary>
    public string? ConversationStorePath { get; init; }

    /// <summary>
    /// When set, the daemon ALSO writes its own logs as structured JSONL (Serilog
    /// <see cref="Serilog.Formatting.Compact.CompactJsonFormatter"/>, daily-rolled) to this path — canonical
    /// <c>@t</c>/<c>@l</c>/<c>@m</c>/<c>SourceContext</c> fields, DuckDB-queryable — in addition to the console
    /// output. Unset (default) leaves only the console logger, exactly as before.
    /// </summary>
    public string? LogFilePath { get; init; }

    /// <summary>
    /// When true, a review whose sandbox session has NO <c>code-reviewer</c> sub-agent support (nothing
    /// discovered → <c>SubAgentOptions</c> would be null) is ABORTED rather than degraded to a skill-only
    /// review, and the daemon stops (<see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication"/>)
    /// — Revobot's reviews are only trustworthy WITH the code-reviewer skill + sub-agents, so a workspace
    /// that can't provide them is a fatal misconfiguration to surface, not to review through. Default false
    /// (degrade-not-fail, unchanged).
    /// </summary>
    public bool RequireSkillSupport { get; init; }

    /// <summary>
    /// Model id the <b>primary orchestrator loop</b> runs with — the "dispatcher / state" agent that reads
    /// the diff, dispatches the <c>code-reviewer:*</c> review sub-agents, holds the review's conversation
    /// state, and synthesizes the final posted review (the id sent to the Copilot backend, e.g.
    /// <c>claude-sonnet-5</c> or <c>gpt-5.6-luna</c>). The poller stamps it onto each review run so the
    /// primary review has a concrete model — an empty id would be rejected by the provider. The deep review
    /// passes can run on a different model via <see cref="SubAgentModelId"/>; the A/B comparison (B) variant
    /// keeps its own bounded model id and is unaffected by this knob.
    /// </summary>
    public string ReviewModelId { get; init; } = "claude-sonnet-5";

    /// <summary>
    /// Model id the discovered <c>code-reviewer:*</c> <b>review sub-agents</b> run with — the agents that do
    /// the focused, deep review passes the primary loop dispatches. Empty (default) ⇒ sub-agents inherit the
    /// primary loop's model (<see cref="ReviewModelId"/>), exactly as before. Set it to split the two roles:
    /// a stronger model for the actual reviewing (e.g. <c>gpt-5.6-sol</c>) while the orchestrator/dispatcher
    /// runs a lighter one (<see cref="ReviewModelId"/>, e.g. <c>gpt-5.6-luna</c>). When set it overrides
    /// whatever <c>model:</c> a discovered sub-agent's markdown declares. It must be served by the same
    /// Copilot backend the daemon uses (a <c>gpt-*</c>/<c>o*</c> id routes through OpenAI Responses, a
    /// <c>claude-*</c> id through Anthropic Messages) — an unsupported slug is rejected with
    /// <c>model_not_supported</c>.
    /// </summary>
    public string SubAgentModelId { get; init; } = "";

    /// <summary>
    /// Bigger-context model the <b>primary review loop</b> escalates to when a review attempt fails with a
    /// context-window overflow (the diff + all fanned-out sub-agent results exceed <see cref="ReviewModelId"/>'s
    /// window). On overflow the loop retries on a FRESH thread with this model (keeping the tool context), then
    /// falls back to diff-only on it if it still overflows. Default <c>gpt-5.6-terra</c> (the largest-window
    /// sibling of <c>gpt-5.6-luna</c>/<c>-sol</c>). Empty ⇒ no model escalation (fall straight back to diff-only
    /// on <see cref="ReviewModelId"/>). Must be served by the same Copilot backend as the review model.
    /// </summary>
    public string OverflowEscalationModelId { get; init; } = "gpt-5.6-terra";

    /// <summary>
    /// Maximum number of discovered <c>code-reviewer:*</c> sub-agents the review loop may run concurrently
    /// (maps to the library's <c>SubAgentOptions.MaxConcurrentSubAgents</c>). Once this many are in flight the
    /// dispatcher blocks with "Max concurrent sub-agents (N) reached" until one completes, so a higher value
    /// lets a deep review parallelize more of its focused passes — at the cost of more simultaneous model
    /// calls and gateway load. Defaults to the library default of 5.
    /// </summary>
    public int MaxConcurrentSubAgents { get; init; } = 5;

    /// <summary>
    /// Model id the at-close <b>knowledge-extraction agent</b> runs with (<see cref="EnableKnowledgeAgent"/>) —
    /// the gated pass that distils a merged PR's review notes into the Knowledge Base. Empty (default) ⇒ the
    /// extraction loop inherits the primary <see cref="ReviewModelId"/>, exactly as before. Set it to run the
    /// extraction on a dedicated model — e.g. a stronger writer like <c>claude-opus-4.8</c> — independent of
    /// the dispatcher. Like the other model knobs it must be served by the daemon's Copilot backend (a
    /// <c>claude-*</c> id routes through Anthropic Messages, a <c>gpt-*</c>/<c>o*</c> id through OpenAI
    /// Responses); an unsupported slug — or an empty request model — is rejected with <c>model_not_supported</c>.
    /// </summary>
    public string KnowledgeModelId { get; init; } = "";

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
    /// Maximum turns the primary review agent's multi-turn loop may take before it is stopped (the per-run
    /// cap handed to the review loop). The tool-assisted path reads across the checkout, loads skills, and
    /// dispatches sub-agents, so a large PR can exhaust the library default (50) before the loop ever writes
    /// its review — yielding an empty review that then posts nothing. Raised so big diffs have the headroom
    /// to finish. Applies to every loop this daemon's loop factory creates (review, judge, knowledge, and the
    /// A/B variant arm); the review sub-agents are bounded separately by their own template cap.
    /// </summary>
    public int ReviewMaxTurns { get; init; } = 150;

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
    /// When &gt; 0, the poller only reviews PRs whose recency signal falls within this many days: GitHub
    /// uses the PR's <c>updated_at</c> (true last activity); ADO's PR list has no last-activity field, so it
    /// uses <c>creationDate</c> and — for PRs opened before the window — the source branch's last-push time
    /// (the tip commit's date), fetched per-PR so an old-but-recently-pushed PR is still reviewed. A PR the
    /// provider gives no date for is always kept (never silently skipped). 0 (default) disables the filter —
    /// every open PR is reviewed. Overridable per run with the <c>--days N</c> / <c>--max-pr-age-days N</c>
    /// command-line flag, which wins over this value.
    /// </summary>
    public int MaxPrAgeDays { get; init; }

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

    /// <summary>Plugin-marketplace aliases enabled on the per-run session. Default <c>gb-plugins</c>, <c>superpowers</c>.</summary>
    public IReadOnlyList<string> Marketplaces { get; init; } = ["gb-plugins", "superpowers"];

    /// <summary>
    /// Marketplace aliases whose discovered sub-agents are exposed to the review agent as spawnable
    /// <c>Agent</c> templates — INDEPENDENT of <see cref="Marketplaces"/> (which controls what the gateway
    /// loads for skills + discovery): a marketplace can stay loaded for its skills yet be excluded here. The
    /// default <c>gb-plugins</c> exposes EVERY plugin's agents in that marketplace (not just
    /// <c>code-reviewer</c>). An empty list ⇒ expose ALL discovered sub-agents regardless of marketplace.
    /// </summary>
    public IReadOnlyList<string> SubAgentMarketplaces { get; init; } = ["gb-plugins"];

    /// <summary>
    /// The read-only MCP tool names the review agent may call. The daemon owns all writes, so this must
    /// never include <c>Write</c>/<c>Edit</c>. Default <c>Read</c>/<c>Grep</c>/<c>Glob</c>/<c>Skill</c>.
    /// </summary>
    public IReadOnlyList<string> ReadOnlyToolAllowList { get; init; } = ["Read", "Grep", "Glob", "Skill"];

    /// <summary>
    /// GitHub <c>owner/repo</c> paths of the <c>AchieveAiReviews</c> store's sibling-repo submodules the
    /// tool-assisted review may additionally read for cross-repo context, beyond the reviewed repo and the
    /// always-allowed <c>Contracts/</c> layer (Task 16). Empty (default) means no sibling co-location.
    /// These are only added to the run's submodule allow-list when the confidentiality gate
    /// (<c>DaemonReviewStageExecutor.AllowsCrossRepoCoLocation</c>, Task 17) permits it for the run — a
    /// fork or public-repo PR never gets them, regardless of this configuration.
    /// </summary>
    public IReadOnlyList<string> CrossRepoSiblings { get; init; } = [];

    /// <summary>
    /// The reviewed repo's OWN first-party nested submodule repo names — the <c>_git/&lt;name&gt;</c> (ADO) or
    /// <c>&lt;name&gt;</c> (GitHub) URL path segments its <c>.gitmodules</c> declares. Each is added to the run's
    /// submodule allow-list under the same org/owner (+ project, for ADO) as the reviewed repo, so the
    /// tool-assisted review can initialize and read the target's own dependency graph. Empty (default) ⇒ none.
    /// <para>
    /// Unlike <see cref="CrossRepoSiblings"/> (store-level sibling repos co-located for extra cross-repo
    /// context), these are the <b>target's own</b> dependencies — needed to build and understand it — so they
    /// are added <b>unconditionally</b> and are NOT gated by
    /// <c>DaemonReviewStageExecutor.AllowsCrossRepoCoLocation</c>'s fork/public confidentiality check. The
    /// allow-list stays fail-closed: only the exact names listed here are permitted; a submodule an attacker
    /// adds — or repoints an existing path to — any other name/host is still denied.
    /// </para>
    /// <para>
    /// Names are matched against the parsed request URL path, which is NOT URL-decoded, so a URL-encoded
    /// segment must be listed exactly as it appears in the URL (e.g. <c>Microsoft%20Orleans</c>, not
    /// <c>Microsoft Orleans</c>).
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ReviewedRepoSubmodules { get; init; } = [];

    /// <summary>
    /// Remote URL of the <c>AchieveAiReviews</c> cross-repo store to check out as the review superproject:
    /// the reviewed repo is a submodule under <c>repos/&lt;RepoName&gt;</c> alongside the shared
    /// <c>Contracts/</c> layer and sibling repos. When a tool-assisted run's reviewed repo is a submodule of
    /// this store, the daemon clones the store and initializes that submodule so the agent reads across it —
    /// and, as a bonus, the gateway's Grep/Glob work on a submodule working tree (a gitlink) where they abort
    /// at a standalone clone root. Blank (default) falls back to <see cref="ReviewBotRepoUrl"/> — the store IS
    /// the ReviewBot repo — so pointing the daemon at the ReviewBot repo enables both retention and store
    /// review. When neither is set, or the reviewed repo is not a submodule of the store, the review uses the
    /// single-repo <c>/workspace/target</c> checkout.
    /// </summary>
    public string? CrossRepoStoreUrl { get; init; }

    /// <summary>Warm review-checkout slots kept ready to skip re-cloning. Default 2.</summary>
    public int ReviewPoolSize { get; init; } = 2;

    /// <summary>Host root the review-checkout pool slots live under; defaults beside the binary.</summary>
    public string? ReviewPoolHostRoot { get; init; }

    /// <summary>
    /// Whether the sandbox gateway roots every workspace at <c>WORKSPACE_BASE_PATH/&lt;app-dir&gt;/&lt;workspace&gt;</c>
    /// (SandboxedOstoolsMcpServer ADR 0028). When <c>true</c>, the daemon prepares its pooled store — and measures
    /// slot paths — under <c>&lt;app-dir&gt;</c> (derived from <c>SandboxGateway:AppId</c>) so the app-dir-less
    /// <c>workspace</c> field it sends re-roots to the prepared store instead of an empty gateway-created dir.
    /// Default <c>false</c> = pre-ADR-0028 flat behavior, matching a gateway image that predates per-app rooting;
    /// set <c>true</c> only against a gateway that does the per-app rooting.
    /// </summary>
    public bool PerAppWorkspaceRooting { get; init; }

    /// <summary>Ephemeral scratch dir name (sibling of the store clone), wiped per lease.</summary>
    public string ScratchDirName { get; init; } = "scratch";

    /// <summary>
    /// Maximum ContextReady attempts (including the re-clone escalation) before a run is parked with a
    /// greppable alert instead of retried forever. Retry state is in-memory, so a daemon restart resets it —
    /// a restart retries parked runs. Default 5.
    /// </summary>
    public int MaxContextRetries { get; init; } = 5;

    /// <summary>First retry backoff after a failed run, doubling each attempt up to the cap. Default 30s —
    /// replaces the old ~30s hot-loop that re-ran a stuck run every poll.</summary>
    public int RetryBackoffBaseSeconds { get; init; } = 30;

    /// <summary>Ceiling for the exponential retry backoff. Default 900s (15m).</summary>
    public int RetryBackoffCapSeconds { get; init; } = 900;

    /// <summary>When true, the reviewer gets scoped Write/Edit/Bash to take PR notes + do
    /// file-level diffs (code stays read-only; writes scoped to the PR notes dir + scratch).</summary>
    public bool EnableReviewerWrites { get; init; }

    /// <summary>Extra tool names granted when <see cref="EnableReviewerWrites"/> is on.</summary>
    public IReadOnlyList<string> WritableToolAllowList { get; init; } = ["Write", "Edit", "Bash"];

    /// <summary>Merge the persistent PR notes branch into the store default branch on PR close.</summary>
    public bool MergeNotesBranchOnClose { get; init; } = true;

    /// <summary>
    /// Display name the daemon presents as, both as the git commit identity's <c>user.name</c> for
    /// retention commits (see <see cref="Workspace.Git.GitRunner"/>; the commit <c>user.email</c> stays the
    /// fixed <c>review-bot@achieveai.local</c> regardless of this setting) and as a <c>[BotName]</c> prefix
    /// on the body of every posted PR comment — the comment's actual author is a shared OAuth app or a
    /// person's token, so the prefix disambiguates that the content was authored by the bot on their
    /// behalf. Default <c>Revobot</c>; an operator may personalize it, e.g. <c>GB's Revobot</c>.
    /// </summary>
    public string BotName { get; init; } = "Revobot";

    /// <summary>The resolved cross-repo store URL: <see cref="CrossRepoStoreUrl"/> when set, else
    /// <see cref="ReviewBotRepoUrl"/> (the review store and the ReviewBot retention repo are one repo).</summary>
    public string? ResolvedStoreUrl =>
        string.IsNullOrWhiteSpace(CrossRepoStoreUrl) ? ReviewBotRepoUrl : CrossRepoStoreUrl;
}
