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
    TextWriter log
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

        log.WriteLine($"[run {runKey}] starting (topic: {topic})");
        try
        {
            threadId = await client.ProvisionConversationAsync(workspaceId, model, modeId, ct);
            var taskText = TaskTemplateRenderer.Render(task.Template, topic);
            inputId = await client.SendMessageAsync(threadId, taskText, ct);

            var deadline = started + TimeSpan.FromMinutes(config.PerRunTimeoutMinutes);
            var status = await client.PollToTerminalAsync(threadId, inputId, deadline, config.Poll, ct);

            var ended = DateTimeOffset.UtcNow;
            log.WriteLine(
                $"[run {runKey}] {status.Status} after {(ended - started).TotalSeconds:0}s (thread {threadId})"
            );
            return new RunManifestEntry
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
            };
        }
        catch (TimeoutException ex)
        {
            return Failed(RunOutcomes.TimedOut, ex.Message);
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
            return Failed(RunOutcomes.HarnessError, ex.Message);
        }

        RunManifestEntry Failed(string status, string error)
        {
            var ended = DateTimeOffset.UtcNow;
            log.WriteLine($"[run {runKey}] {status}: {error}");
            return new RunManifestEntry
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
            };
        }
    }
}
