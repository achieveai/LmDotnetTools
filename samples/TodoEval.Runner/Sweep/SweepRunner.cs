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
            var (status, steerAt) = await PollWithOptionalSteerAsync(threadId, inputId, task, started, deadline, ct);
            steerSentAt = steerAt;

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
        J1Result? j1
    ) =>
        entry with
        {
            WorkspacePath = workspacePath,
            SteerSentAt = steerSentAt,
            J1 = j1,
            MinCompactions = task.Meta?.MinCompactions,
            VariantCompacts = variant.Compacts,
        };

    /// <summary>
    /// Polls the task message to a terminal status, sending the task's <c>## steer</c> correction as a
    /// second message once <c>steerAfterSeconds</c> has passed and the run is still going.
    /// </summary>
    /// <remarks>
    /// The wait for the correction IS a poll to a shorter deadline: reaching a terminal status first
    /// means the run answered before the user changed their mind, so there is nothing to correct and
    /// no steer is sent. Once it IS sent, the run's completion is the SECOND input's — the first input
    /// has its own terminal status that says nothing about whether the correction was honoured.
    /// </remarks>
    private async Task<(RunStatus Status, DateTimeOffset? SteerSentAt)> PollWithOptionalSteerAsync(
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
            return (await client.PollToTerminalAsync(threadId, inputId, deadline, config.Poll, ct), null);
        }

        // Poll to whichever comes first. Reaching a terminal status means the run answered before the
        // correction was due, so there is nothing to correct. Running out of time at the RUN's own
        // deadline is an ordinary timeout and propagates — a correction sent then could never land.
        var steerDue = started + TimeSpan.FromSeconds(task.Meta?.SteerAfterSeconds ?? 0);
        var firstDeadline = steerDue < deadline ? steerDue : deadline;
        try
        {
            return (await client.PollToTerminalAsync(threadId, inputId, firstDeadline, config.Poll, ct), null);
        }
        catch (TimeoutException) when (firstDeadline < deadline)
        {
            // Still working, which is the point: the correction has to land mid-run.
        }

        var steerInputId = await client.SendMessageAsync(threadId, steer, ct);
        var sentAt = DateTimeOffset.UtcNow;
        log.WriteLine($"[run] steer sent on thread {threadId} at {sentAt:O}");
        return (await client.PollToTerminalAsync(threadId, steerInputId, deadline, config.Poll, ct), sentAt);
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
