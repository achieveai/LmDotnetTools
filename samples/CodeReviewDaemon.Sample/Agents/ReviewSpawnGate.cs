using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The sub-agent templates whose PURPOSE is to write to a pull request. Matching one means the spawn was an
/// attempt to post, whatever the brief said.
/// <para>
/// A DENY set, not an allow set, and the choice is deliberate. An allow set of "review specialist" templates
/// would be evaluated on every run — every run is collect-only — so any template it failed to anticipate
/// would be refused, and the daemon would quietly lose fan-out it depends on (the specialists ARE the
/// review). The failure mode of a deny set is a posting template nobody listed; the failure mode of an allow
/// set is a review with no specialists, which is the defect the executor already logs a zero-dispatch
/// warning for. Between "misses a new posting template" and "silently guts every review", the first is the
/// one that can be corrected by adding a line here.
/// </para>
/// </summary>
internal static class PostingCapableTemplates
{
    /// <summary>
    /// Matched as case-insensitive SUBSTRINGS, for the same reason the executor's context-gatherer
    /// accounting is: <c>ReviewSubAgentNode.Template</c> carries whatever the review host names the spawn,
    /// and <c>ado:ado-devops-assistant</c>, <c>ado-devops-assistant</c> and any future qualification are the
    /// same agent. An equality or prefix match would report a clean run the day the host changes how it
    /// qualifies names — a false negative, which for this gate is the only unaffordable error.
    /// </summary>
    private static readonly string[] Markers =
    [
        // The one actually observed: 11 of 161 specialist findings files on a collect-only notes branch
        // carried this template, each on a run whose manifest reads "| Mode | collect-only |".
        "ado-devops-assistant",

        // The skill whose entire job is publishing review findings to a PR, on either provider.
        "post-pr-review",

        // The ADO workflows that open, update or merge a PR as a matter of course.
        "ado-publish-pr",
        "ado-babysit-pr",
        "ado-pr-tender",
    ];

    /// <summary>
    /// True when <paramref name="template"/> names a posting-capable agent, with the marker that matched in
    /// <paramref name="marker"/>. A null/blank template matches nothing: an unnamed spawn is not evidence of
    /// posting, and this gate must not manufacture a refusal out of a missing field.
    /// </summary>
    public static bool IsPostingCapable(string? template, out string marker)
    {
        if (!string.IsNullOrWhiteSpace(template))
        {
            foreach (var candidate in Markers)
            {
                if (template.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    marker = candidate;
                    return true;
                }
            }
        }

        marker = string.Empty;
        return false;
    }
}

/// <summary>
/// The daemon-side decision on whether a sub-agent template may run for this daemon's posture. On a
/// collect-only daemon (<c>EnableCommentPosting=false</c>) a posting-capable template is refused; with
/// posting enabled nothing here refuses anything, so this is a narrowing of the collect-only case and not a
/// removal of the capability.
/// <para>
/// <b>What this can and cannot do.</b> On the S2S path the review host owns the spawn: the daemon provisions
/// a conversation and then OBSERVES the descendant tree, so this gate refuses a template at the seam the
/// daemon controls — every roster the daemon reads — and records the refusal. It does not, and on that path
/// cannot, prevent the host from having started the agent. What it does guarantee is that a posting spawn on
/// a collect-only run leaves a named, durable trace instead of being invisible, which is the state the
/// eleven observed dispatches were found in: no error, no denial, no row anywhere.
/// </para>
/// </summary>
internal sealed class ReviewSpawnGate
{
    private readonly bool _postingEnabled;
    private readonly IPolicyRefusalRecorder? _refusals;
    private readonly ILogger _logger;

    /// <param name="postingEnabled">
    /// <see cref="Configuration.CodeReviewDaemonOptions.EnableCommentPosting"/>. When true this gate allows
    /// every template — an operator who authorized posting authorized the agents that post.
    /// </param>
    /// <param name="logger">Where refusals are named. Required: a refusal nobody can read is not a control.</param>
    /// <param name="refusals">Durable refusal ledger; optional, and enforcement never depends on it.</param>
    public ReviewSpawnGate(bool postingEnabled, ILogger logger, IPolicyRefusalRecorder? refusals = null)
    {
        _postingEnabled = postingEnabled;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _refusals = refusals;
    }

    /// <summary>Whether this daemon's posture permits posting-capable templates at all.</summary>
    public bool PostingEnabled => _postingEnabled;

    /// <summary>
    /// Decides <paramref name="template"/> for <paramref name="runId"/>. Returns <c>false</c> — and logs at
    /// Error naming the template, and records a <see cref="PolicyRefusalKind.SubAgentSpawn"/> refusal — when
    /// a collect-only run reaches for a posting-capable agent. Returns <c>true</c> otherwise, including for
    /// EVERY template when posting is enabled.
    /// </summary>
    /// <param name="runId">The review run the spawn belongs to, for correlation.</param>
    /// <param name="template">The template name as the review host reported it.</param>
    /// <param name="where">Where the spawn was observed (thread/agent id), recorded as the refusal target.</param>
    public bool IsSpawnAllowed(long runId, string? template, string? where = null)
    {
        if (_postingEnabled || !PostingCapableTemplates.IsPostingCapable(template, out var marker))
        {
            return true;
        }

        var reason =
            $"this run is collect-only (EnableCommentPosting=false), so the posting-capable template "
            + $"'{template}' (matched '{marker}') has no capability to run";

        _logger.LogError(
            "Run {RunId}: REFUSED posting-capable sub-agent template \"{RefusedTemplate}\" (matched "
                + "\"{TemplateMarker}\") on a collect-only run at {SpawnSite}. The operator's standing "
                + "instruction is to collect what would have been posted, not to post it.",
            runId,
            template,
            marker,
            where ?? "(unknown)");

        _refusals?.Record(new PolicyRefusalRecord(
            DateTimeOffset.UtcNow,
            PolicyRefusalKind.SubAgentSpawn,
            "daemon",
            template ?? string.Empty,
            "spawn",
            where ?? $"run {runId}",
            reason));

        return false;
    }
}
