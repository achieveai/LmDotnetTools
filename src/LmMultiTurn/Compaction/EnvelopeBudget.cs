using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>What <see cref="EnvelopeBudget.Fit" /> shrank, as counts: never text.</summary>
internal sealed record EnvelopeFit
{
    public int IndexEntriesCoalesced { get; init; }

    public int DecisionsDropped { get; init; }

    public int GoalsDropped { get; init; }

    public int ArtifactsDropped { get; init; }

    /// <summary>The agents whose task or outcome shrank toward its recall marker.</summary>
    public int AgentsTrimmed { get; init; }

    public int NarrativeCharsTrimmed { get; init; }

    public int InstructionsTrimmed { get; init; }

    /// <summary>The seqs of the standing instructions dropped, by rank; their rows stay recallable through the index.</summary>
    public IReadOnlyDictionary<InstructionRank, IReadOnlyList<long>> DroppedInstructionSeqs { get; init; } =
        new Dictionary<InstructionRank, IReadOnlyList<long>>();

    /// <summary>False when the envelope is still over its cap after every step.</summary>
    public bool Fits { get; init; } = true;

    public int InstructionsDropped => DroppedInstructionSeqs.Values.Sum(s => s.Count);

    public bool Shrunk =>
        IndexEntriesCoalesced
            + DecisionsDropped
            + GoalsDropped
            + ArtifactsDropped
            + AgentsTrimmed
            + NarrativeCharsTrimmed
            + InstructionsTrimmed
            + InstructionsDropped
        > 0;
}

/// <summary>Which standing instructions the envelope budget drops first, lowest first.</summary>
internal enum InstructionRank
{
    /// <summary>A quote of a row that is not human input.</summary>
    Other,

    /// <summary>Another agent's directive (<c>DelegateTask</c>, <c>Steer</c>).</summary>
    Directive,

    /// <summary>A user's instruction.</summary>
    User,
}

/// <summary>
///     The deterministic envelope budget (spec 679 §2.5, §3.4). Chaining merges every previous section, so without it a
///     long chain breaks V9 on every build, the fallback included. While the rendered envelope is over the V9 cap, what
///     the chain carries shrinks in a fixed order: the index coalesces to <see cref="MinIndexEntries" />; the oldest
///     carried decisions, then goals, then artifacts drop; sub-agent tasks and outcomes shrink toward their recall
///     markers; the narrative loses its start; standing instructions are trimmed; standing instructions drop (see
///     <see cref="InstructionRank" />), never the newest user instruction; and last, the index coalesces to one entry.
///     <c>CurrentInstruction</c> is never touched. Everything shrunk stays reachable through the index and
///     RecallConversation.
/// </summary>
internal static class EnvelopeBudget
{
    /// <summary>The index entries kept while anything else can still shrink.</summary>
    public const int MinIndexEntries = 8;

    private const string Elided = "…";

    /// <param name="manifest">The assembled manifest.</param>
    /// <param name="narrative">The narrative; a trailing <paramref name="protectedSuffix" /> is never trimmed.</param>
    /// <param name="previous">The previous checkpoint's manifest: what counts as carried.</param>
    /// <param name="measure">The rendered envelope's tokens for a manifest and narrative.</param>
    /// <param name="cap">The V9 cap.</param>
    /// <param name="rank">The drop rank of the row at a seq.</param>
    /// <param name="protectedSuffix">The end of the narrative that always stays whole (the fallback line).</param>
    /// <param name="rowText">The text of the row at a seq, so a quote verbatim in its row is never read as a trim.</param>
    /// <param name="agentsAt">
    ///     The agents with each task and outcome bounded to an allowance in chars
    ///     (<see cref="ManifestAssembler.BoundAgents" />); null leaves the agents as assembled.
    /// </param>
    public static (ContextManifest Manifest, string Narrative, EnvelopeFit Fit) Fit(
        ContextManifest manifest,
        string narrative,
        ContextManifest? previous,
        Func<ContextManifest, string, long> measure,
        long cap,
        Func<long, InstructionRank> rank,
        string? protectedSuffix = null,
        Func<long, string?>? rowText = null,
        Func<int, IReadOnlyList<AgentRef>>? agentsAt = null
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(narrative);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(rank);

        bool Over(ContextManifest m) => measure(m, narrative) > cap;
        if (!Over(manifest))
        {
            return (manifest, narrative, new EnvelopeFit());
        }

        (manifest, var coalesced) = CoalesceIndex(manifest, MinIndexEntries, Over);

        var carriedDecisions = (previous?.Decisions ?? []).ToHashSet();
        (manifest, var decisions) = DropOldest(
            manifest,
            m => m.Decisions,
            (m, kept) => m with { Decisions = kept },
            carriedDecisions.Contains,
            Over
        );

        var carriedGoals = (previous?.Goals ?? []).ToHashSet(StringComparer.Ordinal);
        (manifest, var goals) = DropOldest(
            manifest,
            m => m.Goals,
            (m, kept) => m with { Goals = kept },
            carriedGoals.Contains,
            Over
        );

        var carriedPaths = (previous?.Artifacts ?? []).Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        (manifest, var artifacts) = DropOldest(
            manifest,
            m => m.Artifacts,
            (m, kept) => m with { Artifacts = kept },
            a => carriedPaths.Contains(a.Path),
            Over
        );

        (manifest, var agents) = ShrinkAgents(manifest, agentsAt, Over);

        var before = narrative.Length;
        if (Over(manifest))
        {
            var fixedManifest = manifest;
            narrative = KeepEnd(narrative, protectedSuffix, text => measure(fixedManifest, text) <= cap);
        }

        rowText ??= _ => null;
        (manifest, var trimmed) = TrimInstructions(manifest, rowText, Over);
        (manifest, var droppedSeqs) = DropInstructions(manifest, rank, Over);
        (manifest, var lastCoalesced) = CoalesceIndex(manifest, 1, Over);

        return (
            manifest,
            narrative,
            new EnvelopeFit
            {
                IndexEntriesCoalesced = coalesced + lastCoalesced,
                DecisionsDropped = decisions,
                GoalsDropped = goals,
                ArtifactsDropped = artifacts,
                AgentsTrimmed = agents,
                NarrativeCharsTrimmed = Math.Max(0, before - narrative.Length),
                InstructionsTrimmed = trimmed,
                DroppedInstructionSeqs = droppedSeqs,
                Fits = !Over(manifest),
            }
        );
    }

    /// <summary>
    ///     The longest end of <paramref name="text" /> that <paramref name="fits" />, marked with a leading "…", followed
    ///     by <paramref name="suffix" /> whole. The whole text when it fits; the suffix alone (or "…") when no end does.
    /// </summary>
    internal static string KeepEnd(string text, string? suffix, Func<string, bool> fits)
    {
        suffix ??= string.Empty;
        var body = suffix.Length > 0 && text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;
        if (fits(body + suffix))
        {
            return body + suffix;
        }

        var (low, high) = (0, body.Length);
        while (low < high)
        {
            var keep = (low + high + 1) / 2;
            (low, high) = fits(Elided + body[^keep..] + suffix) ? (keep, high) : (low, keep - 1);
        }

        return low > 0 ? Elided + body[^low..] + suffix
            : suffix.Length > 0 ? suffix.TrimStart()
            : Elided;
    }

    private static (ContextManifest, int) CoalesceIndex(
        ContextManifest manifest,
        int floor,
        Func<ContextManifest, bool> over
    )
    {
        var merged = 0;
        while (manifest.Index.Count > floor && over(manifest))
        {
            manifest = manifest with
            {
                Index = ManifestAssembler.CoalesceOldest(manifest.Index, manifest.Index.Count - 1),
            };
            merged++;
        }

        return (manifest, merged);
    }

    /// <summary>Removes the earliest items <paramref name="carried" /> selects, one at a time, while over the cap.</summary>
    private static (ContextManifest, int) DropOldest<T>(
        ContextManifest manifest,
        Func<ContextManifest, IReadOnlyList<T>> section,
        Func<ContextManifest, IReadOnlyList<T>, ContextManifest> apply,
        Func<T, bool> carried,
        Func<ContextManifest, bool> over
    )
    {
        var kept = new List<T>(section(manifest));
        var dropped = 0;
        while (over(manifest))
        {
            var oldest = kept.FindIndex(i => carried(i));
            if (oldest < 0)
            {
                break;
            }

            kept.RemoveAt(oldest);
            manifest = apply(manifest, [.. kept]);
            dropped++;
        }

        return (manifest, dropped);
    }

    /// <summary>
    ///     Every standing instruction longer than one shared allowance is shrunk to it
    ///     (<see cref="CurrentInstructionQuotes.Shrink" />), with the largest allowance that fits and never below
    ///     <see cref="CurrentInstructionQuotes.MinKeptChars" />.
    /// </summary>
    private static (ContextManifest, int) TrimInstructions(
        ContextManifest manifest,
        Func<long, string?> rowText,
        Func<ContextManifest, bool> over
    )
    {
        var original = manifest.Instructions;
        var longest =
            original.Count == 0 ? 0 : original.Max(q => CurrentInstructionQuotes.VisibleChars(q, rowText(q.Seq)));
        if (longest <= CurrentInstructionQuotes.MinKeptChars || !over(manifest))
        {
            return (manifest, 0);
        }

        ContextManifest At(int keep) =>
            manifest with
            {
                Instructions = [.. original.Select(q => CurrentInstructionQuotes.Shrink(q, keep, rowText(q.Seq)))],
            };

        var shrunk = At(LargestFitting(CurrentInstructionQuotes.MinKeptChars, longest - 1, keep => over(At(keep))));
        return (shrunk, original.Where((q, i) => q != shrunk.Instructions[i]).Count());
    }

    /// <summary>
    ///     Every sub-agent's task and outcome shrink to one shared allowance, the largest that fits, down to the marker
    ///     alone. What they lose is verbatim in the row the marker names, so this goes before the narrative, which no row
    ///     holds, and before any instruction.
    /// </summary>
    private static (ContextManifest, int) ShrinkAgents(
        ContextManifest manifest,
        Func<int, IReadOnlyList<AgentRef>>? agentsAt,
        Func<ContextManifest, bool> over
    )
    {
        if (agentsAt is null || manifest.Agents.Count == 0 || !over(manifest))
        {
            return (manifest, 0);
        }

        ContextManifest At(int keep) => manifest with { Agents = agentsAt(keep) };

        var shrunk = At(LargestFitting(0, ManifestAssembler.MaxAgentOutcomeChars - 1, keep => over(At(keep))));
        return (shrunk, manifest.Agents.Where((a, i) => a != shrunk.Agents[i]).Count());
    }

    /// <summary>The largest allowance in <paramref name="low" />…<paramref name="high" /> not over the cap, else <paramref name="low" />.</summary>
    private static int LargestFitting(int low, int high, Func<int, bool> over)
    {
        if (over(low))
        {
            return low;
        }

        while (low < high)
        {
            var keep = (low + high + 1) / 2;
            (low, high) = over(keep) ? (low, keep - 1) : (keep, high);
        }

        return low;
    }

    /// <summary>
    ///     The last resort: standing instructions drop one at a time. A quote of a row in <c>CurrentInstruction</c> goes
    ///     first, since that section repeats the row and is never touched, so the drop loses nothing; it is never the
    ///     protected newest user instruction either. Then lowest <paramref name="rank" /> first and oldest first within it.
    ///     The newest user instruction is never dropped.
    /// </summary>
    private static (ContextManifest, IReadOnlyDictionary<InstructionRank, IReadOnlyList<long>>) DropInstructions(
        ContextManifest manifest,
        Func<long, InstructionRank> rank,
        Func<ContextManifest, bool> over
    )
    {
        var current = manifest.CurrentInstruction.Select(q => q.Seq).ToHashSet();
        var newestUser = manifest
            .Instructions.Where(q => !current.Contains(q.Seq) && rank(q.Seq) == InstructionRank.User)
            .Select(q => q.Seq)
            .DefaultIfEmpty(long.MinValue)
            .Max();
        var dropOrder = manifest
            .Instructions.Where(q => q.Seq != newestUser)
            .OrderBy(q => !current.Contains(q.Seq))
            .ThenBy(q => rank(q.Seq))
            .ThenBy(q => q.Seq)
            .ToList();
        var kept = new List<QuotedItem>(manifest.Instructions);
        var dropped = new List<QuotedItem>();
        foreach (var quote in dropOrder)
        {
            if (!over(manifest))
            {
                break;
            }

            _ = kept.Remove(quote);
            dropped.Add(quote);
            manifest = manifest with { Instructions = [.. kept] };
        }

        return (
            manifest,
            dropped
                .GroupBy(q => rank(q.Seq))
                .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)[.. g.Select(q => q.Seq).Distinct()])
        );
    }

    /// <summary>
    ///     What each section adds to the envelope, in tokens: the envelope less the envelope without that section. For
    ///     the log when the envelope cannot fit; sizes only, never text.
    /// </summary>
    public static IReadOnlyDictionary<string, long> SectionTokens(
        ContextManifest manifest,
        string narrative,
        Func<ContextManifest, string, long> measure
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(measure);
        var whole = measure(manifest, narrative);
        long Without(ContextManifest m, string n) => whole - measure(m, n);
        return new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [nameof(ContextManifest.CurrentInstruction)] = Without(
                manifest with
                {
                    CurrentInstruction = [],
                },
                narrative
            ),
            [nameof(ContextManifest.Instructions)] = Without(manifest with { Instructions = [] }, narrative),
            [nameof(ContextManifest.Goals)] = Without(manifest with { Goals = [] }, narrative),
            [nameof(ContextManifest.Decisions)] = Without(manifest with { Decisions = [] }, narrative),
            [nameof(ContextManifest.Tasks)] = Without(manifest with { Tasks = [] }, narrative),
            [nameof(ContextManifest.Artifacts)] = Without(manifest with { Artifacts = [] }, narrative),
            [nameof(ContextManifest.Agents)] = Without(manifest with { Agents = [] }, narrative),
            [nameof(ContextManifest.Index)] = Without(manifest with { Index = [] }, narrative),
            ["Narrative"] = Without(manifest, string.Empty),
            ["Total"] = whole,
        };
    }
}
