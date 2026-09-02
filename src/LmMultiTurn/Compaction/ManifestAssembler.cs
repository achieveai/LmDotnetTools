using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;

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
    private const string UnknownRunId = "unknown";

    public static ContextManifest Assemble(
        IReadOnlyList<SequencedMessage> rows,
        CutDecision.Cut cut,
        ContextManifest? previous,
        long previousBoundary,
        CheckpointSummary summary,
        TodoBoardSnapshot? board,
        IReadOnlyList<AgentRef> roster,
        ManifestAssemblerOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(roster);
        options ??= new ManifestAssemblerOptions();

        var covered = rows.Where(r => r.Seq > previousBoundary && r.Seq <= cut.Seq).ToList();

        return new ContextManifest
        {
            CurrentInstruction =
            [
                .. cut.CurrentInstruction.Select(r => new QuotedItem { Seq = r.Seq, Quote = r.Text ?? string.Empty }),
            ],
            Instructions = MergeQuotes(previous?.Instructions, summary.Instructions),
            Goals = [.. (previous?.Goals ?? []).Concat(summary.Goals).Distinct(StringComparer.Ordinal)],
            Decisions = MergeQuotes(previous?.Decisions, summary.Decisions),
            Tasks = board is null ? [.. summary.Tasks.Select(t => t with { Id = null })] : Flatten(board.Tasks),
            Artifacts = MergeArtifacts(previous?.Artifacts, ArtifactsFromRows(covered, options), summary.Artifacts),
            Agents = [.. roster.Select(agent => agent with { Outcome = OutcomeFor(agent, covered, summary) })],
            Index = BuildIndex(previous?.Index, previousBoundary, covered, cut.Seq, summary.Headlines, options),
            Recovery = cut.Recovery,
        };
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
            IEnumerable<ToolCall> calls = row.Message switch
            {
                ToolCallMessage single => [single],
                ICanGetToolCalls many => many.GetToolCalls() ?? [],
                _ => [],
            };

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
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(args);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (
                    document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } path
                )
                {
                    return path;
                }
            }
        }
        catch (JsonException)
        {
            // A call whose arguments never parsed names nothing.
        }

        return null;
    }

    private static string? OutcomeFor(AgentRef agent, List<SequencedMessage> covered, CheckpointSummary summary)
    {
        var fromRows = covered
            .Select(r => r.Message)
            .OfType<NotifyMessage>()
            .Where(n =>
                string.Equals(n.NotifyKind, NotifyKinds.SubAgentCompletion, StringComparison.Ordinal)
                && (Mentions(n.Label, agent.AgentId) || Mentions(n.Detail, agent.AgentId))
            )
            .Select(n => n.Detail ?? n.Label)
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return fromRows
            ?? (summary.AgentOutcomes.TryGetValue(agent.AgentId, out var fromModel) ? fromModel : agent.Outcome);
    }

    private static bool Mentions(string? text, string agentId) =>
        text is not null && text.Contains(agentId, StringComparison.Ordinal);

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

        while (entries.Count > options.MaxIndexEntries && entries.Count >= 2)
        {
            var (a, b) = (entries[0], entries[1]);
            entries.RemoveRange(0, 2);
            entries.Insert(
                0,
                new IndexEntry
                {
                    FromSeq = a.FromSeq,
                    ToSeq = b.ToSeq,
                    RunId = string.Equals(a.RunId, b.RunId, StringComparison.Ordinal)
                        ? a.RunId
                        : $"{a.RunId},{b.RunId}",
                    Headline = $"{a.Headline}; {b.Headline}",
                }
            );
        }

        return entries;
    }
}
