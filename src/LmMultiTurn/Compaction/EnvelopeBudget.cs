using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

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

    /// <summary>Completed board tasks left out, oldest first; the board still lists them.</summary>
    public int CompletedTasksDropped { get; init; }

    /// <summary>Completed, errored or stopped agents left out, oldest first; the roster still lists them.</summary>
    public int FinishedAgentsDropped { get; init; }

    public int NarrativeCharsTrimmed { get; init; }

    public int InstructionsTrimmed { get; init; }

    /// <summary>The seqs of the standing instructions dropped, by rank; their rows stay recallable through the index.</summary>
    public IReadOnlyDictionary<InstructionRank, IReadOnlyList<long>> DroppedInstructionSeqs { get; init; } =
        new Dictionary<InstructionRank, IReadOnlyList<long>>();

    /// <summary>What the last rung left out of the open tasks, live agents and artifacts, as counts; null when nothing.</summary>
    public ManifestOmissions? Omitted { get; init; }

    /// <summary>False when the envelope is still over its hard cap after every step.</summary>
    public bool Fits { get; init; } = true;

    /// <summary>False when the envelope is over its target: within the hard cap, so still valid.</summary>
    public bool MeetsTarget { get; init; } = true;

    public int InstructionsDropped => DroppedInstructionSeqs.Values.Sum(s => s.Count);

    public bool Shrunk =>
        IndexEntriesCoalesced
            + DecisionsDropped
            + GoalsDropped
            + ArtifactsDropped
            + AgentsTrimmed
            + CompletedTasksDropped
            + FinishedAgentsDropped
            + (Omitted is null ? 0 : Omitted.Tasks + Omitted.Agents + Omitted.Artifacts)
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
///     The deterministic envelope budget (spec 679 §2.5, §3.4). Chaining merges every previous section, and the board and
///     roster only grow, so without it a long conversation breaks V9 on every build, the fallback included. It works in
///     two phases against two sizes.
///     <para>
///         Phase A aims at the target and loses nothing another source does not hold: the index coalesces to
///         <see cref="MinIndexEntries" />; the oldest carried decisions, then goals, then artifacts drop; sub-agent tasks
///         and outcomes shrink toward their recall markers; completed board tasks drop, oldest first; and completed,
///         errored or stopped agents drop, oldest first. Over the target but within the hard cap, it stops there: the
///         target is best effort.
///     </para>
///     <para>
///         Phase B runs only over the hard cap and stops at it: the narrative loses its start; standing instructions are
///         trimmed; standing instructions drop (see <see cref="InstructionRank" />), never the newest user instruction;
///         the index coalesces to one entry; and last, the open tasks, live agents and artifacts keep only their newest
///         items, the rest counted in <see cref="ContextManifest.Omitted" />.
///     </para>
///     <c>CurrentInstruction</c> is never touched: it is bounded where it is quoted (<see cref="CurrentInstructionQuotes" />).
///     Everything shrunk stays reachable through the board, the roster, the index and RecallConversation.
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
    /// <param name="cap">The target phase A aims at.</param>
    /// <param name="rank">The drop rank of the row at a seq.</param>
    /// <param name="protectedSuffix">The end of the narrative that always stays whole (the fallback line).</param>
    /// <param name="rowText">The text of the row at a seq, so a quote verbatim in its row is never read as a trim.</param>
    /// <param name="agentsAt">
    ///     The agents with each task and outcome bounded to an allowance in chars
    ///     (<see cref="ManifestAssembler.BoundAgents" />); null leaves the agents as assembled.
    /// </param>
    /// <param name="hardCap">The V9 cap phase B stops at; null, or below <paramref name="cap" />, means the cap.</param>
    public static (ContextManifest Manifest, string Narrative, EnvelopeFit Fit) Fit(
        ContextManifest manifest,
        string narrative,
        ContextManifest? previous,
        Func<ContextManifest, string, long> measure,
        long cap,
        Func<long, InstructionRank> rank,
        string? protectedSuffix = null,
        Func<long, string?>? rowText = null,
        Func<int, IReadOnlyList<AgentRef>>? agentsAt = null,
        long? hardCap = null
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(narrative);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(rank);

        var limit = Math.Max(cap, hardCap ?? cap);
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

        (manifest, var completedTasks) = DropOldest(
            manifest,
            m => m.Tasks,
            (m, kept) => m with { Tasks = kept },
            t => string.Equals(t.Status, nameof(TodoTaskStatus.Completed), StringComparison.OrdinalIgnoreCase),
            Over
        );
        (manifest, var finishedAgents) = DropOldest(
            manifest,
            m => m.Agents,
            (m, kept) => m with { Agents = kept },
            a => FinishedAgentStatuses.Contains(a.Status),
            Over
        );

        // Phase B: only what is still over the hard cap pays with what no other source holds as compactly.
        bool OverLimit(ContextManifest m) => measure(m, narrative) > limit;
        var before = narrative.Length;
        if (OverLimit(manifest))
        {
            var fixedManifest = manifest;
            narrative = KeepEnd(narrative, protectedSuffix, text => measure(fixedManifest, text) <= limit);
        }

        rowText ??= _ => null;
        (manifest, var trimmed) = TrimInstructions(manifest, rowText, OverLimit);
        (manifest, var droppedSeqs) = DropInstructions(manifest, rank, OverLimit);
        (manifest, var lastCoalesced) = CoalesceIndex(manifest, 1, OverLimit);
        manifest = KeepNewest(manifest, OverLimit);

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
                CompletedTasksDropped = completedTasks,
                FinishedAgentsDropped = finishedAgents,
                Omitted = manifest.Omitted,
                NarrativeCharsTrimmed = Math.Max(0, before - narrative.Length),
                InstructionsTrimmed = trimmed,
                DroppedInstructionSeqs = droppedSeqs,
                Fits = !OverLimit(manifest),
                MeetsTarget = !Over(manifest),
            }
        );
    }

    /// <summary>The roster statuses of an agent that will not run again unless asked: its line is history.</summary>
    private static readonly HashSet<string> FinishedAgentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SubAgentStatus.Completed),
        nameof(SubAgentStatus.Error),
        nameof(SubAgentStatus.Stopped),
    };

    /// <summary>
    ///     The last rung: the open tasks, live agents and artifacts keep their newest <c>K</c> items each, with the
    ///     largest <c>K</c> that fits, and the rest become one count line per section. With <c>K</c> at 0 every one of
    ///     them is a count, which is what bounds the envelope whatever the board or roster holds.
    /// </summary>
    private static ContextManifest KeepNewest(ContextManifest manifest, Func<ContextManifest, bool> over)
    {
        var most = Math.Max(manifest.Tasks.Count, Math.Max(manifest.Agents.Count, manifest.Artifacts.Count));
        if (most == 0 || !over(manifest))
        {
            return manifest;
        }

        var original = manifest;

        ContextManifest At(int keep)
        {
            var omitted = new ManifestOmissions
            {
                Tasks = Math.Max(0, original.Tasks.Count - keep),
                Agents = Math.Max(0, original.Agents.Count - keep),
                Artifacts = Math.Max(0, original.Artifacts.Count - keep),
            };
            return original with
            {
                Tasks = [.. original.Tasks.Skip(omitted.Tasks)],
                Agents = [.. original.Agents.Skip(omitted.Agents)],
                Artifacts = [.. original.Artifacts.Skip(omitted.Artifacts)],
                Omitted = omitted is { Tasks: 0, Agents: 0, Artifacts: 0 } ? null : omitted,
            };
        }

        return At(LargestFitting(0, most - 1, keep => over(At(keep))));
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

    /// <summary>
    ///     Removes the fewest earliest items <paramref name="carried" /> selects that bring the envelope under the cap, or
    ///     all of them when none does. Found by bisection: a board or roster can hold thousands.
    /// </summary>
    private static (ContextManifest, int) DropOldest<T>(
        ContextManifest manifest,
        Func<ContextManifest, IReadOnlyList<T>> section,
        Func<ContextManifest, IReadOnlyList<T>, ContextManifest> apply,
        Func<T, bool> carried,
        Func<ContextManifest, bool> over
    )
    {
        var items = section(manifest);
        var droppable = items.Count(carried);
        if (droppable == 0 || !over(manifest))
        {
            return (manifest, 0);
        }

        var original = manifest;

        ContextManifest Without(int count)
        {
            var kept = new List<T>(items.Count);
            foreach (var item in items)
            {
                if (count > 0 && carried(item))
                {
                    count--;
                }
                else
                {
                    kept.Add(item);
                }
            }

            return apply(original, kept);
        }

        // The fewest drops that fit: the largest number kept, counted back from all of them.
        var most = LargestFitting(0, droppable - 1, keep => over(Without(droppable - keep)));
        return (Without(droppable - most), droppable - most);
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
            [nameof(ContextManifest.Omitted)] = Without(manifest with { Omitted = null }, narrative),
            ["Narrative"] = Without(manifest, string.Empty),
            ["Total"] = whole,
        };
    }
}
