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

    /// <summary>
    /// When <c>false</c> (default) the daemon does NOT post the host-side single-summary comment: the review
    /// agent posts its findings inline itself (line-anchored review comments + thread replies) over the egress
    /// proxy. Enable only as a degraded fallback — it posts one PR-level summary blob instead of inline
    /// comments, which is strictly inferior. Requires <see cref="EnableCommentPosting"/>.
    /// </summary>
    public bool EnableHostSummaryFallback { get; init; }

    /// <summary>When <c>false</c> (default) the knowledge-base agent does not run.</summary>
    public bool EnableKnowledgeAgent { get; init; }

    /// <summary>
    /// When <c>false</c> (default) the per-developer review-feedback agent does not run. Enabling it makes the
    /// daemon write a record NAMED AFTER each PR author into the store's <c>KnowledgeBase/developers/</c>
    /// directory, which for a public store is public, searchable and effectively permanent — an operator
    /// decision, never a default. Independent of <see cref="EnableKnowledgeAgent"/> so either half of the
    /// at-close extraction can be turned off alone, but both share the same notes-branch commit.
    /// </summary>
    public bool EnableReviewFeedbackAgent { get; init; }

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
    /// When true, a review that cannot be backed by Revobot's <c>code-reviewer</c> skill + sub-agents is
    /// ABORTED rather than degraded, and the daemon stops
    /// (<see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication"/>) — Revobot's reviews
    /// are only trustworthy WITH them, so a setup that can't provide them is a fatal misconfiguration to
    /// surface, not to review through. Where the prerequisite is checked depends on who owns the session:
    /// <list type="bullet">
    ///   <item><b>In-process</b> — the daemon's own sandbox session discovered no <c>code-reviewer</c>
    ///   sub-agents (<c>SubAgentOptions</c> would be null).</item>
    ///   <item><b>S2S</b> (<see cref="UseS2SReviewAgent"/>) — the daemon provisions no session at all, so the
    ///   gateway's marketplace catalog is read directly over <see cref="SubAgentMarketplaces"/> and must
    ///   surface both the <c>code-reviewer:pr-review</c> skill and ≥1 <c>code-reviewer:*</c> agent. A catalog
    ///   that cannot be READ is a different finding (gateway down, not skills absent): it warns and re-probes
    ///   on the next run.</item>
    /// </list>
    /// Default false (degrade-not-fail, unchanged).
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
    /// runs a lighter one (<see cref="ReviewModelId"/>, e.g. <c>gpt-5.6-luna</c>). It must be served by the
    /// same Copilot backend the daemon uses (a <c>gpt-*</c>/<c>o*</c> id routes through OpenAI Responses, a
    /// <c>claude-*</c> id through Anthropic Messages) — an unsupported slug is rejected with
    /// <c>model_not_supported</c>.
    /// <para>
    /// <b>Precedence.</b> The value is not the last word — it is one rung of a ladder the review host
    /// applies per spawn, highest first:
    /// <code>spawn-model &gt; spawn-tier &gt; SubAgentModelId &gt; template-model &gt; template-tier &gt; ReviewModelId</code>
    /// So a spawn that names its own <c>model</c> or <c>modelIntelligence</c> tier still wins (the parent
    /// agent deciding for THAT task), this value then outranks whatever <c>model:</c>/
    /// <c>modelintelligence:</c> a discovered sub-agent's markdown declares (those templates live in a
    /// workspace the operator does not author), and only with none of the above does a sub-agent fall
    /// through to the orchestrator's <see cref="ReviewModelId"/>. Empty (default) removes this rung
    /// entirely, so <see cref="ReviewModelId"/> is inherited exactly as before. Which rung actually won is
    /// reported per sub-agent as <c>modelSelectionSource</c> (<c>conversation-default</c> when this one
    /// did), so "the configured model won" is distinguishable from "nothing was configured".
    /// </para>
    /// <para>
    /// <b>Wire.</b> Read by <c>S2SReviewAgentLoopFactory</c> and sent to the review host as
    /// <c>ProvisionConversationRequest.SubAgentModelId</c> when a review provisions its conversation. It is
    /// conversation-scoped, so it takes effect only on a host running this build or newer, only from that
    /// host's next restart, and only for conversations provisioned after that — a review RESUMED onto an
    /// existing thread keeps whatever its original provision set.
    /// </para>
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
    /// (maps to the library's <c>SubAgentOptions.MaxConcurrentSubAgents</c>). Once this many are in flight a
    /// further spawn is DEFER-QUEUED (accepted immediately, then started by a background pump as a slot
    /// frees) rather than rejected, so a lower value simply serializes the focused passes instead of failing
    /// them; a higher value lets a deep review parallelize more — at the cost of more simultaneous model
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
    /// Model id the judge agent grades on. Empty (default) grades on the <b>reviewing run's own
    /// model</b>, which is self-preference bias (P6 §3.2): the generator scores its own output and
    /// the number carries no independent signal. It stays the default because swapping the judge
    /// model changes what every score this daemon has already recorded means — a behaviour change
    /// #322 owns — but the judge stage warns whenever it applies, and the persisted artifact records
    /// which model graded and which model wrote, so the axis is measurable rather than lost.
    /// <para>
    /// Set it to a model from a <i>different family</i> than <see cref="ReviewModelId"/>, and at
    /// least as capable: a cheaper verifier rubber-stamps, and resampling against an imperfect
    /// verifier cannot reduce the false-positive rate at any compute budget (§7.2).
    /// </para>
    /// </summary>
    public string JudgeModelId { get; init; } = "";

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

    /// <summary>Default for <see cref="MaxPagesPerPoll"/>, and the value both PR providers fall back to
    /// when the configured one is not a usable page count.</summary>
    public const int DefaultMaxPagesPerPoll = 10;

    /// <summary>Default for <see cref="MaxPrsPerPage"/>. Each provider additionally clamps it to its own
    /// API's ceiling (GitHub 100, Azure DevOps 1000).</summary>
    public const int DefaultMaxPrsPerPage = 200;

    /// <summary>
    /// Maximum number of provider pages a single poll fetches before stopping (PR #121 M5). Bounds the
    /// work one poll cycle does when a repo has many open PRs; the next poll resumes from the advanced
    /// cursor. Default <see cref="DefaultMaxPagesPerPoll"/> (10).
    /// <para>
    /// <b>Semantics of 0 / absent / negative.</b> Absent leaves this initializer's 10. An explicitly
    /// configured <c>0</c> or a negative value is <b>not</b> "fetch nothing" and is <b>never</b>
    /// "unbounded" — both providers normalize any value <c>&lt;= 0</c> back to
    /// <see cref="DefaultMaxPagesPerPoll"/>. Zero pages would poll a repo into looking permanently empty,
    /// and an unbounded poll is the failure this bound exists to prevent; treating a nonsensical value as
    /// "unset" is the only reading that is safe in both directions. Every poll is therefore bounded by
    /// <c>MaxPagesPerPoll × MaxPrsPerPage</c> PRs, and a poll that stops with more still available says so
    /// at <c>Warning</c>.
    /// </para>
    /// <para>
    /// This was declared with <b>zero readers</b> until issue #537 — both providers carried their own
    /// <c>private const int MaxPages = 10</c>, so the knob documented a control that did not exist. It is
    /// now the value both of them use (wired in <c>Program.cs</c> at provider registration).
    /// </para>
    /// </summary>
    public int MaxPagesPerPoll { get; init; } = DefaultMaxPagesPerPoll;

    /// <summary>
    /// How many pull requests a single provider page asks for. Default
    /// <see cref="DefaultMaxPrsPerPage"/> (200); a value <c>&lt;= 0</c> is normalized to that default, and
    /// each provider then clamps to its own API ceiling (GitHub's documented <c>per_page</c> max of 100,
    /// Azure DevOps' <c>$top</c> max of 1000).
    /// <para>
    /// This exists because leaving the page size to the server was measurably losing PRs. The ADO request
    /// set no <c>$top</c> at all, so it took ADO's documented default of 101 — and the endpoint returns no
    /// <c>x-ms-continuationtoken</c>, so the "while token" loop could never iterate and
    /// <see cref="MaxPagesPerPoll"/> on its own would still enumerate one page. Measured on a repo with 711
    /// active PRs: 101 seen per poll. Downstream filters can only ever filter what the page returned, so the
    /// unseen remainder was invisible rather than skipped.
    /// </para>
    /// <para>
    /// 200 rather than ADO's 1000 maximum: every PR older than the recency window costs one bounded
    /// <c>/pushes</c> lookup, so the page size trades round trips against how much work one page commits to.
    /// Combined with <see cref="MaxPagesPerPoll"/> the defaults cover 2,000 open PRs per repo per poll.
    /// </para>
    /// </summary>
    public int MaxPrsPerPage { get; init; } = DefaultMaxPrsPerPage;

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
    /// <para>
    /// Names are matched against the parsed request URL path, which is NOT URL-decoded, so a URL-encoded
    /// segment must be listed exactly as it appears in the URL (e.g. <c>Microsoft%20Orleans</c>, not
    /// <c>Microsoft Orleans</c>).
    /// </para>
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

    /// <summary>
    /// When <c>true</c>, the daemon drives each review through a running <b>LmStreaming.Sample</b> server
    /// over the S2S REST API instead of the in-process <c>LiveReviewAgentLoopFactory</c>. This makes the
    /// review a real LmStreaming-hosted conversation (parent loop + <c>code-reviewer:*</c> sub-agent tree)
    /// that a human can open and judge via the deep-link appended to the posted comment. Default <c>false</c>
    /// (in-process review, unchanged) — opt-in because it requires a reachable LmStreaming review host and a
    /// shared sandbox gateway. Requires <see cref="LmStreamingBaseUrl"/> and <see cref="LmStreamingProviderId"/>.
    /// </summary>
    public bool UseS2SReviewAgent { get; init; }

    /// <summary>
    /// Base URL of the running LmStreaming.Sample <b>review host</b> that S2S reviews are provisioned against
    /// and that the deep-link points at (e.g. <c>http://localhost:5051</c> — a separate instance from any
    /// production LmStreaming). The deep-link is <c>{LmStreamingBaseUrl}/?threadId={threadId}&amp;focus=1</c>.
    /// Required when <see cref="UseS2SReviewAgent"/> is on; ignored otherwise.
    /// </summary>
    public string? LmStreamingBaseUrl { get; init; }

    /// <summary>
    /// Secret sent as the <c>X-S2S-Auth</c> header on every S2S request; must equal the review host's
    /// <c>Auth:S2SInboundSecret</c> (env <c>LMSTREAMING_S2S_INBOUND_SECRET</c>). Read from configuration/env
    /// and <b>never logged or echoed</b> (AUTH_ENFORCE invariant). When unset the header is omitted — only
    /// valid against a review host that leaves the inbound S2S guard unarmed (local-use only).
    /// </summary>
    public string? LmStreamingS2SSecret { get; init; }

    /// <summary>
    /// The LmStreaming provider id to provision the review conversation with. Provision carries <b>no model
    /// field</b> — the model is whatever this provider resolves server-side — so this must name a provider on
    /// the review host that yields the intended review model (an OpenAI/Anthropic/Copilot middleware provider,
    /// since <see cref="LmStreamingModeId"/>'s workspace-agent mode rejects CLI-only/mock providers). Required
    /// when <see cref="UseS2SReviewAgent"/> is on.
    /// </summary>
    public string LmStreamingProviderId { get; init; } = "";

    /// <summary>
    /// The LmStreaming conversation mode the review is provisioned in. Defaults to <c>workspace-agent</c>,
    /// which binds a sandbox session and surfaces the <c>code-reviewer:*</c> sub-agent tree from the
    /// workspace's marketplaces — the whole point of the deep-link. A non-workspace mode would open a real
    /// conversation but an empty/generic sub-agent panel, so this should stay <c>workspace-agent</c> for the
    /// faithful-link review.
    /// </summary>
    public string LmStreamingModeId { get; init; } = "workspace-agent";

    /// <summary>
    /// The code-reviewer marketplace alias attached to the provisioned LmStreaming workspace so the gateway
    /// discovers the <c>code-reviewer:*</c> sub-agents (typically the same alias the daemon's
    /// <see cref="Marketplaces"/> list uses, e.g. <c>gb-plugins</c>). Without it the provisioned workspace's
    /// sub-agent panel is generic — a failed faithful-link review. Applies only on the S2S path.
    /// </summary>
    public string? LmStreamingReviewMarketplace { get; init; }

    /// <summary>
    /// How long, in hours, a posted review deep-link stays live: the hosted conversation is discarded from the
    /// review host once it has existed this long, after which <c>?threadId=</c> stops resolving. Default 24.
    /// <para>
    /// This is a <b>ceiling</b>, not a teardown hook — a conversation is never discarded because its review
    /// finished, its slot was returned or its PR closed, only because it aged out. Reviews are minutes long and
    /// the link exists to be opened afterwards, so anything that tied the two together would delete the feature.
    /// </para>
    /// <para>
    /// Set to <c>0</c> (or negative) to keep every conversation forever — the pre-retention behaviour, in which
    /// each review, judge and A/B arm leaves a permanent conversation on the host. Applies only on the S2S path.
    /// </para>
    /// </summary>
    public double DeepLinkRetentionHours { get; init; } = 24;

    /// <summary>
    /// The single absolute budget, in minutes, a review stage gets to wait for its recursive sub-agent tree
    /// to settle before giving up. Default 30. Read once by the caller that computes the absolute deadline
    /// passed into <c>ReviewSubAgentCompletionBarrier.WaitAsync</c> — the barrier itself never reads this
    /// option and never fabricates or resets a budget of its own; a resumed wait only ever gets whatever
    /// time remains of this original window.
    /// </summary>
    public int ReviewStageDeadlineMinutes { get; init; } = 30;

    /// <summary>
    /// How long, in seconds, two observations of the review sub-agent tree must be identical (same node
    /// ids, parent relationships, and statuses) before <c>ReviewSubAgentCompletionBarrier</c> treats the
    /// tree as settled. Default 2. Guards against synthesizing/posting against a roster that is still
    /// mid-transition (e.g. a child that finished and a grandchild about to be spawned in response).
    /// </summary>
    public int ReviewSubAgentBarrierQuietSeconds { get; init; } = 2;

    /// <summary>
    /// How long, in seconds, a sub-agent node whose status could not be resolved at all ("unknown") must
    /// have shown no activity before <c>ReviewSubAgentCompletionBarrier</c> stops waiting on it. Default 300.
    /// Set to 0 to disable the allowance and require a terminal status from every node.
    /// </summary>
    /// <remarks>
    /// An unresolved node reports no terminal transition and no running flag, so a terminal-only barrier
    /// waits on it until the whole <see cref="ReviewStageDeadlineMinutes"/> budget is gone and then discards
    /// a review that had actually finished — and the retry reproduces the same node, so it never converges.
    /// The default is deliberately much longer than any gap between a working agent's tool calls, so the
    /// only thing it can admit is a node that has genuinely stopped. Raise it if a legitimately slow child
    /// is ever admitted early; lower it only with the same evidence in hand.
    /// </remarks>
    public int ReviewSubAgentUnknownQuiescenceSeconds { get; init; } = 300;

    /// <summary>
    /// How many hours a non-terminal run must have sat untouched before the stranded-run reconciler treats it
    /// as unreachable and takes it over. Default 6. Set to 0 to disable the reconciler entirely.
    /// </summary>
    /// <remarks>
    /// A run only advances when a poll enumerates its PR, and the poll lists OPEN pull requests within its
    /// target's recency window — so a run whose PR closed, or whose PR went quiet for longer than that, is
    /// never retried again by anything. The grace period is what separates those from work still
    /// in flight: a healthy run stamps <c>updated_at</c> at every stage boundary, so any value comfortably
    /// above <see cref="ReviewStageDeadlineMinutes"/> cannot catch a live run mid-stage.
    /// </remarks>
    public double StrandedRunGraceHours { get; init; } = 6;

    /// <summary>
    /// The most stranded runs one reconciler pass will read from the store. Default 50. A cap on reading, not
    /// on working: rows beyond it are simply picked up by the following pass.
    /// </summary>
    public int StrandedRunScanLimit { get; init; } = 50;

    /// <summary>
    /// The most stranded runs one reconciler pass will actually resume through the orchestrator (the rest of
    /// the pass is bookkeeping and costs nothing). Default 2, deliberately small: a backlog that has built up
    /// over weeks must not turn into a burst of concurrent reviews — and, on a posting daemon, a burst of
    /// comments — the first time this runs. Deferred runs are logged, never dropped silently.
    /// </summary>
    public int StrandedRunMaxResumesPerSweep { get; init; } = 2;

    /// <summary>
    /// How many minutes a run left in <c>RetryPending</c> waits before the reconciler's fast path resumes it,
    /// instead of waiting the whole <see cref="StrandedRunGraceHours"/> abandonment window. Default 45. Set to
    /// 0 to switch the fast path off, leaving <c>RetryPending</c> to drain on the abandonment window as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RetryPending</c> is the one status that earns a window of its own: it is written deliberately, by the
    /// orchestrator's stage catch, and means a stage ran, failed, and the run is owed another attempt.
    /// <c>Pending</c> and <c>Running</c> mean nobody has said anything about the run at all, so its age is the
    /// only evidence there is and the abandonment window is the right question to ask of it. Before this knob
    /// existed, a deliberate retry decision on a PR outside the poll's recency window waited six hours — a
    /// number chosen to decide abandonment, reused by accident to schedule a retry.
    /// </para>
    /// <para>
    /// <b>Both bounds are real.</b> It must stay comfortably ABOVE
    /// <see cref="ReviewStageDeadlineMinutes"/> for the same reason the abandonment window must: a live run
    /// stamps <c>updated_at</c> at stage BOUNDARIES, so a run legitimately working one long stage looks
    /// untouched, and resuming it would put a second review of the same PR in flight beside the first. It must
    /// also stay BELOW <see cref="StrandedRunGraceHours"/> — above it, the "fast" path is the slower of the
    /// two and reads in configuration as something it is not; the reconciler refuses that at construction
    /// rather than running misconfigured.
    /// </para>
    /// <para>
    /// Lowering it is not free. Resumes go through the orchestrator's reconcile entry, which resets the
    /// <c>RetryGovernor</c> on purpose, so this path has no attempt budget and no exponential backoff — this
    /// window IS the backoff, and a permanently-broken run buys a full lease, clone and LLM run once per
    /// window, indefinitely. <see cref="StrandedRunMaxResumesPerSweep"/> bounds how many run at once but not
    /// how often they recur.
    /// </para>
    /// </remarks>
    public double StrandedRunRetryPendingGraceMinutes { get; init; } = 45;

    // ── eval corpus sweep (#400) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// How often the eval corpus sweep runs, in minutes. Default <c>0</c>, which switches it off:
    /// nothing about the sweep is on by default, matching every other opt-in above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep reads the reviews recorded since it last ran, measures each one's citation surface
    /// and joins it to the grade the daemon's own judge wrote, and advances a persisted cursor. It
    /// contacts no model and writes no artifact — the cost is a pass over rows the store already
    /// holds — but that pass covers the whole window, so it is not something to run on the poller's
    /// thirty-second cadence, which is what "no knob" would have meant.
    /// </para>
    /// <para>
    /// Cadence and enablement are ONE knob rather than a bool beside an interval, because the pair
    /// admits a state the single value cannot: enabled with no cadence, or a cadence set and quietly
    /// ignored because the flag beside it is false. Zero here is unambiguous — the schedule is not
    /// registered at all, and <see cref="Eval.EvalCorpusSweepSchedule"/> refuses a zero interval
    /// rather than reading it as "as often as possible".
    /// </para>
    /// <para>
    /// A window the limit cut short overrides this interval: the next maintenance tick resumes
    /// immediately instead of waiting, so raising the cadence delays the start of a backlog drain
    /// without slowing the drain itself.
    /// </para>
    /// </remarks>
    public double EvalCorpusSweepIntervalMinutes { get; init; }

    /// <summary>
    /// Most review runs one eval sweep considers. Default 1000.
    /// </summary>
    /// <remarks>
    /// This bounds the work of a single pass, not the corpus: a sweep that fills its window reports
    /// the window as truncated and the next tick resumes from the edge it reached, so lowering this
    /// makes each pass cheaper rather than making the sweep skip history. It is <b>not</b>
    /// <c>required</c>, deliberately — the configuration binder does not enforce that keyword, so a
    /// <c>required</c> knob absent from configuration would bind as <c>0</c> and be refused at
    /// construction, turning a missing line in a JSON file into a daemon that will not start.
    /// </remarks>
    public int EvalCorpusSweepWindow { get; init; } = 1000;

    /// <summary>The resolved cross-repo store URL: <see cref="CrossRepoStoreUrl"/> when set, else
    /// <see cref="ReviewBotRepoUrl"/> (the review store and the ReviewBot retention repo are one repo).</summary>
    public string? ResolvedStoreUrl =>
        string.IsNullOrWhiteSpace(CrossRepoStoreUrl) ? ReviewBotRepoUrl : CrossRepoStoreUrl;
}
