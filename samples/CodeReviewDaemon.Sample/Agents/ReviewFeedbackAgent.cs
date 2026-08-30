using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Maintains the per-developer review-feedback record: the running list of mistakes a PR author repeats,
/// distilled from findings that were raised in one review round and then FIXED in a later one, so future
/// reviews of that author's work can check their known patterns first.
/// <para>
/// The shape deliberately mirrors <see cref="KnowledgeAgent"/> — one collect-only agent run, a decline
/// sentinel, exactly one corrective turn, daemon-injected frontmatter — with one structural difference:
/// <b>no component of the output path comes from the model</b>. The record's location is derived here from
/// the provider-supplied PR author, which removes the model-supplied-path traversal class outright instead
/// of defending against it, and guarantees a public, named file can only ever be written under an identity
/// the provider actually reported.
/// </para>
/// </summary>
internal sealed class ReviewFeedbackAgent
{
    private const string KnowledgeBaseDirectory = "KnowledgeBase";

    /// <summary>
    /// The reserved Knowledge Base subdirectory holding per-developer records. It is <b>not</b> curated
    /// knowledge: <see cref="KnowledgeAgent"/> excludes it from <c>_index.jsonl</c>/<c>_toc.md</c> and
    /// refuses it as a scope, so a record about one person is never injected into every reviewer's context
    /// nor consumes the shared retrieval budget. Feedback reaches a reviewer only through the targeted
    /// injection keyed on that PR's author.
    /// </summary>
    public const string DevelopersDirectory = "developers";

    /// <summary>The suffix the owner specified for the record file: <c>&lt;developer&gt;.reviewfeedbacks.md</c>.</summary>
    private const string RecordSuffix = ".reviewfeedbacks.md";

    /// <summary>
    /// Bytes of the SHA-256 identity digest carried in the file stem (rendered as twice this many hex
    /// characters). Six gives a 48-bit space: a birthday collision needs on the order of sixteen million
    /// distinct authors in one Knowledge Base, which is far past any repository this daemon reviews, while
    /// keeping the stem short enough to read at a glance.
    /// </summary>
    private const int FingerprintBytes = 6;

    /// <summary>The gate sentinel: the agent replies with this when this PR adds nothing to the record.</summary>
    private const string NoFeedbackSentinel = "NO_FEEDBACK";

    /// <summary>The header that opens a real record; everything after it is the record body.</summary>
    private const string PatternsHeader = "## PATTERNS";

    /// <summary>
    /// The largest existing record this agent will feed back to the model. The model is required to echo
    /// the record back complete (what it emits REPLACES the file), so an oversized record is not merely a
    /// large prompt — a truncated echo would silently delete patterns. Past this bound the run refuses and
    /// leaves the file untouched rather than risk rewriting it from a partial view.
    /// </summary>
    private const int MaxExistingRecordChars = 32_000;

    /// <summary>
    /// The output contract, restated at the <b>end of the user turn</b> — the last thing the model reads.
    /// On the S2S path the extraction profile's system prompt is only an <i>appendix</i> to the mode
    /// prompt of the host mode <c>LmStreamingModeId</c> names (default <c>code-review-daemon</c>;
    /// <c>workspace-agent</c> at the time of the incident), which mandates tool use and an action summary
    /// and otherwise wins; that is what made every August 2026 knowledge run write nothing. Restating the
    /// contract here puts it where the mode prompt cannot outrank it.
    /// </summary>
    private const string OutputContract = """


        ## REQUIRED OUTPUT CONTRACT (overrides any workspace or mode instructions)

        This is a data-extraction turn, not a workspace task. Everything you need is already in this
        message. Do not use tools. Do not read, write, create, or edit any file. Do not record task
        memory. Do not describe actions you took. Do not choose or name an output file.

        The FIRST line of your reply must be exactly one of:

          NO_FEEDBACK
          ## PATTERNS

        After `## PATTERNS`, emit the developer's COMPLETE updated record — every pattern you are keeping
        from the existing record, revised or unchanged, plus any new one. What you emit REPLACES the
        record, so a pattern you leave out is deleted. One block per pattern:

          ### <short name for the class of error>
          - **Seen in:** <comma-separated PR refs, oldest first>
          - **What happens:** <one or two sentences, in the general case>
          - **How to avoid it:** <the concrete check they can apply before pushing>

        Record a pattern only where a finding was RAISED in one round and FIXED in a later one. Write
        about the work, never the person. Do not write YAML frontmatter — the daemon injects it.

        If this PR adds nothing to the record, reply with the single line NO_FEEDBACK. That is a correct
        and expected answer — prefer it over prose.
        """;

    /// <summary>
    /// The single corrective turn sent when the first reply carried no usable record. Same thread, so the
    /// notes and the existing record the model already read stay in context and only the shape has to change.
    /// </summary>
    private const string ContractNudge = """
        Your previous reply did not follow the output contract, so nothing could be written.

        Reply again with the record only — no prose, no action summary, no tool use. The FIRST line must
        be exactly `NO_FEEDBACK` or `## PATTERNS`, and a `## PATTERNS` reply must be followed by at least
        one `### <pattern>` block carrying the complete updated record.

        If this PR adds nothing to this developer's record, reply with the single line NO_FEEDBACK.
        """;

    /// <summary>Characters of a non-conforming reply carried into the log — bounded because the notes it
    /// derives from are attacker-influenceable PR content.</summary>
    private const int ReplyPreviewChars = 300;

    private readonly IMultiTurnAgent _agent;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly ILogger<ReviewFeedbackAgent> _logger;

    public ReviewFeedbackAgent(
        IMultiTurnAgent agent,
        ISandboxFileSystem fileSystem,
        ILogger<ReviewFeedbackAgent> logger
    )
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The at-close feedback pass. Drives one collect-only run over the review-feedback extraction agent,
    /// giving it the PR's accumulated <paramref name="notesInput"/> plus this developer's existing record so
    /// a repeat instance updates the pattern it belongs to instead of appending a near-duplicate. Writes
    /// <c>KnowledgeBase/developers/&lt;slug&gt;.reviewfeedbacks.md</c> with daemon-owned frontmatter
    /// (<c>developer</c>, <c>sourcePrs</c> merged with <paramref name="sourcePrRef"/>, <c>updated</c> =
    /// <paramref name="todayUtc"/>).
    /// <para>
    /// <paramref name="author"/> is the provider-reported PR author and may be <c>null</c>: that is an
    /// ordinary outcome, not an error. With no author there is no addressable record, and the daemon writes
    /// nothing rather than filing a public, named file under an invented identity. A bot author is skipped
    /// for the same reason — there is nobody to give feedback to.
    /// </para>
    /// <para>
    /// A reply that still carries no usable record after the one corrective turn is
    /// <see cref="KnowledgeExtractionOutcome.Failed"/>, <b>not</b> a decline: it is a lost extraction the
    /// caller may retry, and conflating the two is what made every knowledge failure permanent (defect D5).
    /// </para>
    /// </summary>
    public async Task<KnowledgeExtractionResult> TryExtractAsync(
        string repoRoot,
        string? author,
        string notesInput,
        string sourcePrRef,
        string todayUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(todayUtc);

        var slug = SlugifyAuthor(author);
        if (slug is null)
        {
            // Missing, bot, or unsluggable author. Declined, not Failed: there is nothing to retry, and a
            // retry could not produce an identity the provider never reported.
            _logger.LogInformation(
                "No addressable PR author for {SourcePr}; no review-feedback record written.",
                sourcePrRef
            );
            return KnowledgeExtractionResult.Declined(runId: null);
        }

        var developersDir = JoinPath(JoinPath(repoRoot, KnowledgeBaseDirectory), DevelopersDirectory);
        var relPath = RecordRelPath(slug);
        var recordPath = JoinPath(developersDir, slug + RecordSuffix);

        var read = await _fileSystem
            .ReadFileAsync(recordPath, SandboxReadLimits.KnowledgeEntryBytes, cancellationToken)
            .ConfigureAwait(false);

        // Two ceilings sit over this read and BOTH mean the same thing here. The sandbox refuses anything
        // past SandboxReadLimits.KnowledgeEntryBytes and reports TooLarge with no content;
        // MaxExistingRecordChars is the tighter prompt budget below it. Either way we hold a partial view of
        // a record the model rewrites WHOLESALE, so writing would delete every pattern we could not see.
        // A refusal must never fall through as "no record": that is exactly the presence-check hazard
        // SandboxFileRead was introduced to name, and on this path it would blank a real developer's record
        // rather than merely re-seed an empty store. Leaving the file untouched keeps a damaged record
        // readable instead of making the damage permanent (the knowledge-extraction merge+delete defect).
        if (read.TooLarge || read.Content?.Length > MaxExistingRecordChars)
        {
            _logger.LogWarning(
                "Review-feedback record '{Record}' is too large to show the model in full ({Length} chars "
                    + "read, prompt limit {Limit}, sandbox refused: {Refused}); leaving it untouched rather "
                    + "than rewriting it from a partial view.",
                relPath,
                read.Content?.Length ?? 0,
                MaxExistingRecordChars,
                read.TooLarge
            );
            return KnowledgeExtractionResult.Failed();
        }

        var existing = read.Content;

        var extractionInput = BuildExtractionInput(author!, relPath, existing, notesInput);
        var collected = await AgentTextCollector
            .CollectAsync(_agent, extractionInput, cancellationToken)
            .ConfigureAwait(false);

        // Gate: an empty or NO_FEEDBACK reply means this PR adds nothing — leave the record byte-identical.
        var text = collected.Text?.TrimStart() ?? string.Empty;
        if (IsDecline(text))
        {
            _logger.LogInformation(
                "Review-feedback run {RunId} added nothing for {Developer} (gate); record left unchanged.",
                collected.RunId,
                slug
            );
            return KnowledgeExtractionResult.Declined(collected.RunId);
        }

        var body = TryParseRecordBody(text);
        if (body is null)
        {
            // One corrective same-thread turn, for the same reason as knowledge extraction: the appendix
            // loses to the host mode prompt and the model answers like a coding agent. Exactly one — a
            // model locked into the wrong mode will not conform on retry N, and a loop burns the budget.
            _logger.LogWarning(
                "Review-feedback run {RunId} replied without a usable {Header} record; sending one "
                    + "corrective turn. Reply began: {ReplyPrefix}",
                collected.RunId,
                PatternsHeader,
                Preview(text)
            );

            collected = await AgentTextCollector
                .CollectAsync(_agent, ContractNudge, cancellationToken)
                .ConfigureAwait(false);
            text = collected.Text?.TrimStart() ?? string.Empty;

            if (IsDecline(text))
            {
                _logger.LogInformation(
                    "Review-feedback run {RunId} added nothing for {Developer} (gate, after nudge); "
                        + "record left unchanged.",
                    collected.RunId,
                    slug
                );
                return KnowledgeExtractionResult.Declined(collected.RunId);
            }

            body = TryParseRecordBody(text);
        }

        if (body is null)
        {
            _logger.LogWarning(
                "Review-feedback run {RunId} emitted no usable record after the corrective turn; "
                    + "nothing written. Reply began: {ReplyPrefix}",
                collected.RunId,
                Preview(text)
            );
            return KnowledgeExtractionResult.Failed(collected.RunId);
        }

        var sourcePrs = MergeSourcePrs(ExistingSourcePrs(relPath, existing), sourcePrRef);
        var record = BuildRecord(author!, sourcePrs, todayUtc, body);
        await _fileSystem.WriteFileAsync(recordPath, record, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Review-feedback run {RunId} updated the record for {Developer} at '{Record}'.",
            collected.RunId,
            slug,
            relPath
        );

        return KnowledgeExtractionResult.Wrote(relPath, collected.RunId);
    }

    /// <summary>
    /// Assembles the agent's input: the PR notes, then this developer's existing record <b>body</b> (the
    /// daemon-owned frontmatter is stripped so the model is never shown the fields it is told not to write),
    /// then the output contract.
    /// </summary>
    private static string BuildExtractionInput(string author, string relPath, string? existing, string notesInput)
    {
        var builder = new StringBuilder();
        _ = builder.Append(notesInput ?? string.Empty);
        _ = builder
            .Append("\n\n## Existing review-feedback record for ")
            .Append(author)
            .Append(" (")
            .Append(relPath)
            .Append(")\n");

        var existingBody = StripFrontmatter(existing);
        _ = builder.Append(existingBody.Length == 0 ? "(none — this developer has no record yet)" : existingBody);
        _ = builder.Append(OutputContract);
        return builder.ToString();
    }

    /// <summary>
    /// The record's path relative to the Knowledge Base directory
    /// (<c>developers/&lt;developer&gt;.reviewfeedbacks.md</c>) — the entry name reported in
    /// <see cref="KnowledgeExtractionResult.EntryFileName"/> and the key its frontmatter is parsed under.
    /// </summary>
    internal static string RecordRelPath(string developer) => $"{DevelopersDirectory}/{developer}{RecordSuffix}";

    /// <summary>
    /// The record's path relative to the STORE ROOT — what a reader outside the Knowledge Base joins onto the
    /// store root, e.g. the review-input injection that hands an author their own record back. Derived from
    /// the same slug and suffix the write uses, so reader and writer can never drift onto different files.
    /// </summary>
    internal static string StoreRelPath(string developer) => $"{KnowledgeBaseDirectory}/{RecordRelPath(developer)}";

    /// <summary>
    /// The Knowledge Base file stem for <paramref name="author"/>, or <c>null</c> when no record is
    /// addressable. Deliberately strict, because the result names a file committed to a public repository
    /// under a person's identity:
    /// <list type="bullet">
    /// <item>a missing/blank author yields <c>null</c> — never a placeholder, which would merge every
    /// unattributed PR into one shared file bearing a name nobody owns;</item>
    /// <item>a GitHub App identity (<c>dependabot[bot]</c>) yields <c>null</c> — there is no developer to
    /// give feedback to;</item>
    /// <item>everything else is lowercased with each run of non-alphanumerics collapsed to a hyphen, so an
    /// ADO <c>uniqueName</c> email becomes <c>jane-doe-contoso-com</c> and the readable part of the stem is
    /// <c>[a-z0-9-]</c> by construction — a crafted <c>../../.git/hooks/x</c> cannot survive it;</item>
    /// <item>a value that slugs to nothing yields <c>null</c> rather than a shared fallback stem.</item>
    /// </list>
    /// <para>
    /// <b>The readable part alone is not a key.</b> Collapsing every run of non-alphanumerics to one hyphen
    /// is many-to-one: <c>Jane.Doe</c> and <c>Jane-Doe</c>, or <c>jane.doe@contoso.com</c> and
    /// <c>jane-doe@contoso.com</c>, land on the same stem. Since the stem is the ONLY thing
    /// <see cref="RecordRelPath"/> keys on, that would put two people in one public file bearing one of
    /// their names — each one's extraction merging into or overwriting the other's, and the review-input
    /// injection then handing an author the wrong person's recurring mistakes and source-PR references.
    /// So the stem carries a fingerprint of the identity it came from, making distinct identities distinct
    /// records. The fingerprint is taken over the CASE-FOLDED identity on purpose: provider payloads vary
    /// the casing of the same login (<c>Jane.Doe</c> / <c>jane.doe</c>), and splitting one developer across
    /// two records is its own bug — the collision to prevent is between different people, not between two
    /// spellings of one.
    /// </para>
    /// </summary>
    internal static string? SlugifyAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return null;
        }

        var trimmed = author.Trim();
        if (trimmed.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new StringBuilder(trimmed.Length);
        var pendingHyphen = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    _ = builder.Append('-');
                }

                _ = builder.Append(char.ToLowerInvariant(ch));
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var slug = builder.ToString();
        return slug.Length == 0 ? null : slug + "-" + IdentityFingerprint(trimmed);
    }

    /// <summary>
    /// A short, stable, lowercase-hex fingerprint of one provider identity, appended to the readable stem so
    /// two identities that slug alike still address two records. SHA-256 over the case-folded identity,
    /// truncated to <see cref="FingerprintBytes"/> bytes — enough that a collision between two real
    /// developers in one repository is not a practical concern, short enough to leave the file name legible.
    /// It is not a secret and is not defending against a chosen-prefix attacker: the author string is
    /// provider-reported, and the traversal class is already closed by the charset filter above.
    /// </summary>
    private static string IdentityFingerprint(string identity)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()));
        return Convert.ToHexStringLower(digest.AsSpan(0, FingerprintBytes));
    }

    /// <summary>
    /// True when the reply is the gate answer: the <see cref="NoFeedbackSentinel"/>, or nothing at all.
    /// Reaching this on the corrective turn is a success — most PRs add nothing to a developer's record.
    /// </summary>
    private static bool IsDecline(string text) =>
        text.Length == 0 || text.StartsWith(NoFeedbackSentinel, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the record body that follows the <c>## PATTERNS</c> header, tolerating preamble before it
    /// (a collect-only agent that prefaces its reply still yields the record). Returns <c>null</c> when the
    /// header is absent <b>or</b> when nothing follows it — an empty <c>## PATTERNS</c> block would blank an
    /// existing record, and "the model emitted nothing" must never be honoured as "delete every pattern".
    /// </summary>
    private static string? TryParseRecordBody(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i].Trim(), PatternsHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var body = string.Join("\n", lines[(i + 1)..]).Trim();
            return body.Length == 0 ? null : body;
        }

        return null;
    }

    /// <summary>The <c>sourcePrs</c> already recorded in the existing file, or none when it has no frontmatter.</summary>
    private static IReadOnlyList<string>? ExistingSourcePrs(string relPath, string? existing) =>
        existing is null ? null : KnowledgeIndex.ParseFrontmatter(relPath, existing)?.SourcePrs;

    /// <summary>Merges <paramref name="existing"/> source-PR refs with <paramref name="sourcePrRef"/>, preserving order and de-duplicating.</summary>
    private static IReadOnlyList<string> MergeSourcePrs(IReadOnlyList<string>? existing, string sourcePrRef)
    {
        List<string> merged = existing is null ? [] : [.. existing];
        if (!merged.Contains(sourcePrRef, StringComparer.Ordinal))
        {
            merged.Add(sourcePrRef);
        }

        return merged;
    }

    /// <summary>
    /// Renders the record: a leading <c>---</c>…<c>---</c> YAML frontmatter block the daemon owns, then the
    /// model's patterns. <c>sourcePrs</c> is emitted flow-style because that is the only shape
    /// <see cref="KnowledgeIndex.ParseFrontmatter"/> reads back, so the record round-trips across runs.
    /// There is deliberately no <c>title</c>: this is not a curated knowledge entry, and a title would let
    /// it masquerade as one if it ever reached the index.
    /// </summary>
    private static string BuildRecord(string developer, IReadOnlyList<string> sourcePrs, string updated, string body)
    {
        var builder = new StringBuilder();
        _ = builder.Append("---\n");
        _ = builder.Append("developer: ").Append(developer).Append('\n');
        _ = builder.Append("sourcePrs: [").Append(string.Join(", ", sourcePrs.Select(Quote))).Append("]\n");
        _ = builder.Append("updated: ").Append(updated).Append('\n');
        _ = builder.Append("---\n\n");
        _ = builder.Append(PatternsHeader).Append("\n\n");
        _ = builder.Append(body.Trim()).Append('\n');
        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value}\"";

    /// <summary>
    /// Everything after the leading <c>---</c>…<c>---</c> frontmatter block, or the whole document when it
    /// has none. Used to show the model the record without the daemon-owned fields it must not write, and to
    /// hand a reviewer the patterns without the bookkeeping.
    /// </summary>
    internal static string StripFrontmatter(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        var open = 0;
        while (open < lines.Length && lines[open].Trim().Length == 0)
        {
            open++;
        }

        if (open >= lines.Length || lines[open].Trim() != "---")
        {
            return content.Trim();
        }

        for (var i = open + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return string.Join("\n", lines[(i + 1)..]).Trim();
            }
        }

        return content.Trim();
    }

    /// <summary>
    /// A bounded, single-line prefix of a reply, so a non-conforming extraction stops being a silent
    /// failure without letting untrusted note-derived content flood the daemon log.
    /// </summary>
    private static string Preview(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= ReplyPreviewChars
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, ReplyPreviewChars), "…");
    }

    private static string JoinPath(string root, string relative) => $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}
