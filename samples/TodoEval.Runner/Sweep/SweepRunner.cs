using System.Text.Json;

namespace TodoEval.Runner.Sweep;

/// <summary>
/// Drives the tasks x models x seeds sweep for ONE variant against an already-ready isolated host:
/// one conversation per cell, completion gated on the host's run-state machinery (status-by-input
/// polled to a terminal status — never a UI/idle heuristic), a hard per-run wall-clock timeout, and
/// sequential execution by default with opt-in bounded parallelism. Each finished run is appended to
/// the manifest immediately so a crashed sweep still leaves a usable partial record.
/// </summary>
/// <remarks>
/// The variant axis is deliberately NOT here: every compaction knob is bound onto a host-level DI
/// singleton, so varying one means launching another host. <c>EvalProgram</c> owns that loop and
/// hands each host its own runner.
/// </remarks>
internal sealed class SweepRunner(
    EvalHostClient client,
    EvalRunnerConfig config,
    string workspaceId,
    string modeId,
    VariantConfig variant,
    IReadOnlyList<EvalTaskAsset> tasks,
    TextWriter log,
    ITaskChecker? checker = null,
    string? scoresDir = null
)
{
    public async Task<IReadOnlyList<RunManifestEntry>> RunSweepAsync(string manifestPath, CancellationToken ct)
    {
        var specs = new List<(EvalTaskAsset Task, string Model, int SeedIndex)>();
        foreach (var task in tasks)
        {
            foreach (var model in config.Models)
            {
                for (var seed = 0; seed < config.Seeds; seed++)
                {
                    specs.Add((task, model, seed));
                }
            }
        }

        var entries = new List<RunManifestEntry>(specs.Count);
        var manifestLock = new object();
        using var throttle = new SemaphoreSlim(config.MaxParallelRuns);

        var running = specs.Select(async spec =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var entry = await RunOneAsync(spec.Task, spec.Model, spec.SeedIndex, ct);
                lock (manifestLock)
                {
                    entries.Add(entry);
                    File.AppendAllLines(manifestPath, [entry.ToJsonLine()]);
                }

                return entry;
            }
            finally
            {
                _ = throttle.Release();
            }
        });

        _ = await Task.WhenAll(running);
        return
        [
            .. entries
                .OrderBy(e => e.Task, StringComparer.Ordinal)
                .ThenBy(e => e.Model, StringComparer.Ordinal)
                .ThenBy(e => e.SeedIndex),
        ];
    }

    private async Task<RunManifestEntry> RunOneAsync(
        EvalTaskAsset task,
        string model,
        int seedIndex,
        CancellationToken ct
    )
    {
        var topic = config.TopicForSeed(seedIndex);
        var runKey = RunManifestEntry.MakeRunKey(variant, task.Id, model, seedIndex);
        var started = DateTimeOffset.UtcNow;
        string? threadId = null;
        string? inputId = null;
        string? workspacePath = null;
        DateTimeOffset? steerSentAt = null;
        bool? steerMidRun = null;

        log.WriteLine($"[run {runKey}] starting (topic: {topic})");
        try
        {
            // A task that ships fixtures gets its OWN workspace directory, freshly filled, and the
            // conversation is bound to it. A task without fixtures keeps the sweep's shared workspace,
            // which is exactly what the single-task todo-eval layout has always done.
            var runWorkspaceId = workspaceId;
            if (task.FixturesDir is { } fixtures)
            {
                workspacePath = RunWorkspace.Prepare(config.WorkspacesRoot, runKey, fixtures);
                runWorkspaceId = await client.EnsureWorkspaceAsync(
                    runKey,
                    ct,
                    Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar))
                );
                log.WriteLine($"[run {runKey}] workspace {workspacePath}");
            }

            threadId = await client.ProvisionConversationAsync(runWorkspaceId, model, modeId, ct);
            var taskText = TaskTemplateRenderer.Render(task.Template, topic, task.Meta?.SeedForIndex(seedIndex));
            inputId = await client.SendMessageAsync(threadId, taskText, ct);

            var deadline = started + TimeSpan.FromMinutes(task.Meta?.TimeoutMinutes ?? config.PerRunTimeoutMinutes);
            var (status, steerAt, midRun) = await PollWithOptionalSteerAsync(
                threadId,
                inputId,
                task,
                started,
                deadline,
                ct
            );
            steerSentAt = steerAt;
            steerMidRun = midRun;

            var ended = DateTimeOffset.UtcNow;
            // A non-Completed terminal status is the HOST's verdict and is kept as-is; the host's own
            // reason for it (when the status payload names one) rides along so the manifest row —
            // and the console line — say why, exactly as the harness-error path does.
            log.WriteLine(
                $"[run {runKey}] {status.Status} after {(ended - started).TotalSeconds:0}s (thread {threadId})"
                    + (status.Error is { } hostError ? $": {hostError}" : "")
            );
            return Stamp(
                new RunManifestEntry
                {
                    RunKey = runKey,
                    Model = model,
                    SeedIndex = seedIndex,
                    Topic = topic,
                    Variant = variant.Name,
                    Task = task.Id,
                    Status = status.Status,
                    ThreadId = threadId,
                    InputId = inputId,
                    RunId = status.RunId,
                    StartedUtc = started,
                    EndedUtc = ended,
                    DurationMs = (long)(ended - started).TotalMilliseconds,
                    Error = status.Error,
                },
                task,
                workspacePath,
                steerSentAt,
                steerMidRun,
                await JudgeAsync(task, runKey, workspacePath, ct)
            );
        }
        catch (TimeoutException ex)
        {
            return await FailedAsync(RunOutcomes.TimedOut, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // F-008: JsonException/KeyNotFoundException from parsing a malformed 200 body must become
        // THIS run's HarnessError row — escaping here would fault Task.WhenAll and skip the archive
        // and extraction of every run that DID finish.
        catch (Exception ex)
            when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return await FailedAsync(RunOutcomes.HarnessError, ex.Message);
        }

        async Task<RunManifestEntry> FailedAsync(string status, string error)
        {
            var ended = DateTimeOffset.UtcNow;
            log.WriteLine($"[run {runKey}] {status}: {error}");

            // A timed-out run still left a workspace behind, and what it managed to build there is
            // worth judging even though J0 will mark the run invalid. Only the score is discarded
            // by the aggregate, never the evidence.
            return Stamp(
                new RunManifestEntry
                {
                    RunKey = runKey,
                    Model = model,
                    SeedIndex = seedIndex,
                    Topic = topic,
                    Variant = variant.Name,
                    Task = task.Id,
                    Status = status,
                    ThreadId = threadId,
                    InputId = inputId,
                    StartedUtc = started,
                    EndedUtc = ended,
                    DurationMs = (long)(ended - started).TotalMilliseconds,
                    Error = error,
                },
                task,
                workspacePath,
                steerSentAt,
                steerMidRun,
                await JudgeAsync(task, runKey, workspacePath, ct)
            );
        }
    }

    /// <summary>
    /// The run's own facts plus everything the judging layers need to read it back offline: where it
    /// worked, whether the correction was sent, its J1 verdict, and the J0 compaction floor that
    /// applies to it.
    /// </summary>
    private RunManifestEntry Stamp(
        RunManifestEntry entry,
        EvalTaskAsset task,
        string? workspacePath,
        DateTimeOffset? steerSentAt,
        bool? steerMidRun,
        J1Result? j1
    ) =>
        entry with
        {
            WorkspacePath = workspacePath,
            SteerSentAt = steerSentAt,
            SteerMidRun = steerMidRun,
            J1 = j1,
            MinCompactions = task.Meta?.MinCompactions,
            VariantCompacts = variant.Compacts,
        };

    /// <summary>
    /// Polls the task message to a terminal status, sending the task's <c>## steer</c> correction as a
    /// second message: mid-run once <c>steerAfterSeconds</c> has passed and the run is still going,
    /// otherwise right after the first answer, as the next turn.
    /// </summary>
    /// <remarks>
    /// The wait for the correction IS a poll to a shorter deadline. Reaching a terminal status first
    /// means the model answered before the user changed their mind; the correction is still owed (a
    /// fast model must not skip the task's second half), it just lands as a follow-up turn, and the
    /// row says so. Either way the run's completion is the SECOND input's — the first input has its
    /// own terminal status that says nothing about whether the correction was honoured. A first
    /// answer that is not Completed (errored, interrupted) is returned as-is: nothing can be steered.
    /// </remarks>
    private async Task<(RunStatus Status, DateTimeOffset? SteerSentAt, bool? SteerMidRun)> PollWithOptionalSteerAsync(
        string threadId,
        string inputId,
        EvalTaskAsset task,
        DateTimeOffset started,
        DateTimeOffset deadline,
        CancellationToken ct
    )
    {
        if (task.Steer is not { } steer)
        {
            return (await client.PollToTerminalAsync(threadId, inputId, deadline, config.Poll, ct), null, null);
        }

        var released = task.Meta?.SteerAfter is { } trigger
            ? await WaitForSteerTriggerAsync(threadId, inputId, trigger, deadline, ct)
            : await WaitForSteerClockAsync(threadId, inputId, task, started, deadline, ct);
        if (released.First is { } early)
        {
            return (early, null, null);
        }

        var midRun = released.MidRun;
        var steerInputId = await client.SendMessageAsync(threadId, steer, ct);
        var sentAt = DateTimeOffset.UtcNow;
        log.WriteLine(
            $"[run] steer sent on thread {threadId} at {sentAt:O} ({(midRun ? "mid-run" : "after the first answer")})"
        );
        return (await client.PollToTerminalAsync(threadId, steerInputId, deadline, config.Poll, ct), sentAt, midRun);
    }

    /// <summary>
    ///     A status that means the first answer is over. Deliberately NOT "anything but Running": a host
    ///     reports queued and starting states too, and treating one of those as an ending would retire
    ///     the count trigger before the agent had made a single call.
    /// </summary>
    private static bool IsTerminal(string status) =>
        string.Equals(status, RunOutcomes.Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, RunOutcomes.Errored, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, RunOutcomes.Interrupted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The legacy release: whichever comes first, the clock reaching <c>steerAfterSeconds</c> or the
    ///     first answer going terminal. Kept so every task written before <c>steerAfter</c> existed, and
    ///     every sweep already recorded, still means what it meant.
    /// </summary>
    private async Task<(RunStatus? First, bool MidRun)> WaitForSteerClockAsync(
        string threadId,
        string inputId,
        EvalTaskAsset task,
        DateTimeOffset started,
        DateTimeOffset deadline,
        CancellationToken ct
    )
    {
        // Running out of time at the RUN's own deadline is an ordinary timeout and propagates — a
        // correction sent then could never land.
        var steerDue = started + TimeSpan.FromSeconds(task.Meta?.SteerAfterSeconds ?? 0);
        var firstDeadline = steerDue < deadline ? steerDue : deadline;
        try
        {
            var first = await client.PollToTerminalAsync(threadId, inputId, firstDeadline, config.Poll, ct);
            return first.Status == RunOutcomes.Completed ? (null, false) : (first, false);
        }
        catch (TimeoutException) when (firstDeadline < deadline)
        {
            // Still working, which is what the steer family exists to measure: the correction lands mid-run.
            return (null, true);
        }
    }

    /// <summary>
    ///     The event release: hold the correction until the conversation itself says the moment has come,
    ///     so the correction lands at the same point in the WORK however long the work took.
    /// </summary>
    /// <remarks>
    ///     The first answer is always the backstop. For <c>firstAnswer</c> it is the whole trigger, which
    ///     is what a two-wave task needs — its steer is a second wave and a second wave cannot precede the
    ///     first. For <c>toolCalls</c> the count usually wins, and a run that finishes without ever
    ///     reaching it gets the correction as a follow-up turn exactly as before.
    /// </remarks>
    private async Task<(RunStatus? First, bool MidRun)> WaitForSteerTriggerAsync(
        string threadId,
        string inputId,
        SteerTrigger trigger,
        DateTimeOffset deadline,
        CancellationToken ct
    )
    {
        if (trigger.Kind == SteerTriggerKinds.FirstAnswer)
        {
            var answered = await client.PollToTerminalAsync(threadId, inputId, deadline, config.Poll, ct);
            return answered.Status == RunOutcomes.Completed ? (null, false) : (answered, false);
        }

        var tool = trigger.Tool!;
        var wanted = trigger.Count!.Value;
        var interval = config.Poll.InitialInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Run on thread {threadId} neither reached {wanted} {tool} calls nor finished before {deadline:O}."
                );
            }

            if (await client.CountToolCallsAsync(threadId, tool, ct) >= wanted)
            {
                log.WriteLine($"[run] steer released on thread {threadId}: {wanted} {tool} calls reached");
                return (null, true);
            }

            // The count usually wins. If the run reaches an end first, hand the verdict to the hardened
            // poll rather than reading it here: a bare Interrupted right after a send is routinely
            // synthesized, and only PollToTerminalAsync knows to re-poll it through the grace window.
            var status = await client.GetStatusByInputIdAsync(threadId, inputId, ct);
            if (IsTerminal(status.Status))
            {
                var settled = await client.PollToTerminalAsync(threadId, inputId, deadline, config.Poll, ct);
                return settled.Status == RunOutcomes.Completed ? (null, false) : (settled, false);
            }

            await Task.Delay(interval, ct);
            var next = interval + interval;
            interval = next > config.Poll.MaxInterval ? config.Poll.MaxInterval : next;
        }
    }

    /// <summary>
    /// The J1 verdict for a finished run, or null when there is nothing to judge — no checker, or no
    /// workspace of its own for a checker to look at.
    /// </summary>
    private async Task<J1Result?> JudgeAsync(
        EvalTaskAsset task,
        string runKey,
        string? workspacePath,
        CancellationToken ct
    )
    {
        if (checker is null || scoresDir is null || workspacePath is null)
        {
            return null;
        }

        var result = await checker.JudgeAsync(
            task,
            workspacePath,
            Path.Combine(scoresDir, RunWorkspace.LeafFor(runKey)),
            ct
        );
        if (result is not null)
        {
            log.WriteLine(
                $"[run {runKey}] j1 {result.Outcome}"
                    + (result.Score is { } score ? $" ({score:0.####})" : "")
                    + (result.Error is { } error ? $": {error}" : "")
            );
        }

        return result;
    }
}
