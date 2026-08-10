using System.Text;
using System.Text.RegularExpressions;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Removes daemon-infrastructure narration from the review body before it reaches the PR author — the
/// sandbox lacking <c>dotnet</c>, an ADO/GitHub HTTP 502 blocking a post, "no comments were posted per the
/// collect-only instruction" — none of which is about the author's code.
/// <para>
/// Measured (#113) over 261 completed reviews, window 2026-08-06T22:13:36Z–2026-08-10T05:41:27Z: 48/203
/// substantive reviews (23.6%) narrate our infrastructure, none of them under a severity-tagged finding
/// heading. That absence is the filter boundary, not a word list: every real finding in this corpus sits
/// under a heading naming a severity (<c>[BLOCKER]</c>, <c>### 3. MEDIUM — ...</c>, <c>Finding 3 — HIGH</c>,
/// the <c>Must</c>/<c>Should</c>/<c>Consider</c> tags from the review prompt). Infra narration sits under
/// process/meta headings instead — <c>Verification</c>, <c>Validation</c>, <c>Posting status</c>,
/// <c>Delivery status</c>, <c>Coverage and posting status</c> — or trails the document with no heading of
/// its own at all. A keyword filter (any mention of <c>429</c>, <c>provider</c>, bare <c>build</c>/
/// <c>test</c>) has no such boundary and silently eats real findings that merely share vocabulary — an
/// author's own retry-policy bug ("429 responses recurse into another retry"), a breaking-change finding
/// ("existing consumers... can fail to compile"), a controller test-coverage gap phrased around HTTP 403/422.
/// This filter never runs against a segment under a finding heading, so those are structurally unreachable
/// here regardless of what words they contain.
/// </para>
/// <para>
/// Filtering happens at BULLET or SENTENCE granularity, never a whole section and never the whole review.
/// A real "Verification"/"Validation" section routinely mixes a legitimate line (<c>git diff --check</c>
/// passed, the PR's own CI pass counts) with an infra-narration line in the very same list — deleting the
/// section would erase the legitimate line along with it.
/// </para>
/// <para>
/// Two dispositions, matching what each sub-category is worth to the author:
/// <list type="bullet">
///   <item><see cref="InfraCategory.SandboxTooling"/> — REWRITE. The internal cause (a named tool/proxy)
///   is stripped, but the segment is never deleted; whatever evidence sits in a NEIGHBORING segment (the
///   PR's own CI build/test counts) is untouched because this filter only ever rewrites the matched segment
///   itself.</item>
///   <item><see cref="InfraCategory.ProviderOrPosting"/> — MOVE. An ADO/GitHub HTTP failure or a bare
///   "no comments were posted" statement is zero-value to the author and real value to whoever runs the
///   daemon, so it is removed from the returned body and reported back via <see cref="MovedNote"/> for the
///   caller to send to an operator-side channel — never dropped on the floor and never left on the PR.
///   </item>
/// </list>
/// </para>
/// </summary>
internal static partial class InfraNarrationFilter
{
    /// <summary>Which disposition a matched segment received.</summary>
    public enum InfraCategory
    {
        /// <summary>REWRITE — the internal cause is stripped; the segment stays, generic and evidence-free.</summary>
        SandboxTooling,

        /// <summary>MOVE — an ADO/GitHub posting failure or a bare delivery-status statement; removed from
        /// the body and reported to the caller for the operator-side channel.</summary>
        ProviderOrPosting,
    }

    /// <summary>
    /// One segment moved off the PR comment. <paramref name="SubTag"/> is the finer label
    /// (<c>provider_http</c>, <c>posting_state</c>, or both — team-lead's brief treats them identically, so
    /// this filter does too, but the sub-tag survives for whoever reads the operator channel).
    /// </summary>
    public sealed record MovedNote(InfraCategory Category, string SubTag, string? Heading, string Text);

    /// <summary>
    /// Fixed replacement for a REWRITTEN sandbox/tooling segment. Deliberately generic: it names no tool, no
    /// proxy, no internal path — it states only the fact the author needs (this was not verified by running
    /// it), which is exactly what #113 found the original sentences buried under an internal cause the
    /// author has no way to act on.
    /// </summary>
    private const string RewrittenSandboxSentence =
        "Local build/test execution was not possible for this review; no results from running the code "
            + "are reflected in this assessment.";

    /// <summary>
    /// Filters <paramref name="reviewBody"/> for the PR-facing comment. Returns the filtered body and every
    /// segment that was moved (empty if none). The caller owns sending <see cref="MovedNote"/>s to the
    /// operator-side channel — this method has no logging dependency so it stays a pure, directly testable
    /// transform (mirroring <see cref="UntrustedTranscriptText"/>).
    /// </summary>
    public static (string Body, IReadOnlyList<MovedNote> Moved) Filter(string? reviewBody)
    {
        if (string.IsNullOrEmpty(reviewBody))
        {
            return (reviewBody ?? string.Empty, []);
        }

        var lines = reviewBody.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(reviewBody.Length);
        List<MovedNote> moved = [];

        string? currentHeading = null;
        List<string> segment = [];
        string? bulletPrefix = null;
        var inFence = false;

        // Set by FlushSegment to the category of the last NON-EMPTY segment it processed; a blank line does
        // not reset it (FlushSegment no-ops on an empty segment), but a heading or any other segment does.
        // This is what lets a fenced code block that directly quotes a just-rewritten sandbox/tooling
        // sentence (measured: runs 181, 228 — a disclosure sentence followed by a blank line, then a fenced
        // raw error like `502 policy_evaluation_failed` or `/bin/sh: dotnet: not found`) get swept away with
        // it, rather than leaking the very internal detail the rewrite exists to strip. A fence that is NOT
        // directly attached to a rewritten sentence this way — e.g. a code sample inside a real finding — is
        // untouched, same as before.
        InfraCategory? lastCategory = null;
        var suppressingFence = false;

        void FlushSegment()
        {
            if (segment.Count == 0)
            {
                return;
            }

            var joined = string.Join('\n', segment);
            var bare = bulletPrefix is null ? joined : joined[bulletPrefix.Length..];

            // Sentence granularity, never more: a bullet or paragraph can mix an infra-narration sentence
            // with a real one the author needs (measured: run 235 — one paragraph, "No new comment should
            // be posted from this review." followed by "The PR still requires resolution ... before
            // approval." in the very same sentence run). Classify every sentence independently first; only
            // if NONE of them match does this fall through to the byte-for-byte fast path below, which is
            // what keeps the overwhelming majority of every review's original wrapping untouched.
            var sentences = SentenceBoundary().Split(bare);
            var categories = new (InfraCategory? Category, string SubTag)[sentences.Length];
            var anyMatch = false;
            for (var i = 0; i < sentences.Length; i++)
            {
                var category = Classify(sentences[i], currentHeading, out var subTag);
                categories[i] = (category, subTag);
                anyMatch |= category is not null;
            }

            if (!anyMatch)
            {
                output.Append(joined).Append('\n');
                lastCategory = null;
                segment.Clear();
                bulletPrefix = null;
                return;
            }

            var firstLineEmitted = false;
            for (var i = 0; i < sentences.Length; i++)
            {
                var sentence = sentences[i];
                if (sentence.Length == 0)
                {
                    continue;
                }

                var (category, subTag) = categories[i];

                // The bullet marker (if any) rides on whichever sentence is actually the first one WRITTEN
                // — a MOVEd leading sentence must not leave the bullet marker orphaned on a blank line.
                var prefix = !firstLineEmitted ? bulletPrefix : null;
                switch (category)
                {
                    case InfraCategory.SandboxTooling:
                        // Rewrite in place — never delete. A neighboring sentence/bullet (a CI-evidence
                        // bullet, or a second sentence in the same paragraph) was never touched: this only
                        // rewrites the matched sentence itself.
                        output.Append(prefix).Append(RewrittenSandboxSentence).Append('\n');
                        lastCategory = InfraCategory.SandboxTooling;
                        firstLineEmitted = true;
                        break;
                    case InfraCategory.ProviderOrPosting:
                        // Moved, not written to the PR body at all — the caller logs this to the operator
                        // channel. Zero value to the author; nothing substituted, because nothing is owed
                        // here.
                        moved.Add(new MovedNote(InfraCategory.ProviderOrPosting, subTag, currentHeading, sentence));
                        lastCategory = InfraCategory.ProviderOrPosting;
                        break;
                    default:
                        output.Append(prefix).Append(sentence).Append('\n');
                        lastCategory = null;
                        firstLineEmitted = true;
                        break;
                }
            }

            segment.Clear();
            bulletPrefix = null;
        }

        foreach (var line in lines)
        {
            if (FenceMarker().IsMatch(line))
            {
                FlushSegment();
                suppressingFence = lastCategory == InfraCategory.SandboxTooling;
                if (!suppressingFence)
                {
                    // Not attached to a rewritten sentence — opaque pass-through, same as any other fenced
                    // code sample a real finding might quote.
                    output.Append(line).Append('\n');
                }

                inFence = !inFence;
                if (!inFence)
                {
                    // Fence just closed; the block it carried (suppressed or not) is fully consumed, so
                    // reset — a later fence with nothing sandbox-related between it and here must not also
                    // be swept away.
                    lastCategory = null;
                    suppressingFence = false;
                }

                continue;
            }

            if (inFence)
            {
                if (!suppressingFence)
                {
                    output.Append(line).Append('\n');
                }

                continue;
            }

            if (HeadingLine().Match(line) is { Success: true } headingMatch)
            {
                FlushSegment();
                currentHeading = headingMatch.Groups["text"].Value.Trim();
                output.Append(line).Append('\n');
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushSegment();
                output.Append(line).Append('\n');
                continue;
            }

            if (BulletLine().Match(line) is { Success: true } bulletMatch)
            {
                // A bullet is its own segment — one finding/observation per line in every review this
                // filter has been measured against (#113's fixture corpus has no multi-line bullets).
                FlushSegment();
                bulletPrefix = bulletMatch.Value;
                segment.Add(line);
                FlushSegment();
                continue;
            }

            // Plain paragraph text: accumulate until the next blank line, heading, bullet, or fence.
            segment.Add(line);
        }

        FlushSegment();

        // The scan always appends a trailing '\n' per input line (including the true last line), so the
        // result carries exactly one line ending more than the original lacked. Trim that back off rather
        // than let every filtered review grow a phantom blank line the author never asked for.
        var result = output.ToString();
        if (result.EndsWith('\n') && !reviewBody.EndsWith('\n'))
        {
            result = result[..^1];
        }

        return (result, moved);
    }

    /// <summary>
    /// Classifies one segment. Returns <c>null</c> — never touched — for anything under a finding heading
    /// (see <see cref="IsFindingHeading"/>) and for anything that fails both structural patterns below, even
    /// if it shares vocabulary with one (an author's own retry-policy or HTTP-status finding).
    /// </summary>
    private static InfraCategory? Classify(string segmentText, string? heading, out string subTag)
    {
        subTag = string.Empty;
        if (IsFindingHeading(heading))
        {
            return null;
        }

        // Checked first and takes precedence over a provider/HTTP vocabulary hit in the SAME segment (see
        // run 228 in the #113 corpus: "Jest test run... could not start because dependency resolution
        // failed through the Azure Artifacts proxy") — the sentence's subject is test EXECUTION, so it is
        // rewritten (kept, generic) rather than moved (removed outright).
        if (ExecutionBlockedPattern().IsMatch(segmentText) && EnvironmentReferencePattern().IsMatch(segmentText))
        {
            return InfraCategory.SandboxTooling;
        }

        var providerHit = ProviderReferencePattern().IsMatch(segmentText) && AccessBlockedPattern().IsMatch(segmentText);
        var postingHit = PostingStatePattern().IsMatch(segmentText);
        if (providerHit || postingHit)
        {
            subTag = (providerHit, postingHit) switch
            {
                (true, true) => "provider_http+posting_state",
                (true, false) => "provider_http",
                _ => "posting_state",
            };
            return InfraCategory.ProviderOrPosting;
        }

        return null;
    }

    /// <summary>
    /// A heading is a FINDING heading — never touched by this filter — if it names a severity. Every actual
    /// finding in this daemon's reviews is tagged this way (<c>[BLOCKER]</c>, <c>### 3. MEDIUM — ...</c>,
    /// <c>Finding 3 — HIGH</c>, or the prompt's <c>Must</c>/<c>Should</c>/<c>Consider</c> tags); process/meta
    /// headings (Verification, Validation, Posting status, Coverage and posting status, ...) never are. This
    /// is a DENY list on purpose, not an allow list on specific heading names: heading wording for process
    /// notes drifts (measured: "Verification", "Validation evidence", "CI evidence", "Areas reviewed without
    /// additional findings", even the bare "Review" heading with no more specific one in the document) far
    /// more than the severity-tagging convention does.
    /// </summary>
    private static bool IsFindingHeading(string? heading) =>
        heading is not null && FindingHeadingWord().IsMatch(heading);

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*[-*]\s+")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceMarker();

    [GeneratedRegex(@"\b(BLOCKER|CRITICAL|HIGH|MEDIUM|LOW|MUST|SHOULD|CONSIDER|FINDING)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FindingHeadingWord();

    // Broadened past "did not run"/"could not run" after the #113 fixture corpus turned up real phrasings a
    // narrower pattern missed: "I did not build or test locally" (run 283), "No local build or test commands
    // were run" (run 252, negation on "No" rather than on "run"), and a bare "is unavailable" with no "not"
    // at all (run 160: "`dotnet` is unavailable in the sandbox").
    [GeneratedRegex(
        @"\b(could not (?:be )?(?:run|start|complete|execute)|did not (?:run|build|test|execute)|were not (?:run|executed)|was not (?:run|executed|possible)|no\b.{0,60}\b(?:were|was)\s+run\b|not (?:installed|found|available)|unavailable)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExecutionBlockedPattern();

    // "npm" was deliberately dropped from this list (2026-08-10): bare "npm" has no fixture in the #113
    // corpus where it is the deciding anchor for a real sandbox/tooling REWRITE, and it produces a real
    // false positive — run 54's "Azure DevOps posting was unavailable during the run (...); ADO MCP startup
    // also encountered npm `403 Forbidden`" is provider/HTTP narration whose subject is posting, not test
    // execution, but the bare "npm" token combined with ExecutionBlockedPattern's "unavailable" made it win
    // SandboxTooling's precedence and get rewritten into a build/test disclaimer that misdescribes what
    // actually failed. "npm registry" (a two-word phrase) remains covered separately by
    // ProviderReferencePattern for exactly this kind of case.
    [GeneratedRegex(
        @"\b(sandbox|review environment|local environment|this environment|the checkout|toolchain|dependency resolution|dotnet|msbuild|jest)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentReferencePattern();

    [GeneratedRegex(
        @"\b(azure devops|\bado\b|policy_evaluation_failed|azure artifacts|npm registry)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProviderReferencePattern();

    [GeneratedRegex(
        @"\b(could not|cannot|failed|was blocked|were blocked|unavailable|returned)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AccessBlockedPattern();

    // Loose on purpose: real phrasing varies in what sits between "no" and the eventual "posted"/"made"
    // (run 20: "No provider API calls or comments were made"; run 9: "No provider comments were posted, per
    // the collect-only instruction"; run 77: "No provider mutation was made") — a tight adjacency requirement
    // missed the first of these during #113 fixture verification.
    // Loose on purpose: real phrasing varies in what sits between "no" and the eventual "posted"/"made"
    // (run 20: "No provider API calls or comments were made"; run 160: "No provider comment or
    // review-posting request was made"; run 9: "No provider comments were posted, per the collect-only
    // instruction") — a tight adjacency requirement missed the first two of these during #113 fixture
    // verification.
    [GeneratedRegex(
        @"no\b.{0,40}\b(?:comments?|mutations?)\b.{0,40}\b(?:posted|made|modified)\b|per the collect-only|collect-only (?:instruction|delivery)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PostingStatePattern();

    // Sentence boundary: split after a `.`/`!`/`?` followed by whitespace. Checked against the full #113
    // fixture corpus for the usual false-split hazards (abbreviations, decimals, "e.g."/"i.e.") — none
    // occur in this corpus, so the simple heuristic holds; a corpus that grows different phrasing may need
    // a smarter splitter later.
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBoundary();
}
