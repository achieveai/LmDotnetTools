using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>
/// Turns what a run left behind into the report row and the four zero-tolerance invariants (#686 AC 3,
/// AC 4). Independent of the loop's own validator: it re-derives every claim from the persisted rows,
/// the provider's request log and the store call log.
/// </summary>
internal static class CorpusEvaluator
{
    /// <summary>Bump when the metrics or the invariants change meaning; the report carries it.</summary>
    public const string Version = "686.1";

    public static ScenarioModeResult Evaluate(CorpusRunData data)
    {
        var scenario = data.Scenario;
        var loss = new List<string>();
        var rawLoss = new List<string>();
        var notes = new List<string>();

        var activated = ActivatedCheckpoints(data.RootMessages, data.RootState);
        var childActivated = data.ChildThreads.Sum(kv =>
            ActivatedCheckpoints(
                MessagePersistenceConverter.FromPersistedMessagesResilient(kv.Value.Rows),
                kv.Value.State
            ).Count
        );
        var shadow = data.RootState?.History.Count(e => e.Trigger == CompactionTrigger.Shadow) ?? 0;

        // --- protected state (spec §2.6) on every activated checkpoint ---
        var rowsBySeq = data.RootRows.Where(r => r.Seq is not null).ToDictionary(r => r.Seq!.Value);
        var rosterIds = data.Roster.Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
        var boardIds = data.Board is null
            ? null
            : Flatten(data.Board.Tasks).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var instructionVerbatim = true;
        var tasksOnBoard = true;
        var agentsMatch = true;
        var recoveryZero = true;
        var indexContiguous = true;
        var artifactsCarried = true;

        foreach (var cp in activated)
        {
            var manifest = cp.Manifest;
            foreach (var quote in manifest.CurrentInstruction.Concat(manifest.Instructions).Concat(manifest.Decisions))
            {
                if (
                    !rowsBySeq.TryGetValue(quote.Seq, out var row)
                    || MessagePersistenceConverter.FromPersistedMessage(row) is not TextMessage text
                    || !string.Equals(text.Text, quote.Quote, StringComparison.Ordinal)
                )
                {
                    instructionVerbatim = false;
                    loss.Add($"checkpoint {Short(cp)}: quote at seq {quote.Seq} is not the persisted row verbatim");
                }
            }

            // The current instruction is every human row of the checkpoint's own run at or below the
            // boundary (CutSelector R2/R4): a correction injected mid-run is one of them, and a run whose
            // human rows all sit in the tail quotes none.
            var expectedInstruction = data
                .RootRows.Where(r =>
                    r.Seq is not null
                    && r.Seq <= cp.Boundary.Seq
                    && string.Equals(r.RunId, cp.RunId, StringComparison.Ordinal)
                    && string.Equals(r.Role, "User", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.MessageType, "TextMessage", StringComparison.Ordinal)
                )
                .Select(r => r.Seq!.Value)
                .Order()
                .ToList();
            var quotedInstruction = manifest.CurrentInstruction.Select(q => q.Seq).Order().ToList();
            if (!expectedInstruction.SequenceEqual(quotedInstruction))
            {
                instructionVerbatim = false;
                loss.Add(
                    $"checkpoint {Short(cp)}: current instruction quotes seqs [{string.Join(",", quotedInstruction)}], the run's human rows are [{string.Join(",", expectedInstruction)}]"
                );
            }

            if (boardIds is not null)
            {
                var manifestIds = manifest
                    .Tasks.Where(t => t.Id is not null)
                    .Select(t => t.Id!)
                    .ToHashSet(StringComparer.Ordinal);
                if (!manifestIds.SetEquals(boardIds))
                {
                    tasksOnBoard = false;
                    loss.Add(
                        $"checkpoint {Short(cp)}: tasks [{string.Join(",", manifestIds)}] != board [{string.Join(",", boardIds)}]"
                    );
                }
            }

            var manifestAgents = manifest.Agents.Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
            if (scenario.Children.Count > 0 && !manifestAgents.SetEquals(rosterIds))
            {
                agentsMatch = false;
                loss.Add(
                    $"checkpoint {Short(cp)}: agents [{string.Join(",", manifestAgents)}] != roster [{string.Join(",", rosterIds)}]"
                );
            }

            if (
                manifest.Recovery.DeferredToolCalls != 0
                || manifest.Recovery.ParkedWaits != 0
                || manifest.Recovery.OwedContinuations != 0
                || manifest.Recovery.InterruptedTurns != 0
            )
            {
                recoveryZero = false;
                loss.Add($"checkpoint {Short(cp)}: recovery state at the cut is not zero");
            }

            var expectedFrom = 1L;
            foreach (var entry in manifest.Index)
            {
                if (entry.FromSeq != expectedFrom)
                {
                    indexContiguous = false;
                    loss.Add($"checkpoint {Short(cp)}: index gap before seq {entry.FromSeq}");
                }

                expectedFrom = entry.ToSeq + 1;
            }

            if (manifest.Index.Count == 0 || manifest.Index[^1].ToSeq != cp.Boundary.Seq)
            {
                indexContiguous = false;
                loss.Add($"checkpoint {Short(cp)}: index does not reach the boundary {cp.Boundary.Seq}");
            }

            if (
                !manifest.Artifacts.Any(a =>
                    string.Equals(a.Path, CorpusSummarizer.ArtifactPath, StringComparison.Ordinal)
                )
            )
            {
                artifactsCarried = false;
                loss.Add($"checkpoint {Short(cp)}: the summarised artifact was dropped");
            }

            notes.Add(
                $"checkpoint {Short(cp)} ({cp.Trigger}) boundary {cp.Boundary.Seq}, {cp.Stats.RowsCovered} rows, {cp.Stats.EstimatedTokensBefore}->{cp.Stats.EstimatedTokensAfter} tokens, agents [{string.Join(", ", manifest.Agents.Select(a => $"{a.AgentId}:{a.Status}"))}], tasks {manifest.Tasks.Count}"
            );
        }

        // --- everything the summariser returned reached a manifest (the drop-class mutations of §12.3) ---
        // Only Compact activates what it summarises; a checkpoint per summary means none was rejected.
        if (data.Mode == CompactionMode.Compact && data.RootSummaries.Count == activated.Count)
        {
            var carriedQuotes = activated
                .SelectMany(cp => cp.Manifest.Instructions.Concat(cp.Manifest.Decisions))
                .Select(q => (q.Seq, q.Quote))
                .ToHashSet();
            var carriedPaths = activated
                .SelectMany(cp => cp.Manifest.Artifacts.Select(a => a.Path))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var summary in data.RootSummaries)
            {
                foreach (var quote in summary.Instructions.Concat(summary.Decisions))
                {
                    if (!carriedQuotes.Contains((quote.Seq, quote.Quote)))
                    {
                        instructionVerbatim = false;
                        loss.Add($"the summary's quote at seq {quote.Seq} never reached a manifest verbatim");
                    }
                }

                foreach (var artifact in summary.Artifacts)
                {
                    if (!carriedPaths.Contains(artifact.Path))
                    {
                        artifactsCarried = false;
                        loss.Add($"the summary's artifact '{artifact.Path}' never reached a manifest");
                    }
                }
            }
        }

        // --- raw history: no gaps, every covered row still there ---
        rawLoss.AddRange(SequenceGaps("root", data.RootRows));
        foreach (var kv in data.ChildThreads)
        {
            rawLoss.AddRange(SequenceGaps(kv.Key, kv.Value.Rows));
        }

        foreach (var cp in activated)
        {
            for (var seq = 1L; seq <= cp.Boundary.Seq; seq++)
            {
                if (!rowsBySeq.ContainsKey(seq))
                {
                    rawLoss.Add($"checkpoint {Short(cp)} covers seq {seq} which is gone");
                }
            }
        }

        // --- tool pairing in the rows and in every provider request ---
        var pairs = InvalidPairs([.. data.RootMessages.Where(m => m is not CompactionCheckpointMessage)]);
        foreach (var kv in data.ChildThreads)
        {
            pairs += InvalidPairs([
                .. MessagePersistenceConverter
                    .FromPersistedMessagesResilient(kv.Value.Rows)
                    .Where(m => m is not CompactionCheckpointMessage),
            ]);
        }

        foreach (var request in data.Root.Requests.Concat(data.Children.SelectMany(c => c.Requests)))
        {
            pairs += InvalidPairs(ScriptedProvider.Expand(request));
        }

        // A boundary that sits between a call row and its result row would split the pair whatever the
        // mode (Shadow never rewrites the request, so only the boundary itself can be checked there).
        pairs += BoundariesSplittingPairs(data.RootRows, data.RootState, notes);
        foreach (var kv in data.ChildThreads)
        {
            pairs += BoundariesSplittingPairs(kv.Value.Rows, kv.Value.State, notes);
        }

        // --- provider-side numbers ---
        var calls = data.Root.Calls;
        var totalPrompt = calls.Sum(c => c.RequestTokens);
        var totalCached = calls.Sum(c => c.CachedTokens);
        var envelopes = data.Root.Requests.Count(HasEnvelope);
        if (envelopes > 0)
        {
            notes.Add($"{envelopes} of {data.Root.Requests.Count} root requests carried a checkpoint envelope");
        }

        if (data.Root.Calls.Any(c => c.Overflowed))
        {
            notes.Add(
                $"{data.Root.Calls.Count(c => c.Overflowed)} root request(s) exceeded the window at the provider"
            );
        }

        var recall = data
            .RootMessages.OfType<ToolCallResultMessage>()
            .FirstOrDefault(r => r.ToolCallId?.Contains("recall", StringComparison.Ordinal) == true);
        if (recall is not null)
        {
            var firstInstruction = scenario.Steps.First(s => s.Kind == "say").Text!;
            notes.Add(
                recall.Result.Contains(firstInstruction, StringComparison.Ordinal)
                    ? "recall returned the compacted instruction verbatim"
                    : $"recall answered: {Truncate(recall.Result)}"
            );
        }

        var runsAsExpected = data.Runs.All(r => r.IsError == r.ExpectedError);
        var reachedTheEnd = data.Root.CallCount > scenario.Root.Replies.Count;
        var compactionCost = data
            .UsageRecords.Where(r => r.ExecutionKind == UsageExecutionKind.Compaction)
            .Sum(r => r.EstimatedPublicCostMicros ?? 0);

        return new ScenarioModeResult
        {
            ScenarioId = scenario.Id,
            Item = scenario.Item,
            Mode = data.Mode.ToString(),
            Fingerprint = scenario.Fingerprint(),
            TaskSuccess = runsAsExpected && reachedTheEnd,
            ExpectedSuccess = scenario.ExpectedSuccess(data.Mode),
            Runs = data.Runs,
            ProviderCalls = data.Root.CallCount,
            ChildProviderCalls = data.Children.Sum(c => c.CallCount),
            PeakRequestTokens = calls.Count == 0 ? 0 : calls.Max(c => c.RequestTokens),
            MeanRequestTokens = calls.Count == 0 ? 0 : calls.Average(c => c.RequestTokens),
            TotalPromptTokens = totalPrompt,
            TotalCachedTokens = totalCached,
            CacheHitRatio = totalPrompt == 0 ? 0 : (double)totalCached / totalPrompt,
            CheckpointsActivated = activated.Count,
            ChildCheckpointsActivated = childActivated,
            ShadowCheckpoints = shadow,
            Decisions = data
                .Decided.GroupBy(d => d.Decision, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Reasons = data
                .Decided.Where(d => d.Reason is not null)
                .GroupBy(d => d.Reason!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            CostMicros = data.Usage?.EstimatedPublicCostMicros,
            CompactionCostMicros = compactionCost,
            CostCompleteness = (data.Usage?.EstimatedCostCompleteness ?? CostCompleteness.Unavailable).ToString(),
            LatencyMs = data.LatencyMs,
            SummaryLatencyMs = activated.Sum(cp => cp.Stats.SummaryLatencyMs ?? 0),
            Invariants = new InvariantReport
            {
                InvalidToolPairs = pairs,
                ProtectedStateLoss = loss,
                CrossThreadReads = data.CrossThread.Count,
                RawHistoryLoss = rawLoss,
            },
            Retention = new ProtectedStateRetention
            {
                CheckpointsChecked = activated.Count,
                CurrentInstructionVerbatim = instructionVerbatim,
                TasksOnBoard = tasksOnBoard,
                AgentsMatchRoster = agentsMatch,
                RecoveryZero = recoveryZero,
                IndexContiguous = indexContiguous,
                ArtifactsCarried = artifactsCarried,
                MetadataKeys = [.. (data.Metadata?.Properties?.Keys ?? []).Order(StringComparer.Ordinal)],
                RunLedgerEntries = data.Ledger.Count,
                UsageRecords = data.UsageRecords.Count,
            },
            Notes = notes,
        };
    }

    /// <summary>Checkpoint rows the state projection shows as activated (Active now, or since superseded).</summary>
    public static IReadOnlyList<CompactionCheckpointMessage> ActivatedCheckpoints(
        IReadOnlyList<IMessage> messages,
        CompactionState? state
    )
    {
        var activatedIds = (state?.History ?? [])
            .Where(e => e.Status is CheckpointStatus.Active or CheckpointStatus.Superseded)
            .Select(e => e.CheckpointId)
            .ToHashSet(StringComparer.Ordinal);
        return [.. messages.OfType<CompactionCheckpointMessage>().Where(cp => activatedIds.Contains(cp.CheckpointId))];
    }

    public static bool HasEnvelope(IReadOnlyList<IMessage> request) =>
        request.Any(m =>
            m is TextMessage { Role: Role.User } t
            && t.Text.Contains(RecallConversationToolProvider.ToolName, StringComparison.Ordinal)
        );

    /// <summary>
    /// Boundaries in the checkpoint history (every status but Rejected, Shadow included) that fall between
    /// a tool call row and its result row.
    /// </summary>
    public static int BoundariesSplittingPairs(
        IReadOnlyList<PersistedMessage> rows,
        CompactionState? state,
        List<string> notes
    )
    {
        if (state is null || state.History.Count == 0)
        {
            return 0;
        }

        var callSeq = new Dictionary<string, long>(StringComparer.Ordinal);
        var resultSeq = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Seq is not null))
        {
            IMessage message;
            try
            {
                message = MessagePersistenceConverter.FromPersistedMessage(row);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                continue;
            }

            switch (message)
            {
                case ToolCallMessage { ToolCallId: { } id }:
                    callSeq[id] = row.Seq!.Value;
                    break;
                case ToolsCallMessage multi:
                    foreach (var id in multi.ToolCalls.Select(c => c.ToolCallId).OfType<string>())
                    {
                        callSeq[id] = row.Seq!.Value;
                    }

                    break;
                case ToolCallResultMessage { ToolCallId: { } id }:
                    resultSeq[id] = row.Seq!.Value;
                    break;
                case ToolsCallResultMessage multi:
                    foreach (var id in multi.ToolCallResults.Select(r => r.ToolCallId).OfType<string>())
                    {
                        resultSeq[id] = row.Seq!.Value;
                    }

                    break;
                default:
                    break;
            }
        }

        var split = 0;
        foreach (var entry in state.History.Where(e => e.Status != CheckpointStatus.Rejected))
        {
            foreach (var (id, call) in callSeq)
            {
                var result = resultSeq.TryGetValue(id, out var r) ? r : long.MaxValue;
                if (call <= entry.BoundarySeq && entry.BoundarySeq < result)
                {
                    split++;
                    notes.Add(
                        $"checkpoint {entry.CheckpointId[..Math.Min(8, entry.CheckpointId.Length)]} boundary {entry.BoundarySeq} splits tool call {id} (call {call}, result {(result == long.MaxValue ? "none" : result)})"
                    );
                }
            }
        }

        return split;
    }

    /// <summary>Calls without a result, results without a call, in one list of messages.</summary>
    public static int InvalidPairs(IReadOnlyList<IMessage> messages)
    {
        var calls = new List<string>();
        var results = new List<string>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case ToolCallMessage call when call.ToolCallId is not null:
                    calls.Add(call.ToolCallId);
                    break;
                case ToolsCallMessage multi:
                    calls.AddRange(multi.ToolCalls.Select(c => c.ToolCallId).OfType<string>());
                    break;
                case ToolCallResultMessage result when result.ToolCallId is not null:
                    results.Add(result.ToolCallId);
                    break;
                case ToolsCallResultMessage multi:
                    results.AddRange(multi.ToolCallResults.Select(r => r.ToolCallId).OfType<string>());
                    break;
                default:
                    break;
            }
        }

        var callSet = calls.ToHashSet(StringComparer.Ordinal);
        var resultSet = results.ToHashSet(StringComparer.Ordinal);
        return callSet.Count(id => !resultSet.Contains(id)) + resultSet.Count(id => !callSet.Contains(id));
    }

    private static IEnumerable<string> SequenceGaps(string thread, IReadOnlyList<PersistedMessage> rows)
    {
        var seqs = rows.Where(r => r.Seq is not null).Select(r => r.Seq!.Value).Order().ToList();
        if (rows.Count > 0 && seqs.Count != rows.Count)
        {
            yield return $"{thread}: {rows.Count - seqs.Count} row(s) without a sequence number";
        }

        for (var i = 0; i < seqs.Count; i++)
        {
            if (seqs[i] != i + 1)
            {
                yield return $"{thread}: sequence gap at {i + 1} (found {seqs[i]})";
                yield break;
            }
        }
    }

    private static IEnumerable<TodoTaskNode> Flatten(IReadOnlyList<TodoTaskNode> nodes) =>
        nodes.Where(n => n.Status != TodoTaskStatus.Removed).SelectMany(n => new[] { n }.Concat(Flatten(n.SubTasks)));

    private static string Short(CompactionCheckpointMessage cp) =>
        cp.CheckpointId.Length > 8 ? cp.CheckpointId[..8] : cp.CheckpointId;

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
