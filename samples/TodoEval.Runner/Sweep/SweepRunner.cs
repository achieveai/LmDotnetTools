namespace TodoEval.Runner.Sweep;

/// <summary>
/// Drives the N-seeds x M-models sweep against an already-ready isolated host: one conversation per
/// (model, seed), completion gated on the host's run-state machinery (status-by-input polled to a
/// terminal status — never a UI/idle heuristic), a hard per-run wall-clock timeout, and sequential
/// execution by default with opt-in bounded parallelism. Each finished run is appended to the
/// manifest immediately so a crashed sweep still leaves a usable partial record.
/// </summary>
internal sealed class SweepRunner(
    EvalHostClient client,
    EvalRunnerConfig config,
    string workspaceId,
    string modeId,
    string taskTemplate,
    TextWriter log
)
{
    public async Task<IReadOnlyList<RunManifestEntry>> RunSweepAsync(string manifestPath, CancellationToken ct)
    {
        var specs = new List<(string Model, int SeedIndex)>();
        foreach (var model in config.Models)
        {
            for (var seed = 0; seed < config.Seeds; seed++)
            {
                specs.Add((model, seed));
            }
        }

        var entries = new List<RunManifestEntry>(specs.Count);
        var manifestLock = new object();
        using var throttle = new SemaphoreSlim(config.MaxParallelRuns);

        var tasks = specs.Select(async spec =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var entry = await RunOneAsync(spec.Model, spec.SeedIndex, ct);
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

        _ = await Task.WhenAll(tasks);
        return [.. entries.OrderBy(e => e.Model, StringComparer.Ordinal).ThenBy(e => e.SeedIndex)];
    }

    private async Task<RunManifestEntry> RunOneAsync(string model, int seedIndex, CancellationToken ct)
    {
        var topic = config.TopicForSeed(seedIndex);
        var runKey = $"{model}/seed{seedIndex}";
        var started = DateTimeOffset.UtcNow;
        string? threadId = null;
        string? inputId = null;

        log.WriteLine($"[run {runKey}] starting (topic: {topic})");
        try
        {
            threadId = await client.ProvisionConversationAsync(workspaceId, model, modeId, ct);
            var taskText = TaskTemplateRenderer.Render(taskTemplate, topic);
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
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
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
