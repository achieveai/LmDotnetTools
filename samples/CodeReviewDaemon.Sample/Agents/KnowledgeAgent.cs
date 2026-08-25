using System.Globalization;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Distills review knowledge into the durable Knowledge Base. The at-close
/// <see cref="TryExtractAsync"/> (design §1/§2) drives one collect-only agent run over a
/// merged PR's accumulated notes, gates on durable, generalizable knowledge, writes a <b>layered</b>
/// <c>KnowledgeBase/&lt;scope&gt;/&lt;slug&gt;.md</c> entry with daemon-injected frontmatter (create-or-update),
/// then regenerates both the queryable <c>_index.jsonl</c> and <c>_toc.md</c> from the entries actually
/// present. Committing/pushing the checkout is the repo manager's job, so this agent stays verifiable
/// against an in-memory fake.
/// </summary>
internal sealed class KnowledgeAgent
{
    private const string KnowledgeBaseDirectory = "KnowledgeBase";
    private const string TocFileName = "_toc.md";
    private const string IndexFileName = "_index.jsonl";

    /// <summary>The gate sentinel: the extraction agent replies with this when nothing durable is worth writing.</summary>
    private const string NoKnowledgeSentinel = "NO_KNOWLEDGE";

    /// <summary>
    /// The output contract, restated at the <b>end of the user turn</b> — the last thing the model reads.
    /// <para>
    /// On the S2S path this extraction runs as a hosted <c>workspace-agent</c> conversation, and the
    /// extraction profile's system prompt is only an <i>appendix</i> to the host's mode prompt. That mode
    /// prompt says to use sandbox tools for all operations, keep task memory, and summarize what changed —
    /// and it won. Every August 2026 run on both daemons answered with an action summary or a review
    /// verdict, so every run parsed to no markers and wrote nothing. Restating the contract here puts it
    /// where the mode prompt cannot outrank it.
    /// </para>
    /// </summary>
    private const string OutputContract = """


        ## REQUIRED OUTPUT CONTRACT (overrides any workspace or mode instructions)

        This is a data-extraction turn, not a workspace task. Everything you need is already in this
        message. Do not use tools. Do not read, write, create, or edit any file. Do not record task
        memory. Do not describe actions you took.

        Reply with the entry itself and nothing else. The FIRST line of your reply must be exactly one of:

          NO_KNOWLEDGE
          ## SCOPE: <one-word area, e.g. system, testing, provider>

        When you emit `## SCOPE:`, follow it with:

          ## TITLE: <short entry title>
          ## TAGS: <comma-separated tags>
          ## UPDATES: <existing KnowledgeBase <scope>/<slug>.md path — only when revising that entry>

        then a blank line, then the entry body in Markdown.

        If this PR contributed nothing durable and generalizable, reply with the single line
        NO_KNOWLEDGE. That is a correct and expected answer — prefer it over prose.
        """;

    /// <summary>
    /// The single corrective turn sent when the first reply carried no usable markers. Same thread, so the
    /// notes the model already read stay in context and only the output shape has to change.
    /// </summary>
    private const string ContractNudge = """
        Your previous reply did not follow the output contract, so nothing could be written.

        Reply again with the entry only — no prose, no action summary, no tool use. The FIRST line must be
        exactly `NO_KNOWLEDGE` or `## SCOPE: <area>`, followed by `## TITLE:` and `## TAGS:` lines, a blank
        line, and the entry body.

        If these notes hold nothing durable and generalizable, reply with the single line NO_KNOWLEDGE.
        """;

    /// <summary>Characters of a non-conforming reply carried into the log — bounded because the notes it
    /// derives from are attacker-influenceable PR content.</summary>
    private const int ReplyPreviewChars = 300;

    private readonly IMultiTurnAgent _agent;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly ILogger<KnowledgeAgent> _logger;

    public KnowledgeAgent(IMultiTurnAgent agent, ISandboxFileSystem fileSystem, ILogger<KnowledgeAgent> logger)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The at-close extraction pass (design §1/§2). Drives one collect-only run over the extraction agent,
    /// giving it the PR's accumulated <paramref name="notesInput"/> plus the existing index/ToC so it can
    /// update a related entry rather than duplicate it. The agent applies the <b>durable-knowledge gate</b>:
    /// when it replies with the <see cref="NoKnowledgeSentinel"/> (the common case — "not every PR
    /// contributes"), this returns <see cref="KnowledgeExtractionOutcome.Declined"/> and writes nothing.
    /// Otherwise it parses the agent's header
    /// markers (<c>## SCOPE/TITLE/TAGS/UPDATES</c>), resolves create-vs-update against
    /// <paramref name="repoRoot"/>, injects daemon-owned YAML frontmatter deterministically
    /// (<c>updated</c> = <paramref name="todayUtc"/>, <c>sourcePrs</c> merges the existing set with
    /// <paramref name="sourcePrRef"/>), writes the layered entry, then regenerates <c>_index.jsonl</c> and
    /// <c>_toc.md</c> from the entries actually present.
    /// <para>
    /// A reply that still carries no usable markers after the one corrective turn is
    /// <see cref="KnowledgeExtractionOutcome.Failed"/>, <b>not</b> a decline: it is a lost extraction the
    /// caller should retry, and conflating the two is what made every failure permanent (defect D5).
    /// </para>
    /// </summary>
    public async Task<KnowledgeExtractionResult> TryExtractAsync(
        string repoRoot,
        string notesInput,
        string sourcePrRef,
        string todayUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(todayUtc);

        var knowledgeBaseDir = JoinPath(repoRoot, KnowledgeBaseDirectory);

        var extractionInput = await BuildExtractionInputAsync(knowledgeBaseDir, notesInput, cancellationToken)
            .ConfigureAwait(false);
        var collected = await AgentTextCollector
            .CollectAsync(_agent, extractionInput, cancellationToken)
            .ConfigureAwait(false);

        // Gate: an empty or NO_KNOWLEDGE reply means nothing durable — write nothing, leave the KB as-is.
        var text = collected.Text?.TrimStart() ?? string.Empty;
        if (IsDecline(text))
        {
            _logger.LogInformation(
                "Knowledge run {RunId} produced no durable knowledge (gate); Knowledge Base left unchanged.",
                collected.RunId
            );
            return KnowledgeExtractionResult.Declined(collected.RunId);
        }

        var parsed = ParseEntry(text);

        // Resolve the target entry: an ## UPDATES marker pointing at an existing file wins; otherwise the
        // scope+slug path (which is a create, or an in-place update when that file already exists).
        var targetRelPath = await ResolveTargetRelPathAsync(knowledgeBaseDir, parsed, cancellationToken)
            .ConfigureAwait(false);

        if (targetRelPath is null)
        {
            // The extraction profile's system prompt rides as an APPENDIX to the host's much larger
            // workspace-agent mode prompt on the S2S path, and it loses: the model answers like a coding
            // agent (action summary, task memory, review verdict) instead of emitting the contract. One
            // corrective same-thread turn recovers it — same thread, so the notes it already read stay in
            // context. Exactly one: a model locked into the wrong mode will not conform on retry N, and a
            // loop would burn the review budget to prove it.
            _logger.LogWarning(
                "Knowledge run {RunId} replied without SCOPE/TITLE markers; sending one corrective turn. "
                    + "Reply began: {ReplyPrefix}",
                collected.RunId,
                Preview(text)
            );

            collected = await AgentTextCollector
                .CollectAsync(_agent, ContractNudge, cancellationToken)
                .ConfigureAwait(false);
            text = collected.Text?.TrimStart() ?? string.Empty;

            if (IsDecline(text))
            {
                _logger.LogInformation(
                    "Knowledge run {RunId} produced no durable knowledge (gate, after nudge); "
                        + "Knowledge Base left unchanged.",
                    collected.RunId
                );
                return KnowledgeExtractionResult.Declined(collected.RunId);
            }

            parsed = ParseEntry(text);
            targetRelPath = await ResolveTargetRelPathAsync(knowledgeBaseDir, parsed, cancellationToken)
                .ConfigureAwait(false);
        }

        if (targetRelPath is null)
        {
            _logger.LogWarning(
                "Knowledge run {RunId} emitted no usable SCOPE/TITLE markers after the corrective turn; "
                    + "nothing written. Reply began: {ReplyPrefix}",
                collected.RunId,
                Preview(text)
            );
            return KnowledgeExtractionResult.Failed(collected.RunId);
        }

        var targetPath = JoinPath(knowledgeBaseDir, targetRelPath);
        var existingRead = await _fileSystem
            .ReadFileAsync(targetPath, SandboxReadLimits.KnowledgeEntryBytes, cancellationToken)
            .ConfigureAwait(false);
        if (existingRead.TooLarge)
        {
            // The write below is an OVERWRITE, and this read is the only thing that carries the current
            // entry's title, tags and source PRs into it. An unreadable entry read as an absent one would
            // therefore not skip a merge — it would replace a file we could not see with one distilled
            // without it, and the entry it destroyed is the durable half of this feature. Refuse the run.
            _logger.LogError(
                "Knowledge run {RunId} targets '{Entry}', which exceeds the {Limit}-byte read limit; refusing "
                    + "to overwrite an entry that could not be read. Nothing written.",
                collected.RunId,
                targetRelPath,
                SandboxReadLimits.KnowledgeEntryBytes
            );
            return KnowledgeExtractionResult.Failed(collected.RunId);
        }

        var existing = existingRead.Content;
        var existingMeta = existing is null ? null : KnowledgeIndex.ParseFrontmatter(targetRelPath, existing);

        var scope = ScopeSegment(targetRelPath) ?? parsed.Scope;
        var title = !string.IsNullOrWhiteSpace(parsed.Title)
            ? parsed.Title
            : existingMeta?.Title ?? SlugFromRelPath(targetRelPath);
        var tags = parsed.Tags.Count > 0 ? parsed.Tags : existingMeta?.Tags ?? [];
        var sourcePrs = MergeSourcePrs(existingMeta?.SourcePrs, sourcePrRef);

        var entryMarkdown = BuildEntry(title, tags, scope, sourcePrs, todayUtc, parsed.Body);
        await _fileSystem.WriteFileAsync(targetPath, entryMarkdown, cancellationToken).ConfigureAwait(false);

        await RegenerateIndexAndTocAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Knowledge run {RunId} wrote entry '{Entry}' and regenerated the index + table of contents.",
            collected.RunId,
            targetRelPath
        );

        return KnowledgeExtractionResult.Wrote(targetRelPath, collected.RunId);
    }

    /// <summary>
    /// Assembles the extraction agent's input: the PR notes followed by the existing <c>_index.jsonl</c>
    /// and <c>_toc.md</c> (best-effort; missing files render as <c>(empty)</c>) so the agent can update a
    /// related entry instead of duplicating one. Each listing is bounded by
    /// <see cref="AppendExistingListing"/>, which announces a shortened or refused listing to the agent.
    /// </summary>
    private async Task<string> BuildExtractionInputAsync(
        string knowledgeBaseDir,
        string notesInput,
        CancellationToken cancellationToken
    )
    {
        var index = await _fileSystem
            .ReadFileAsync(
                JoinPath(knowledgeBaseDir, IndexFileName),
                SandboxReadLimits.KnowledgeListingBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        var toc = await _fileSystem
            .ReadFileAsync(
                JoinPath(knowledgeBaseDir, TocFileName),
                SandboxReadLimits.KnowledgeListingBytes,
                cancellationToken
            )
            .ConfigureAwait(false);

        var builder = new StringBuilder();
        _ = builder.Append(notesInput ?? string.Empty);
        AppendExistingListing(builder, "index (" + IndexFileName + ")", IndexFileName, index);
        AppendExistingListing(builder, "table of contents (" + TocFileName + ")", TocFileName, toc);
        _ = builder.Append(OutputContract);
        return builder.ToString();
    }

    /// <summary>
    /// Characters of one existing-store listing carried into the extraction prompt. The store grows with
    /// every merged PR, so an unbounded listing eventually crowds out the notes it is meant to help
    /// distill — and the notes are the payload.
    /// <para>
    /// <see cref="KnowledgeIndex.MaxIndexRecords"/> deliberately does <b>not</b> transfer here: that ceiling
    /// bounds the cost of <i>parsing</i> the index, and at a few hundred characters a record it still admits
    /// a listing far larger than any prompt should carry. Nothing is parsed on this path, so the budget that
    /// matters is the one the model is charged for.
    /// </para>
    /// </summary>
    private const int MaxExistingListingChars = 16 * 1024;

    /// <summary>
    /// Appends one existing-store listing under its heading, shortened at a line boundary to
    /// <see cref="MaxExistingListingChars"/>.
    /// <para>
    /// A shortened listing is announced <b>in the prompt</b>, not merely in our logs, and that is the point
    /// of the cap rather than a nicety. The agent reads these listings specifically to update a related
    /// entry instead of writing a second one, so a listing that quietly loses its tail does not just shrink
    /// a prompt — it manufactures duplicate Knowledge Base entries, because the agent goes on believing it
    /// has seen the whole store. A silent cap is therefore worse than no cap at all. Told that it is looking
    /// at part of the store, the agent can stop reading "not in this list" as "does not exist"; the log line
    /// below can tell us, but it cannot tell the only party in a position to avoid the duplicate.
    /// </para>
    /// <para>
    /// A listing REFUSED for size gets the same treatment for the same reason, and a stronger warning: it
    /// renders none of the store, so <c>(empty)</c> — the missing-file rendering — would tell the agent the
    /// Knowledge Base is empty at the exact moment it is largest, and every entry it then wrote would
    /// duplicate one that already exists.
    /// </para>
    /// </summary>
    private void AppendExistingListing(
        StringBuilder builder, string heading, string fileName, SandboxFileRead read)
    {
        _ = builder.Append("\n\n## Existing Knowledge Base ").Append(heading).Append('\n');

        if (read.TooLarge)
        {
            _ = builder.Append(
                "**This listing could NOT be read — it exceeds this daemon's size limit, so NONE of the "
                    + "existing Knowledge Base is shown to you here.** The store is not empty; it is unread. "
                    + "Assume an entry on your topic may already exist, prefer an `## UPDATES` to a file you "
                    + "can name from the notes above, and do not treat this section's silence as evidence "
                    + "that a topic is uncovered."
            );

            _logger.LogWarning(
                "Knowledge extraction: the existing {FileName} exceeds the {Limit}-byte read limit and was not "
                    + "read; the extraction prompt says so rather than rendering it as an empty store. Trim the "
                    + "listing at the source — until then every extraction runs blind to the whole Knowledge "
                    + "Base and will duplicate entries.",
                fileName,
                SandboxReadLimits.KnowledgeListingBytes
            );
            return;
        }

        var content = read.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            _ = builder.Append("(empty)");
            return;
        }

        if (content.Length <= MaxExistingListingChars)
        {
            _ = builder.Append(content);
            return;
        }

        // Cut at a line boundary: half an index record reads like a whole one, and an agent that believes
        // 'system/foo' exists because it saw a torn 'system/foo-bar' has been misled rather than shortened.
        // With no boundary inside the budget there is no whole line to show, so show none and say so.
        var cut = content.LastIndexOf('\n', MaxExistingListingChars - 1);
        var kept = cut > 0 ? content[..cut] : string.Empty;
        var dropped = CountLines(content) - CountLines(kept);

        _ = builder.Append(kept);
        _ = builder.Append(
            "\n\n**This listing is PARTIAL — it did not fit, and "
        );
        _ = builder.Append(dropped.ToString(CultureInfo.InvariantCulture));
        _ = builder.Append(
            " further lines are not shown.** Entries you cannot see here may already exist, so absence "
                + "from the part above is NOT evidence that a topic is uncovered: do not treat \"not in "
                + "this list\" as \"does not exist\"."
        );

        _logger.LogWarning(
            "Knowledge extraction: the existing {FileName} is {TotalChars} characters, so the extraction "
                + "prompt carries the first {KeptChars} and tells the agent that {DroppedLines} lines are "
                + "missing. The agent will not read absence as proof of absence, but a store listing this "
                + "long makes duplicate entries likelier and the listing should be trimmed at the source.",
            fileName,
            content.Length,
            kept.Length,
            dropped
        );
    }

    /// <summary>Lines in <paramref name="text"/>, where a trailing newline ends the last line rather than
    /// starting an empty one.</summary>
    private static int CountLines(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return text[^1] == '\n' ? lines - 1 : lines;
    }

    /// <summary>
    /// True when the reply is the gate answer: the <see cref="NoKnowledgeSentinel"/>, or nothing at all.
    /// Reaching this on the corrective turn is a success, not a failure — "not every PR contributes".
    /// </summary>
    private static bool IsDecline(string text) =>
        text.Length == 0 || text.StartsWith(NoKnowledgeSentinel, StringComparison.Ordinal);

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

    /// <summary>
    /// Resolves the KB-relative path the entry should be written to, treating all path components of the
    /// model's reply as untrusted (the notes are derived from attacker-controllable PR content, so a crafted
    /// <c>## SCOPE: ../../.git/hooks</c> or <c>## UPDATES: ../../x</c> must never escape the Knowledge Base).
    /// An <c>## UPDATES</c> marker is honored only when it validates as a KB-relative <c>&lt;scope&gt;/&lt;slug&gt;.md</c>
    /// of ref-safe segments AND names a file that exists; otherwise the path is <c>&lt;scope&gt;/&lt;slug&gt;.md</c>
    /// from a SCOPE that must itself be one ref-safe segment (the slug is already sanitized by
    /// <see cref="Slugify"/>). Every candidate is finally canonicalized and required to stay under
    /// <paramref name="knowledgeBaseDir"/>. Returns <c>null</c> (write nothing) when the markers are too
    /// sparse or fail validation.
    /// </summary>
    private async Task<string?> ResolveTargetRelPathAsync(
        string knowledgeBaseDir,
        ParsedEntry parsed,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(parsed.Updates))
        {
            var updatesRel = NormalizeUpdatesRelPath(parsed.Updates);
            if (updatesRel is null)
            {
                _logger.LogWarning(
                    "Knowledge extraction rejected unsafe ## UPDATES '{Updates}'; treating as a create.",
                    parsed.Updates);
            }
            else if (IsBookkeeping(LeafName(updatesRel)))
            {
                // _toc.md/_index.jsonl are regenerated wholesale by RegenerateIndexAndTocAsync below, so
                // writing the entry there would be silently clobbered by that regen. Refuse and fall
                // through to the scope+slug create instead of quietly losing the entry.
                _logger.LogWarning(
                    "Knowledge extraction rejected ## UPDATES '{Updates}' targeting a bookkeeping file; treating as a create.",
                    parsed.Updates);
            }
            else if (IsDevelopersDirectory(ScopeSegment(updatesRel) ?? string.Empty))
            {
                // The per-developer records are not curated knowledge and are not this agent's to rewrite:
                // ReviewFeedbackAgent replaces one wholesale from a prompt that shows it the whole file, so
                // an entry landing there would be destroyed on the next feedback run — and, worse, would
                // put PR-derived text into a public file bearing a named person's identity.
                _logger.LogWarning(
                    "Knowledge extraction rejected ## UPDATES '{Updates}' targeting the reserved per-developer "
                        + "directory; treating as a create.",
                    parsed.Updates);
            }
            else if (StaysUnderKnowledgeBase(knowledgeBaseDir, updatesRel))
            {
                // A PRESENCE check: an over-size entry is present, and this decides only whether the named
                // file is the one to write. Reading "absent" off it would route the entry to a fresh
                // scope+slug path and leave the real entry unmerged beside its own duplicate. (The caller
                // reads the target for merge and refuses the run if THAT read is refused.)
                var updatesRead = await _fileSystem
                    .ReadFileAsync(
                        JoinPath(knowledgeBaseDir, updatesRel),
                        SandboxReadLimits.KnowledgeEntryBytes,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (updatesRead.Exists)
                {
                    return updatesRel;
                }
            }
        }

        var scope = parsed.Scope.Trim();
        if (scope.Length == 0 || string.IsNullOrWhiteSpace(parsed.Title))
        {
            return null;
        }

        if (!IsSafeSegment(scope))
        {
            _logger.LogWarning("Knowledge extraction rejected unsafe SCOPE '{Scope}'; nothing written.", scope);
            return null;
        }

        if (IsDevelopersDirectory(scope))
        {
            // The per-developer namespace is reserved (see IsDevelopersDirectory). A model-chosen scope must
            // never be able to place a knowledge entry among records that name real people, nor to collide
            // with a record ReviewFeedbackAgent rewrites wholesale.
            _logger.LogWarning(
                "Knowledge extraction rejected reserved SCOPE '{Scope}'; nothing written.", scope);
            return null;
        }

        // Reconcile the scope directory's CASE against the Knowledge Base: the extraction agent is an LLM that
        // cases a repo scope inconsistently across runs (e.g. 'MCQdbDEV' one run, 'mcqdbdev' the next). Written
        // verbatim those become two directories a case-sensitive git tracks but a case-insensitive checkout
        // (Windows) collapses — losing entries and breaking retrieval (observed live on the mcqdb store). Reuse
        // the first-seen case so every entry for a scope stays in ONE directory.
        scope = await ReconcileScopeCaseAsync(knowledgeBaseDir, scope, cancellationToken).ConfigureAwait(false);

        var relPath = $"{scope}/{Slugify(parsed.Title)}.md";
        if (!StaysUnderKnowledgeBase(knowledgeBaseDir, relPath))
        {
            _logger.LogWarning(
                "Knowledge extraction rejected SCOPE '{Scope}' that escapes the Knowledge Base; nothing written.",
                scope);
            return null;
        }

        if (IsBookkeeping(LeafName(relPath)))
        {
            // Defense in depth: Slugify never actually produces a bookkeeping leaf name today, but a
            // resolved target must never be allowed to alias _toc.md/_index.jsonl regardless.
            _logger.LogWarning(
                "Knowledge extraction rejected SCOPE/TITLE resolving to bookkeeping file '{RelPath}'; nothing written.",
                relPath);
            return null;
        }

        return relPath;
    }

    /// <summary>
    /// A path segment is ref-safe only when it is a non-empty run of <c>[A-Za-z0-9._-]</c> that is neither
    /// <c>.</c> nor <c>..</c>. This rejects directory traversal (<c>../</c>, <c>..\</c>), separator injection,
    /// and absolute anchors, so a model-emitted SCOPE/UPDATES value can never reach outside the KB directory.
    /// </summary>
    private static bool IsSafeSegment(string segment) =>
        segment.Length > 0
        && segment is not ("." or "..")
        && segment.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    /// <summary>
    /// Validates a model-supplied <c>## UPDATES</c> value as a KB-relative path of one or two ref-safe
    /// segments whose final segment ends in <c>.md</c> (a layered <c>&lt;scope&gt;/&lt;slug&gt;.md</c> or a legacy flat
    /// <c>&lt;slug&gt;.md</c>), returning the normalized relpath or <c>null</c> when it fails — so a crafted
    /// <c>../../.git/x</c> is refused before it can redirect the write.
    /// </summary>
    private static string? NormalizeUpdatesRelPath(string updates)
    {
        var trimmed = updates.Trim().Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var segments = trimmed.Split('/');
        if (segments.Length > 2
            || !segments.All(IsSafeSegment)
            || !segments[^1].EndsWith(".md", StringComparison.Ordinal))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Backstop for the per-segment checks: canonicalizes <paramref name="relPath"/> under
    /// <paramref name="knowledgeBaseDir"/> and confirms the result stays inside that directory, so any
    /// traversal a parser check missed can never reach <see cref="ISandboxFileSystem.WriteFileAsync"/>.
    /// </summary>
    private static bool StaysUnderKnowledgeBase(string knowledgeBaseDir, string relPath)
    {
        var baseFull = Path.GetFullPath(knowledgeBaseDir);
        var candidate = Path.GetFullPath(Path.Combine(baseFull, relPath));
        var baseWithSeparator = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        return candidate.StartsWith(baseWithSeparator, StringComparison.Ordinal);
    }

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
    /// Renders the entry: a leading <c>---</c>…<c>---</c> YAML frontmatter block the daemon owns, then the
    /// model's body. The lists are emitted <b>flow-style</b> (<c>tags: [a, b]</c>, <c>sourcePrs: ["x"]</c>)
    /// because that is the only shape <see cref="KnowledgeIndex.ParseFrontmatter"/> reads back — so the
    /// entry round-trips through the index parser.
    /// </summary>
    private static string BuildEntry(
        string title,
        IReadOnlyList<string> tags,
        string scope,
        IReadOnlyList<string> sourcePrs,
        string updated,
        string body
    )
    {
        var builder = new StringBuilder();
        _ = builder.Append("---\n");
        _ = builder.Append("title: ").Append(title).Append('\n');
        _ = builder.Append("tags: [").Append(string.Join(", ", tags)).Append("]\n");
        _ = builder.Append("scope: ").Append(scope).Append('\n');
        _ = builder.Append("sourcePrs: [").Append(string.Join(", ", sourcePrs.Select(Quote))).Append("]\n");
        _ = builder.Append("updated: ").Append(updated).Append('\n');
        _ = builder.Append("---\n");

        var trimmedBody = body.Trim();
        if (trimmedBody.Length > 0)
        {
            _ = builder.Append('\n').Append(trimmedBody).Append('\n');
        }

        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value}\"";

    /// <summary>
    /// Parses the agent's header markers (<c>## SCOPE/TITLE/TAGS/UPDATES</c>) and the entry body. Lines
    /// before the first recognized marker are tolerated as preamble ("Here is the entry:\n## SCOPE: …") so
    /// a collect-only agent that prefaces its reply still yields the markers instead of silently dropping
    /// them; but once the header has started, only a CONTIGUOUS run of marker lines is consumed — the
    /// first non-marker line ends the header, and everything from that line on (however heading-shaped it
    /// looks) is body text. This keeps a body line like <c>## TAGS: a, b</c> (the entry giving an example
    /// of the marker syntax) from being re-parsed as a real marker, which would silently overwrite the
    /// field and truncate the body at that point.
    /// </summary>
    private static ParsedEntry ParseEntry(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        string scope = string.Empty, title = string.Empty;
        string? updates = null;
        List<string> tags = [];

        var i = 0;
        while (i < lines.Length && TryParseMarker(lines[i].Trim()) is null)
        {
            i++; // Skip preamble before the header starts.
        }

        while (i < lines.Length)
        {
            var marker = TryParseMarker(lines[i].Trim());
            if (marker is null)
            {
                break; // Header ended; the rest — including any heading-shaped lines — is body.
            }

            var (key, value) = marker.Value;
            switch (key)
            {
                case "SCOPE":
                    scope = value;
                    break;
                case "TITLE":
                    title = value;
                    break;
                case "TAGS":
                    tags = SplitTags(value);
                    break;
                case "UPDATES":
                    updates = value;
                    break;
                default:
                    break;
            }

            i++;
        }

        var body = i < lines.Length ? string.Join("\n", lines[i..]).Trim() : string.Empty;
        return new ParsedEntry(scope, title, tags, updates, body);
    }

    /// <summary>Recognizes a <c>## KEY: value</c> header marker for the known keys, or returns <c>null</c>.</summary>
    private static (string Key, string Value)? TryParseMarker(string line)
    {
        if (!line.StartsWith("## ", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = line[3..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return null;
        }

        var key = rest[..colon].Trim().ToUpperInvariant();
        if (key is not ("SCOPE" or "TITLE" or "TAGS" or "UPDATES"))
        {
            return null;
        }

        return (key, rest[(colon + 1)..].Trim());
    }

    private static List<string> SplitTags(string value)
    {
        List<string> tags = [];
        foreach (var part in value.Split(','))
        {
            var tag = part.Trim();
            if (tag.Length > 0)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>
    /// Regenerates <c>_index.jsonl</c> and <c>_toc.md</c> from the layered entries actually present, so
    /// neither ever drifts from the directory. Walks each scope directory under
    /// <paramref name="knowledgeBaseDir"/>, parses each entry's frontmatter, and skips (with a log) any
    /// entry that has none — malformed frontmatter never aborts the regen (design §6).
    /// </summary>
    private async Task RegenerateIndexAndTocAsync(string knowledgeBaseDir, CancellationToken cancellationToken)
    {
        var metas = await CollectEntryMetasAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        var index = KnowledgeIndex.RenderIndex(metas);
        await _fileSystem
            .WriteFileAsync(JoinPath(knowledgeBaseDir, IndexFileName), index, cancellationToken)
            .ConfigureAwait(false);

        // _toc.md link labels: the blank-title fallback to file path lives in
        // KnowledgeTableOfContents.RenderItems now (issue #259), so every caller gets it for free —
        // pass the raw Title through here.
        var tocEntries = metas.Select(meta => new KnowledgeEntry(meta.File, meta.Title)).ToList();
        var toc = KnowledgeTableOfContents.Render(tocEntries);
        await _fileSystem
            .WriteFileAsync(JoinPath(knowledgeBaseDir, TocFileName), toc, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<KnowledgeEntryMeta>> CollectEntryMetasAsync(
        string knowledgeBaseDir,
        CancellationToken cancellationToken
    )
    {
        var metas = new List<KnowledgeEntryMeta>();
        var children = await _fileSystem.ListFilesAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        foreach (var child in children)
        {
            if (IsBookkeeping(child) || IsDevelopersDirectory(child))
            {
                continue;
            }

            if (child.EndsWith(".md", StringComparison.Ordinal))
            {
                // A legacy flat entry (no scope directory): included only if it carries frontmatter.
                await TryAddMetaAsync(metas, knowledgeBaseDir, child, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Otherwise a scope directory (system/, <repo>/): walk its Markdown entries.
            var scopeDir = JoinPath(knowledgeBaseDir, child);
            var names = await _fileSystem.ListFilesAsync(scopeDir, cancellationToken).ConfigureAwait(false);
            foreach (var name in names)
            {
                if (IsBookkeeping(name) || !name.EndsWith(".md", StringComparison.Ordinal))
                {
                    continue;
                }

                await TryAddMetaAsync(metas, knowledgeBaseDir, $"{child}/{name}", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return metas;
    }

    private async Task TryAddMetaAsync(
        List<KnowledgeEntryMeta> metas,
        string knowledgeBaseDir,
        string relFile,
        CancellationToken cancellationToken
    )
    {
        var read = await _fileSystem
            .ReadFileAsync(
                JoinPath(knowledgeBaseDir, relFile),
                SandboxReadLimits.KnowledgeEntryBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read.TooLarge)
        {
            // LISTED, not skipped. This regen REPLACES both listings, and the reviewer reads _toc.md as the
            // set of entries that exist — so dropping an unreadable entry here does not merely fail to index
            // it, it deletes the only route anything has to a file still sitting in the store. Listed under a
            // path-derived title with no metadata: honest about what is unknown, and the link still resolves.
            _logger.LogWarning(
                "Knowledge Base entry '{Entry}' exceeds the {Limit}-byte read limit; listing it without "
                    + "frontmatter rather than dropping it from the regenerated index and table of contents.",
                relFile,
                SandboxReadLimits.KnowledgeEntryBytes
            );
            metas.Add(
                new KnowledgeEntryMeta(
                    relFile,
                    $"{SlugFromRelPath(relFile)} — too large to index; frontmatter unread",
                    [],
                    ScopeSegment(relFile) ?? string.Empty,
                    [],
                    string.Empty
                )
            );
            return;
        }

        var content = read.Content;
        var meta = content is null ? null : KnowledgeIndex.ParseFrontmatter(relFile, content);
        if (meta is null)
        {
            _logger.LogDebug("Skipping Knowledge Base entry '{Entry}' with no parseable frontmatter during regen.", relFile);
            return;
        }

        metas.Add(meta);
    }

    /// <summary>True for the ToC/index bookkeeping files and dotfiles the entry walk must ignore.</summary>
    private static bool IsBookkeeping(string name) =>
        name.StartsWith('.')
        || string.Equals(name, TocFileName, StringComparison.Ordinal)
        || string.Equals(name, IndexFileName, StringComparison.Ordinal);

    /// <summary>
    /// True for the reserved per-developer review-feedback directory
    /// (<see cref="ReviewFeedbackAgent.DevelopersDirectory"/>). Those records are about ONE person and are
    /// delivered by targeted injection into that person's own PRs; letting them into <c>_index.jsonl</c> /
    /// <c>_toc.md</c> would put every developer's record into every reviewer's context and spend the shared
    /// retrieval budget on it. Matched case-insensitively because a case-insensitive checkout (Windows)
    /// collapses <c>Developers/</c> onto <c>developers/</c>.
    /// </summary>
    private static bool IsDevelopersDirectory(string name) =>
        string.Equals(name, ReviewFeedbackAgent.DevelopersDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>The final path segment (file name) of a KB-relative path such as <c>system/x.md</c>.</summary>
    private static string LeafName(string relPath) => relPath[(relPath.LastIndexOf('/') + 1)..];

    /// <summary>The scope (first path segment) of <paramref name="relPath"/>, or <c>null</c> when it has none.</summary>
    private static string? ScopeSegment(string relPath)
    {
        var slash = relPath.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? relPath[..slash] : null;
    }

    /// <summary>
    /// Returns the scope directory the entry should use, reusing the case of an existing Knowledge Base
    /// directory that matches <paramref name="scope"/> case-insensitively (else <paramref name="scope"/>
    /// as-is). This keeps a scope whose case the model varies across runs ('MCQdbDEV' vs 'mcqdbdev') in a
    /// single directory — otherwise the two are distinct in git but collide on a case-insensitive checkout,
    /// silently dropping entries. A listing failure never blocks the write: it falls back to the model's case.
    /// </summary>
    private async Task<string> ReconcileScopeCaseAsync(
        string knowledgeBaseDir, string scope, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> children;
        try
        {
            children = await _fileSystem.ListFilesAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Listing the Knowledge Base to reconcile scope '{Scope}' case failed; using it as-is.", scope);
            return scope;
        }

        var existing = children.FirstOrDefault(
            child => string.Equals(child, scope, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.Equals(existing, scope, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Reconciling Knowledge Base scope '{Scope}' to the existing directory '{Existing}' to avoid a "
                    + "case-variant collision.",
                scope,
                existing);
            return existing;
        }

        return scope;
    }

    /// <summary>The file stem of <paramref name="relPath"/> (a last-resort title when the model omits one).</summary>
    private static string SlugFromRelPath(string relPath)
    {
        var name = relPath[(relPath.LastIndexOf('/') + 1)..];
        return name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
    }

    /// <summary>
    /// Lowercases <paramref name="title"/> and collapses every run of non-alphanumeric characters to a
    /// single hyphen, trimming leading/trailing hyphens — a deterministic, filesystem-safe file stem.
    /// </summary>
    private static string Slugify(string title)
    {
        var builder = new StringBuilder(title.Length);
        var pendingHyphen = false;
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
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
        return slug.Length == 0 ? "entry" : slug;
    }

    private static string JoinPath(string root, string relative) =>
        $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}

/// <summary>How an extraction attempt ended. The distinction is load-bearing: only <see cref="Failed"/>
/// is worth retrying, and the caller cannot tell the two non-writing outcomes apart without it.</summary>
internal enum KnowledgeExtractionOutcome
{
    /// <summary>An entry was written to the Knowledge Base.</summary>
    Wrote,

    /// <summary>The agent legitimately declined — the PR carried no durable knowledge. Not a failure.</summary>
    Declined,

    /// <summary>The attempt failed (unusable reply, or the write/commit/push did not land). Retryable.</summary>
    Failed,
}

/// <summary>
/// The outcome of an extraction attempt, plus — when it wrote — the Knowledge Base entry and the agent
/// run id that produced it.
/// </summary>
internal sealed record KnowledgeExtractionResult(
    KnowledgeExtractionOutcome Outcome,
    string? EntryFileName = null,
    string? RunId = null
)
{
    public static KnowledgeExtractionResult Declined(string? runId) =>
        new(KnowledgeExtractionOutcome.Declined, RunId: runId);

    public static KnowledgeExtractionResult Failed(string? runId = null) =>
        new(KnowledgeExtractionOutcome.Failed, RunId: runId);

    public static KnowledgeExtractionResult Wrote(string entryFileName, string? runId) =>
        new(KnowledgeExtractionOutcome.Wrote, entryFileName, runId);
}

/// <summary>
/// The header markers the extraction agent emits — <c>Scope</c>, <c>Title</c>, <c>Tags</c>, an optional
/// <c>Updates</c> relpath — and the entry <c>Body</c> that follows them. The daemon turns these into the
/// deterministic frontmatter; the model never writes frontmatter itself.
/// </summary>
internal sealed record ParsedEntry(
    string Scope,
    string Title,
    IReadOnlyList<string> Tags,
    string? Updates,
    string Body
);
