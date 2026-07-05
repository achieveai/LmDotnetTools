using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.Sources;

/// <summary>
/// Built-in one-shot timer source. Fires once when its configured instant is reached, then never
/// again. The awaited instant comes from <c>args.deadline</c> (absolute) or <c>args.delay</c>
/// (relative to when the wait was armed); if neither is supplied it defaults to the wait's own
/// ceiling deadline (i.e. <c>Wait({kind:"timer", timeout:"10m"})</c> means "wake me in 10m").
/// </summary>
/// <remarks>
/// Restartable: the fire instant is a pure function of the persisted arm time and args, so a
/// restored wait re-arms for its remaining delay (or fires immediately if already elapsed). This
/// source holds no per-wait state — each <see cref="ArmAsync"/> returns an independent handle.
/// </remarks>
public sealed class TimerTriggerSource : ITriggerSource
{
    /// <summary>The registered kind token.</summary>
    public const string KindName = "timer";

    /// <summary>Capabilities: block + restore. Notify is a future follow-up.</summary>
    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: false, SupportsRestore: true);

    /// <summary>Human-readable args hint for the tool contract.</summary>
    public const string ArgsSchemaText =
        "{ delay?: \"<duration e.g. 10m|30s|2h>\", deadline?: \"<ISO-8601 time>\" } "
        + "— supply at most one; if omitted, fires at the wait's timeout.";

    /// <inheritdoc />
    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request,
        ITriggerEventSink eventSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var fireAt = ResolveFireInstant(request);
        var handle = new TimerArmedTrigger(request.WaitId, fireAt, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    /// <summary>
    /// Computes the absolute instant the timer should fire, relative to the arm time so restart
    /// re-arming yields the same wall-clock target.
    /// </summary>
    private static DateTimeOffset ResolveFireInstant(TriggerArmRequest request)
    {
        var (delay, deadline) = ParseArgs(request.ArgsJson);

        if (!string.IsNullOrWhiteSpace(deadline)
            && TriggerDurations.TryResolveInstant(deadline, request.ArmedAt, out var absolute, out _))
        {
            return absolute;
        }

        if (!string.IsNullOrWhiteSpace(delay)
            && TriggerDurations.TryParseDuration(delay, out var relative))
        {
            return request.ArmedAt + relative;
        }

        // No explicit fire time — fire at the wait's ceiling ("sleep until timeout").
        return request.Deadline;
    }

    private static (string? Delay, string? Deadline) ParseArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var delay = root.TryGetProperty("delay", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            var deadline = root.TryGetProperty("deadline", out var dl) && dl.ValueKind == JsonValueKind.String
                ? dl.GetString()
                : null;
            return (delay, deadline);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Per-arm handle. Runs a single <see cref="Task.Delay(TimeSpan, CancellationToken)"/> for the
    /// remaining delay and reports one fire. Disposal cancels the delay so no fire occurs afterward.
    /// </summary>
    private sealed class TimerArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _timerTask;
        private int _disposed;

        public TimerArmedTrigger(string waitId, DateTimeOffset fireAt, ITriggerEventSink sink)
        {
            WaitId = waitId;
            _timerTask = RunAsync(fireAt, sink, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(DateTimeOffset fireAt, ITriggerEventSink sink, CancellationToken ct)
        {
            // Yield first so the fire is always asynchronous — never synchronous within ArmAsync,
            // even when the instant has already elapsed (Task.Delay(0) would otherwise continue
            // synchronously and fire before the runtime has finished registering the wait).
            await Task.Yield();

            var remaining = fireAt - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            try
            {
                await Task.Delay(remaining, ct);
            }
            catch (OperationCanceledException)
            {
                return; // disposed/cancelled before firing — no fire.
            }

            await sink.FireAsync(new TriggerFireEvent(), ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync();

            // Do NOT await _timerTask here: disposal is normally invoked by the runtime from
            // within the fire callback (fire → runtime finalize → source dispose), and awaiting
            // our own still-running task would deadlock. Dispose the CTS once the task settles,
            // off the current stack.
            _ = _timerTask.ContinueWith(
                _ =>
                {
                    try
                    {
                        _cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed — nothing to do.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
