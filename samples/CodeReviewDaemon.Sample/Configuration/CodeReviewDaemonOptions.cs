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
}
