using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using Cronos;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Notify/block source firing on a cron expression or a fixed interval. Restorable: the next-fire
/// instant is a pure function of the cron/interval plus the arm time, so a restored wait re-arms
/// deterministically (same argument <c>TimerTriggerSource</c> relies on). Block mode resolves on
/// the first fire only; notify mode repeats via the runtime's multi-fire lifecycle.
/// </summary>
public sealed class ScheduleTriggerSource : ITriggerSource
{
    /// <summary>The registered kind token.</summary>
    public const string KindName = "schedule";

    /// <summary>Human-readable args hint for the tool contract.</summary>
    public const string ArgsSchemaText =
        "{ cron?: \"<cron expr, 5 or 6 fields>\", intervalSeconds?: <int >= 1> } — supply exactly one.";

    /// <summary>Capabilities: block + notify + restore.</summary>
    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: true);

    private const int MinIntervalSeconds = 1;

    /// <inheritdoc />
    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request,
        ITriggerEventSink eventSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var (cron, intervalSeconds) = ParseArgs(request.ArgsJson);
        var hasCron = !string.IsNullOrWhiteSpace(cron);
        var hasInterval = intervalSeconds.HasValue;

        if (hasCron == hasInterval)
        {
            throw new ArgumentException("schedule requires exactly one of 'cron' or 'intervalSeconds'.");
        }

        CronExpression? expr = null;
        if (hasCron)
        {
            var fields = cron!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
            try
            {
                expr = CronExpression.Parse(cron, format);
            }
            catch (CronFormatException ex)
            {
                throw new ArgumentException($"schedule 'cron' is invalid: {ex.Message}", ex);
            }
        }
        else if (intervalSeconds!.Value < MinIntervalSeconds)
        {
            throw new ArgumentException($"schedule 'intervalSeconds' must be >= {MinIntervalSeconds}.");
        }

        var handle = new ScheduleArmedTrigger(request.WaitId, expr, intervalSeconds, request.ArmedAt, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    private static (string? Cron, int? IntervalSeconds) ParseArgs(string argsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"schedule args is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("schedule args must be a JSON object.");
            }

            var cron = root.TryGetProperty("cron", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            int? interval = root.TryGetProperty("intervalSeconds", out var i) && i.ValueKind == JsonValueKind.Number
                ? i.GetInt32()
                : null;
            return (cron, interval);
        }
    }

    /// <summary>
    /// Per-arm handle. Loops re-computing the next occurrence (cron) or the next fixed offset
    /// (interval) from the last fire instant, sleeping until it elapses, and reporting a fire —
    /// repeating until disposed. Disposal cancels the wait so no further fire occurs afterward.
    /// </summary>
    private sealed class ScheduleArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _disposed;

        public ScheduleArmedTrigger(
            string waitId,
            CronExpression? expr,
            int? intervalSeconds,
            DateTimeOffset armedAt,
            ITriggerEventSink sink)
        {
            WaitId = waitId;
            _loop = RunAsync(expr, intervalSeconds, armedAt, sink, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(
            CronExpression? expr,
            int? intervalSeconds,
            DateTimeOffset armedAt,
            ITriggerEventSink sink,
            CancellationToken ct)
        {
            // Yield first so the fire is always asynchronous — never synchronous within ArmAsync.
            await Task.Yield();

            // Restore safety: a restored notify wait is re-armed with its ORIGINAL persisted arm
            // time, which after any outage longer than one period is in the past. Skip forward to
            // the next occurrence AT/AFTER now rather than replaying every occurrence missed during
            // the outage — a naive walk from `armedAt` would fire hundreds of stale events
            // back-to-back (delay <= 0 → no Task.Delay), flooding the notify channel + persistence.
            // The next-fire instant stays a pure function of (schedule, armedAt, now); a live arm
            // (armedAt ≈ now) advances nothing and behaves exactly as before.
            var now = DateTimeOffset.UtcNow;
            var last = armedAt;
            if (last < now)
            {
                if (expr is null)
                {
                    // Interval: jump to the most recent occurrence at/before now, so the first
                    // `next = last + interval` lands on the first FUTURE occurrence (one wait, not
                    // a burst of overdue fires).
                    var missed = (long)((now - armedAt).TotalSeconds / intervalSeconds!.Value);
                    last = armedAt.AddSeconds(missed * intervalSeconds.Value);
                }
                else
                {
                    // Cron: seed the first walk just before now. GetNextOccurrence is exclusive of
                    // its input, so `now - 1 tick` yields the occurrence at/after now (keeping one
                    // landing exactly on now in scope) instead of the first stale one after armedAt.
                    last = now.AddTicks(-1);
                }
            }

            while (!ct.IsCancellationRequested)
            {
                DateTimeOffset next;
                if (expr is not null)
                {
                    var occurrence = expr.GetNextOccurrence(last.UtcDateTime, TimeZoneInfo.Utc);
                    if (occurrence is null)
                    {
                        return; // no further occurrences — the schedule is exhausted.
                    }

                    next = new DateTimeOffset(occurrence.Value, TimeSpan.Zero);
                }
                else
                {
                    next = last.AddSeconds(intervalSeconds!.Value);
                }

                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // disposed/cancelled before firing — no fire.
                    }
                }

                await sink.FireAsync(new TriggerFireEvent(next.ToString("o")), ct);
                last = next;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync();

            // Do NOT await _loop here: disposal is normally invoked by the runtime from within a
            // fire callback, and awaiting our own still-running task would deadlock. Dispose the
            // CTS once the loop settles, off the current stack.
            _ = _loop.ContinueWith(
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
