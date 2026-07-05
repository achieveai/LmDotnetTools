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
    /// <exception cref="ArgumentException">
    /// The args are not a JSON object, supply both <c>delay</c> and <c>deadline</c>, or supply a
    /// <c>delay</c>/<c>deadline</c> that does not parse. Invalid input is rejected rather than
    /// silently falling back to the wait's ceiling, so the caller sees a structured rejection it
    /// can correct instead of an unexpectedly-late fire.
    /// </exception>
    private static DateTimeOffset ResolveFireInstant(TriggerArmRequest request)
    {
        var (delay, deadline) = ParseArgs(request.ArgsJson);
        var hasDelay = !string.IsNullOrWhiteSpace(delay);
        var hasDeadline = !string.IsNullOrWhiteSpace(deadline);

        if (hasDelay && hasDeadline)
        {
            throw new ArgumentException("timer args must supply at most one of 'delay' or 'deadline', not both.");
        }

        if (hasDeadline)
        {
            if (!TriggerDurations.TryResolveInstant(deadline, request.ArmedAt, out var absolute, out var error))
            {
                throw new ArgumentException($"timer 'deadline' is invalid: {error}");
            }
            return absolute;
        }

        if (hasDelay)
        {
            if (!TriggerDurations.TryParseDuration(delay, out var relative))
            {
                throw new ArgumentException(
                    $"timer 'delay' is invalid: '{delay}' is not a duration (e.g. \"10m\", \"30s\").");
            }
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

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(argsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"timer args is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("timer args must be a JSON object.");
            }

            return (GetOptionalString(root, "delay"), GetOptionalString(root, "deadline"));
        }
    }

    private static string? GetOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"timer '{name}' must be a string.");
        }

        return el.GetString();
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
