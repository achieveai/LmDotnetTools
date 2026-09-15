using System.Globalization;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>The knobs of the deterministic manifest sections.</summary>
internal sealed record ManifestAssemblerOptions
{
    /// <summary>The most index entries a manifest carries; older adjacent entries coalesce past it.</summary>
    public int MaxIndexEntries { get; init; } = 60;

    /// <summary>Tool names whose call rows name an artifact.</summary>
    public IReadOnlyCollection<string> FileToolNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Write", "Edit", "MultiEdit", "NotebookEdit" };

    /// <summary>Argument names, tried in order, that carry the artifact path in a file-tool call.</summary>
    public IReadOnlyList<string> PathArgumentNames { get; init; } = ["file_path", "path", "notebook_path"];
}

/// <summary>
///     Builds a <see cref="ContextManifest" /> from the parts the model may not decide and the parts it
///     may (spec 679 §3.3). Deterministic sections — <c>CurrentInstruction</c>, <c>Tasks</c> from the
///     board, <c>Agents</c> from the roster, <c>Index</c> spans, file-tool <c>Artifacts</c>,
///     <c>Recovery</c> — come from the rows and the loop's state; the summary contributes quotes to
///     verify, goals, headlines, outcomes and model-named artifacts. Chaining (§2.5) merges the previous
///     manifest field by field, so nothing a first checkpoint quoted is lost by a second.
/// </summary>
internal static class ManifestAssembler
{
    /// <summary>The longest headline a coalesced index entry carries.</summary>
    internal const int MaxCoalescedHeadlineChars = 200;

    /// <summary>The most run ids a coalesced index entry names before counting the rest.</summary>
    internal const int MaxCoalescedRunIds = 3;

    /// <summary>The most of a sub-agent's result its <see cref="AgentRef.Outcome" /> shows around a recall marker.</summary>
    internal const int MaxAgentOutcomeChars = 600;

    /// <summary>The most of a sub-agent's task its <see cref="AgentRef.Task" /> shows around a recall marker.</summary>
    internal const int MaxAgentTaskChars = 300;

    private const string UnknownRunId = "unknown";

    public static ContextManifest Assemble(
        IReadOnlyList<SequencedMessage> rows,
        CutDecision.Cut cut,
        ContextManifest? previous,
        long previousBoundary,
        CheckpointSummary summary,
        TodoBoardSnapshot? board,
        IReadOnlyList<AgentRef> roster,
        ManifestAssemblerOptions? options = null,
        Action<IReadOnlyList<QuotedItem>>? onDroppedQuotes = null,
        CheckpointValidationOptions? validation = null
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(roster);
        options ??= new ManifestAssemblerOptions();
        validation ??= new CheckpointValidationOptions();

        var covered = rows.Where(r => r.Seq > previousBoundary && r.Seq <= cut.Seq).ToList();
        var bySeq = rows.ToDictionary(r => r.Seq);
        var dropped = new List<QuotedItem>();

        // Whole, or trimmed to half the V9 envelope cap exactly as the validator recomputes it.
        IReadOnlyList<QuotedItem> Bounded(IReadOnlyList<SequencedMessage> humanRows) =>
            CurrentInstructionQuotes.Quote(
                humanRows,
                CurrentInstructionQuotes.Budget(validation.CheckpointTokenCap),
                validation.TextEstimator
            );

        var currentInstruction = Bounded(cut.CurrentInstruction);

        // Human instructions are never silently lost (R5): the previous current instruction and every human row of the
        // newly covered runs that the new current instruction does not quote are carried as standing instructions,
        // deterministically, so a summary that skips them (or a fallback with no summary) cannot drop them.
        var carried = Bounded([
            .. (previous?.CurrentInstruction ?? [])
                .Select(q => q.Seq)
                .Concat(covered.Where(r => r.IsHumanRow).Select(r => r.Seq))
                .Except(currentInstruction.Select(q => q.Seq))
                .Order()
                .Select(seq => bySeq.GetValueOrDefault(seq))
                .OfType<SequencedMessage>()
                .Where(r => r.Seq <= cut.Seq && !string.IsNullOrEmpty(r.Text)),
        ]);
        var carriedSeqs = carried.Select(q => q.Seq).ToHashSet();

        // A row quoted trimmed is too large to quote again: the model's copy of a pasted prompt alone would break V9, and
        // RecallConversation reads it whole. A row quoted whole keeps its standing quotes.
        var trimmedSeqs = currentInstruction
            .Concat(carried)
            .Where(q => !string.Equals(q.Quote, bySeq.GetValueOrDefault(q.Seq)?.Text, StringComparison.Ordinal))
            .Select(q => q.Seq)
            .ToHashSet();

        // A model's quote that is not verbatim (a tool call row has no text; a "user: " prefix; reflowed whitespace)
        // would fail V3 and take the whole checkpoint with it. It is dropped here instead; V3 still guards the rest.
        IReadOnlyList<QuotedItem> Verbatim(IReadOnlyList<QuotedItem> quotes, string section)
        {
            var kept = new List<QuotedItem>(quotes.Count);
            foreach (var quote in quotes)
            {
                if (
                    !trimmedSeqs.Contains(quote.Seq)
                    && CheckpointValidator.CheckSubstringQuote(quote, section, cut.Seq, bySeq) is null
                )
                {
                    kept.Add(quote);
                }
                else
                {
                    dropped.Add(quote);
                }
            }

            return kept;
        }

        var manifest = new ContextManifest
        {
            CurrentInstruction = currentInstruction,
            // A carried row replaces the other quotes of its seq: whole, it contains them.
            Instructions = MergeQuotes(
                [
                    .. Verbatim(MergeQuotes(previous?.Instructions, summary.Instructions), "Instructions")
                        .Where(q => !carriedSeqs.Contains(q.Seq)),
                ],
                carried
            ),
            Goals = [.. (previous?.Goals ?? []).Concat(summary.Goals).Distinct(StringComparer.Ordinal)],
            Decisions = Verbatim(MergeQuotes(previous?.Decisions, summary.Decisions), "Decisions"),
            Tasks = board is null ? [.. summary.Tasks.Select(t => t with { Id = null })] : Flatten(board.Tasks),
            Artifacts = MergeArtifacts(previous?.Artifacts, ArtifactsFromRows(covered, options), summary.Artifacts),
            Agents = BoundAgents(rows, cut.Seq, roster, summary, MaxAgentOutcomeChars),
            Index = BuildIndex(previous?.Index, previousBoundary, covered, cut.Seq, summary.Headlines, options),
            Recovery = cut.Recovery,
        };

        if (dropped.Count > 0)
        {
            onDroppedQuotes?.Invoke(dropped);
        }

        return manifest;
    }

    /// <summary>The board as a flat list, depth first, without removed tasks.</summary>
    public static IReadOnlyList<TaskRef> Flatten(IReadOnlyList<TodoTaskNode> nodes)
    {
        var result = new List<TaskRef>();
        Walk(nodes);
        return result;

        void Walk(IReadOnlyList<TodoTaskNode> level)
        {
            foreach (var node in level)
            {
                if (node.Status == TodoTaskStatus.Removed)
                {
                    continue;
                }

                result.Add(
                    new TaskRef
                    {
                        Id = node.Id,
                        Title = node.Title,
                        Status = node.Status.ToString(),
                    }
                );
                Walk(node.SubTasks);
            }
        }
    }

    private static IReadOnlyList<QuotedItem> MergeQuotes(
        IReadOnlyList<QuotedItem>? older,
        IReadOnlyList<QuotedItem> newer
    ) => [.. (older ?? []).Concat(newer).DistinctBy(q => (q.Seq, q.Quote)).OrderBy(q => q.Seq)];

    private static IReadOnlyList<ArtifactRef> MergeArtifacts(params IReadOnlyList<ArtifactRef>?[] sources)
    {
        var byPath = new Dictionary<string, ArtifactRef>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var artifact in sources.Where(s => s is not null).SelectMany(s => s!))
        {
            if (string.IsNullOrWhiteSpace(artifact.Path))
            {
                continue;
            }

            if (byPath.TryGetValue(artifact.Path, out var existing))
            {
                byPath[artifact.Path] = existing with
                {
                    Hash = existing.Hash ?? artifact.Hash,
                    OriginSeq = Min(existing.OriginSeq, artifact.OriginSeq),
                };
            }
            else
            {
                byPath[artifact.Path] = artifact;
                order.Add(artifact.Path);
            }
        }

        return [.. order.Select(p => byPath[p])];
    }

    private static long? Min(long? a, long? b) =>
        a is null ? b
        : b is null ? a
        : Math.Min(a.Value, b.Value);

    private static List<ArtifactRef> ArtifactsFromRows(List<SequencedMessage> covered, ManifestAssemblerOptions options)
    {
        var result = new List<ArtifactRef>();
        foreach (var row in covered)
        {
            var calls = ToolCallsOf(row.Message);

            foreach (var call in calls)
            {
                if (call.FunctionName is null || !options.FileToolNames.Contains(call.FunctionName))
                {
                    continue;
                }

                var path = PathArgument(call.FunctionArgs, options.PathArgumentNames);
                if (path is not null)
                {
                    result.Add(new ArtifactRef { Path = path, OriginSeq = row.Seq });
                }
            }
        }

        return result;
    }

    private static string? PathArgument(string? args, IReadOnlyList<string> names)
    {
        var strings = StringArguments(args);
        return names.Select(strings.GetValueOrDefault).FirstOrDefault(path => path is { Length: > 0 });
    }

    /// <summary>The top-level string arguments of a call, by name; none when the arguments never parsed.</summary>
    private static Dictionary<string, string> StringArguments(string? args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(args))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(args);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    _ = result.TryAdd(property.Name, property.Value.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // A call whose arguments never parsed names nothing.
        }

        return result;
    }

    /// <summary>
    ///     The roster with each agent's task and outcome bounded (spec 679 §3.3): at most
    ///     <paramref name="keepChars" /> chars of each, and never more than <see cref="MaxAgentTaskChars" /> of a task or
    ///     <see cref="MaxAgentOutcomeChars" /> of an outcome, kept as a head and tail around a marker naming the row
    ///     RecallConversation reads whole. Only a completed or errored agent has an outcome: the result of its last
    ///     completion notification for its task at or before the cut, else the model's, else the roster's. A task's row is
    ///     that notification, else the call that spawned it, both at or before the cut. Text no row holds (a model's outcome, a task no row carries) keeps its maximum around "…" whatever
    ///     <paramref name="keepChars" /> is, since no marker could say where to read it. The envelope budget calls this again
    ///     with a smaller allowance.
    /// </summary>
    internal static IReadOnlyList<AgentRef> BoundAgents(
        IReadOnlyList<SequencedMessage> rows,
        long cutSeq,
        IReadOnlyList<AgentRef> roster,
        CheckpointSummary summary,
        int keepChars
    )
    {
        return [.. roster.Select(Bound)];

        AgentRef Bound(AgentRef agent)
        {
            string? outcome = null;
            if (IsSettled(agent.Status))
            {
                var (completionSeq, result) = rows.Where(r =>
                        r.Seq <= cutSeq && r.Message is NotifyMessage notify && IsCompletionOf(notify, agent.AgentId)
                    )
                    .Select(r => (r.Seq, Result: ResultOf((NotifyMessage)r.Message, agent.Task)))
                    .LastOrDefault(c => c.Result is not null);
                if (result is not null)
                {
                    outcome = CurrentInstructionQuotes.Bound(
                        result,
                        completionSeq,
                        Math.Min(keepChars, MaxAgentOutcomeChars)
                    );
                }
                else
                {
                    outcome = summary.AgentOutcomes.TryGetValue(agent.AgentId, out var fromModel)
                        ? fromModel
                        : agent.Outcome;
                    outcome = outcome is null ? null : Elide(outcome, MaxAgentOutcomeChars);
                }
            }

            var task = agent.Task;
            if (task is not null && task.Length > Math.Min(keepChars, MaxAgentTaskChars))
            {
                task = TaskRow(task) is { } seq
                    ? CurrentInstructionQuotes.Bound(task, seq, Math.Min(keepChars, MaxAgentTaskChars))
                    : Elide(task, MaxAgentTaskChars);
            }

            return agent with
            {
                Task = task,
                Outcome = outcome,
            };

            // Rows up to the cut only, as for the outcome: the checkpoint describes the thread to its boundary.
            long? TaskRow(string task) =>
                rows.LastOrDefault(r =>
                    r.Seq <= cutSeq
                    && r.Message is NotifyMessage notify
                    && IsCompletionOf(notify, agent.AgentId)
                    && notify.Detail?.Contains(task, StringComparison.Ordinal) == true
                )?.Seq
                ?? rows.FirstOrDefault(r =>
                    r.Seq <= cutSeq
                    && ToolCallsOf(r.Message)
                        .Any(call =>
                            call.FunctionArgs?.Length >= task.Length
                            && StringArguments(call.FunctionArgs).ContainsValue(task)
                        )
                )?.Seq;
        }
    }

    /// <summary>
    ///     Whether the roster status ends a run with a result: completed or error. A queued or running agent (a warm
    ///     resume included) has none yet, and a stop sends no completion, so either would show a previous run's result as
    ///     its current one.
    /// </summary>
    private static bool IsSettled(string status) =>
        string.Equals(status, nameof(SubAgentStatus.Completed), StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, nameof(SubAgentStatus.Error), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     A completion notification of <paramref name="agentId" />: its source call id is the agent id, exactly, so
    ///     <c>agent-1</c> never takes <c>agent-10</c>'s result. One without a source id falls back to the
    ///     <c>id="…"</c> attribute SubAgentManager writes in the block's opening tag.
    /// </summary>
    private static bool IsCompletionOf(NotifyMessage notify, string agentId) =>
        string.Equals(notify.NotifyKind, NotifyKinds.SubAgentCompletion, StringComparison.Ordinal)
        && (
            notify.SourceToolCallId is { } source
                ? string.Equals(source, agentId, StringComparison.Ordinal)
                : notify.Detail?.Contains($"id=\"{agentId}\">", StringComparison.Ordinal) == true
        );

    /// <summary>
    ///     The result (or error) of a <c>&lt;sub-agent …&gt;[Completed] Task: {task}\nResult: {result}\n&lt;/sub-agent&gt;</c>
    ///     block, found after the agent's own task so a task that mentions "Result:" cannot split it. A block for another
    ///     task (an earlier run's) is no result of this one: null. Any other text whole; blank text null.
    /// </summary>
    private static string? ResultOf(NotifyMessage notify, string? task)
    {
        const string Close = "\n</sub-agent>";
        var detail = notify.Detail ?? notify.Label;
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        foreach (var label in new[] { "\nResult: ", "\nError: " })
        {
            var marker = $"Task: {task}{label}";
            var at = task is null ? -1 : detail.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                var start = at + marker.Length;
                var end = detail.EndsWith(Close, StringComparison.Ordinal)
                    ? detail.Length - Close.Length
                    : detail.Length;
                return detail[start..Math.Max(start, end)];
            }
        }

        return detail.StartsWith("<sub-agent ", StringComparison.Ordinal) ? null : detail;
    }

    /// <summary>
    ///     One entry per run span among the newly covered rows, contiguous from the previous boundary to
    ///     the cut by construction: a row the current build could not read leaves no hole. Previous entries
    ///     are kept as they were. Past <see cref="ManifestAssemblerOptions.MaxIndexEntries" /> the oldest
    ///     adjacent pair coalesces until the list fits.
    /// </summary>
    private static IReadOnlyList<IndexEntry> BuildIndex(
        IReadOnlyList<IndexEntry>? previous,
        long previousBoundary,
        List<SequencedMessage> covered,
        long cutSeq,
        IReadOnlyDictionary<string, string> headlines,
        ManifestAssemblerOptions options
    )
    {
        var entries = new List<IndexEntry>(previous ?? []);
        var from = previousBoundary + 1;

        var spans = new List<(string RunId, long Last, int Rows)>();
        foreach (var row in covered)
        {
            var runId = row.EffectiveRunId ?? UnknownRunId;
            if (spans.Count > 0 && string.Equals(spans[^1].RunId, runId, StringComparison.Ordinal))
            {
                spans[^1] = (runId, row.Seq, spans[^1].Rows + 1);
            }
            else
            {
                spans.Add((runId, row.Seq, 1));
            }
        }

        if (spans.Count == 0 && cutSeq >= from)
        {
            spans.Add((UnknownRunId, cutSeq, 0));
        }

        for (var i = 0; i < spans.Count; i++)
        {
            var (runId, last, count) = spans[i];
            var to = i == spans.Count - 1 ? cutSeq : last;
            entries.Add(
                new IndexEntry
                {
                    FromSeq = from,
                    ToSeq = to,
                    RunId = runId,
                    Headline =
                        headlines.TryGetValue(runId, out var headline) && !string.IsNullOrWhiteSpace(headline)
                            ? headline
                            : $"{runId}: {count} rows",
                }
            );
            from = to + 1;
        }

        return CoalesceOldest(entries, options.MaxIndexEntries);
    }

    /// <summary>
    ///     The oldest adjacent pair coalesces until at most <paramref name="maxEntries" /> (and at least one) remain. A
    ///     coalesced entry stays bounded however often it merges: its headline keeps its first and last
    ///     <see cref="MaxCoalescedHeadlineChars" /> characters around "…", and its run ids name
    ///     <see cref="MaxCoalescedRunIds" /> runs and count the rest.
    /// </summary>
    internal static IReadOnlyList<IndexEntry> CoalesceOldest(IReadOnlyList<IndexEntry> index, int maxEntries)
    {
        var entries = new List<IndexEntry>(index);
        while (entries.Count > Math.Max(1, maxEntries))
        {
            var (a, b) = (entries[0], entries[1]);
            entries.RemoveRange(0, 2);
            entries.Insert(
                0,
                new IndexEntry
                {
                    FromSeq = a.FromSeq,
                    ToSeq = b.ToSeq,
                    RunId = MergeRunIds(a.RunId, b.RunId),
                    Headline = Elide($"{a.Headline}; {b.Headline}", MaxCoalescedHeadlineChars),
                }
            );
        }

        return entries;
    }

    /// <summary><paramref name="headline" /> whole, or its first and last chars around "…" in <paramref name="maxChars" />.</summary>
    private static string Elide(string headline, int maxChars)
    {
        if (headline.Length <= maxChars)
        {
            return headline;
        }

        var head = (maxChars - 1) / 2;
        var tailStart = headline.Length - (maxChars - 1 - head);

        // Never split a surrogate pair at either edge.
        if (char.IsHighSurrogate(headline[head - 1]))
        {
            head--;
        }

        if (char.IsLowSurrogate(headline[tailStart]))
        {
            tailStart++;
        }

        return string.Concat(headline.AsSpan(0, head), "…", headline.AsSpan(tailStart));
    }

    /// <summary><c>a,b,c +N more</c>: the first <see cref="MaxCoalescedRunIds" /> distinct ids, then how many others.</summary>
    private static string MergeRunIds(string a, string b)
    {
        var (aIds, aMore) = ParseRunIds(a);
        var (bIds, bMore) = ParseRunIds(b);
        var ids = aIds.Concat(bIds).Distinct(StringComparer.Ordinal).ToList();
        var more = aMore + bMore + Math.Max(0, ids.Count - MaxCoalescedRunIds);
        var named = string.Join(",", ids.Take(MaxCoalescedRunIds));
        return more == 0 ? named : string.Create(CultureInfo.InvariantCulture, $"{named} +{more} more");
    }

    private static (string[] Ids, int More) ParseRunIds(string runIds)
    {
        var more = 0;
        var suffix = runIds.LastIndexOf(" +", StringComparison.Ordinal);
        if (
            suffix >= 0
            && runIds.EndsWith(" more", StringComparison.Ordinal)
            && int.TryParse(
                runIds.AsSpan(suffix + 2, runIds.Length - suffix - 2 - " more".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out more
            )
        )
        {
            runIds = runIds[..suffix];
        }

        return (runIds.Split(','), more);
    }

    private static IEnumerable<ToolCall> ToolCallsOf(IMessage message) =>
        message switch
        {
            ToolCallMessage single => [single],
            ICanGetToolCalls many => many.GetToolCalls() ?? [],
            _ => [],
        };
}
