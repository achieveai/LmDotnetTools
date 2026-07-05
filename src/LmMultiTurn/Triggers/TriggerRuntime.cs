using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.Sources;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Delivers a resolved wait payload back to the owning agent loop. Supplied by the loop so the
/// runtime never references loop internals or grows public loop APIs — for a block wait this is
/// simply <c>MultiTurnAgentLoop.ResolveToolCallAsync</c> partially applied.
/// </summary>
public delegate Task TriggerResolveDelegate(
    string toolCallId,
    string result,
    bool isError,
    CancellationToken cancellationToken);

/// <summary>
/// A block wait recovered from persisted history on restart. Carries the original <c>Wait</c>
/// arguments and the instant it was armed so the runtime can re-arm (or expire) it.
/// </summary>
public sealed record RestoredWait(string ToolCallId, string FunctionArgs, long DeferredAtUnixMs);

/// <summary>
/// Owns the entire lifecycle of every armed wait: the single-resolution latch, the ceiling
/// timeout, cancellation, bounded concurrency, resource cleanup, and delivery back to the loop.
/// Trigger <see cref="ITriggerSource"/>s are dumb observers that only report fire events to a
/// runtime-owned sink; every state transition and policy decision happens here so behavior is
/// identical across all kinds.
/// </summary>
/// <remarks>
/// State machine per wait: <c>Pending → { Fired | TimedOut | Cancelled | Failed }</c>, exactly one
/// terminal transition, claimed atomically. Whoever wins the claim performs cleanup and delivers
/// the single result; all other concurrent transitions (a fire racing the timeout racing a cancel)
/// become no-ops.
/// </remarks>
public sealed class TriggerRuntime : IAsyncDisposable
{
    // Ceiling fires this much AFTER the nominal deadline so that, for time-based sources whose
    // fire instant coincides with the ceiling, the source's "fired" outcome deterministically
    // wins the latch over "timed_out".
    private const int CeilingGraceMs = 50;

    // The loop registers a deferral and appends its placeholder to history immediately AFTER the
    // handler returns Deferred; a fast fire can briefly precede that append. Tolerate it with a
    // bounded retry (generous total budget for slow persistence) rather than exposing loop
    // internals to gate on. ~200 * 50ms ≈ 10s worst case.
    private const int DeliverMaxAttempts = 200;
    private const int DeliverRetryMs = 50;

    private readonly TriggerOptions _options;
    private readonly TriggerResolveDelegate _resolve;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ConcurrentDictionary<string, TriggerSourceRegistration> _registrations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ArmedWait> _waits = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public TriggerRuntime(TriggerOptions options, TriggerResolveDelegate resolve, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolve);
        _options = options;
        _resolve = resolve;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(options.MaxConcurrentWaits, options.MaxConcurrentWaits);
    }

    /// <summary>Registers the built-in one-shot <c>timer</c> source.</summary>
    public void RegisterBuiltIns()
    {
        Register(new TriggerSourceRegistration
        {
            Kind = TimerTriggerSource.KindName,
            Description = "Wait for a one-shot timer to elapse (a relative delay or an absolute deadline).",
            ArgsSchema = TimerTriggerSource.ArgsSchemaText,
            Capabilities = TimerTriggerSource.Capabilities,
            Source = new TimerTriggerSource(),
        });
    }

    /// <summary>Registers a trigger kind. The last registration for a kind wins.</summary>
    public void Register(TriggerSourceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registrations[registration.Kind] = registration;
    }

    /// <summary>The kinds currently registered.</summary>
    public IReadOnlyCollection<string> RegisteredKinds => [.. _registrations.Keys];

    /// <summary>
    /// Builds the stable, per-session catalog embedded in the <c>Wait</c> tool description so the
    /// model can see every kind and its args shape.
    /// </summary>
    public string DescribeKindsForToolContract()
    {
        var sb = new StringBuilder();
        _ = sb.Append("Registered wait kinds:");
        foreach (var reg in _registrations.Values.OrderBy(r => r.Kind, StringComparer.Ordinal))
        {
            _ = sb.Append("\n- ").Append(reg.Kind).Append(": ").Append(reg.Description)
                .Append("\n  args: ").Append(reg.ArgsSchema);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Arms a fresh block wait keyed by <paramref name="waitId"/> (the deferred tool_call_id).
    /// Returns <see cref="WaitArmResult.Accept"/> when armed (caller parks the run) or a structured
    /// rejection otherwise.
    /// </summary>
    public async Task<WaitArmResult> ArmAsync(
        string waitId,
        string kind,
        string argsJson,
        string timeout,
        string? label,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return WaitArmResult.Reject("shutting_down", "The trigger runtime is disposed.");
        }

        if (!_registrations.TryGetValue(kind, out var reg))
        {
            return WaitArmResult.Reject(
                "unknown_kind",
                $"No trigger kind '{kind}' is registered. Registered kinds: {string.Join(", ", RegisteredKinds)}.");
        }

        if (!reg.Capabilities.SupportsBlock)
        {
            return WaitArmResult.Reject("unsupported_mode", $"Kind '{kind}' does not support block waits.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TriggerDurations.TryResolveDeadline(timeout, now, _options.MaxBlockWaitDuration, out var deadline, out var timeoutError))
        {
            return WaitArmResult.Reject("invalid_timeout", timeoutError ?? "invalid timeout");
        }

        return await ArmCoreAsync(waitId, reg, argsJson, label, now, deadline, ct);
    }

    private async Task<WaitArmResult> ArmCoreAsync(
        string waitId,
        TriggerSourceRegistration reg,
        string argsJson,
        string? label,
        DateTimeOffset armedAt,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        // Bounded concurrency: acquire once here, release exactly once when the wait terminates
        // (or in the failure path below if arming the source throws).
        if (!await _concurrencyGate.WaitAsync(_options.GateAcquireTimeout, ct))
        {
            return WaitArmResult.Reject(
                "max_concurrent_waits",
                $"The maximum of {_options.MaxConcurrentWaits} concurrent waits is reached. "
                + "Cancel a wait or wait for one to complete.");
        }

        var wait = new ArmedWait
        {
            WaitId = waitId,
            Kind = reg.Kind,
            Label = label,
            ArmedAt = armedAt,
            Deadline = deadline,
        };

        try
        {
            var request = new TriggerArmRequest
            {
                WaitId = waitId,
                Kind = reg.Kind,
                ArgsJson = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson,
                Label = label,
                ArmedAt = armedAt,
                Deadline = deadline,
            };

            var sink = new WaitSink(this, wait);
            wait.Source = await reg.Source.ArmAsync(request, sink, _shutdown.Token);

            // Defensive: a well-behaved source fires asynchronously (never during ArmAsync), but if
            // one fired synchronously the wait already reached a terminal state and FinalizeAsync ran
            // before wait.Source was assigned (so it could not dispose the source). Complete that
            // cleanup here and don't re-register or start a ceiling for an already-terminal wait.
            if (wait.State != WaitState.Pending)
            {
                await wait.Source.DisposeAsync();
                return WaitArmResult.Accept(waitId);
            }

            _waits[waitId] = wait;
            StartCeilingTimer(wait);

            _logger?.LogInformation(
                "trigger.armed {WaitId} kind={Kind} deadline={Deadline:o}",
                waitId, reg.Kind, deadline);

            return WaitArmResult.Accept(waitId);
        }
        catch (ArgumentException ex)
        {
            // The source rejected malformed/contradictory args (e.g. a timer given both `delay`
            // and `deadline`). This is caller-correctable, so it gets its own reason distinct from
            // an unexpected internal failure.
            ReleaseGate(wait);
            _ = _waits.TryRemove(waitId, out _);
            _logger?.LogInformation("trigger.arm_rejected {WaitId} kind={Kind} reason={Message}", waitId, reg.Kind, ex.Message);
            return WaitArmResult.Reject("invalid_args", ex.Message);
        }
        catch (Exception ex)
        {
            ReleaseGate(wait);
            _ = _waits.TryRemove(waitId, out _);
            _logger?.LogWarning(ex, "trigger.arm_failed {WaitId} kind={Kind}", waitId, reg.Kind);
            return WaitArmResult.Reject("arm_failed", ex.Message);
        }
    }

    /// <summary>
    /// Cancels every armed wait matching the selector (by id, label, or kind). Total and
    /// idempotent: unknown or already-terminal waits contribute nothing. Returns the number of
    /// waits this call transitioned to <see cref="WaitState.Cancelled"/>.
    /// </summary>
    public async Task<int> CancelWaitsAsync(string? id, string? label, string? kind, CancellationToken ct)
    {
        List<ArmedWait> matches = [.. _waits.Values.Where(w => Matches(w, id, label, kind))];

        var cancelled = 0;
        foreach (var wait in matches)
        {
            if (await FinalizeAsync(wait, WaitState.Cancelled, BuildTerminalPayload("cancelled", wait), isError: false))
            {
                cancelled++;
            }
        }
        return cancelled;
    }

    private static bool Matches(ArmedWait wait, string? id, string? label, string? kind)
    {
        // A total selector (all null) matches nothing — CancelWait requires at least one filter.
        if (id == null && label == null && kind == null)
        {
            return false;
        }

        if (id != null && !string.Equals(wait.WaitId, id, StringComparison.Ordinal))
        {
            return false;
        }
        if (label != null && !string.Equals(wait.Label, label, StringComparison.Ordinal))
        {
            return false;
        }
        if (kind != null && !string.Equals(wait.Kind, kind, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    /// <summary>Read model of currently-armed waits (no payloads or source internals exposed).</summary>
    public IReadOnlyList<WaitInfo> ListWaits() =>
        [.. _waits.Values
            .Where(w => w.State == WaitState.Pending)
            .Select(w => new WaitInfo
            {
                WaitId = w.WaitId,
                Kind = w.Kind,
                Label = w.Label,
                State = WaitState.Pending,
                ArmedAt = w.ArmedAt.ToString("o"),
                Deadline = w.Deadline.ToString("o"),
            })];

    /// <summary>
    /// Reconciles block waits recovered from persisted history after a restart. A restorable kind
    /// (e.g. timer) is re-armed for its remaining delay — firing immediately if already elapsed. A
    /// non-restorable kind, or one whose original args no longer parse, resolves as
    /// <c>trigger_lost_on_restart</c> so the parked run is never left hanging.
    /// </summary>
    public async Task ReconcileRestoredAsync(IReadOnlyList<RestoredWait> restored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(restored);

        foreach (var r in restored)
        {
            if (!WaitToolArgs.TryParse(r.FunctionArgs, out var parsed))
            {
                await DeliverAsync(r.ToolCallId, BuildFailedPayload(r.ToolCallId, "unknown", null, "trigger_lost_on_restart"), isError: false);
                continue;
            }

            if (!_registrations.TryGetValue(parsed.Kind, out var reg) || !reg.Capabilities.SupportsRestore)
            {
                await DeliverAsync(
                    r.ToolCallId,
                    BuildFailedPayload(r.ToolCallId, parsed.Kind, parsed.Label, "trigger_lost_on_restart"),
                    isError: false);
                continue;
            }

            // A missing arm timestamp (older/hand-authored data) would otherwise map to the Unix
            // epoch and expire instantly — restart the clock from now instead.
            var armedAt = r.DeferredAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(r.DeferredAtUnixMs)
                : DateTimeOffset.UtcNow;
            if (!TriggerDurations.TryResolveDeadline(parsed.Timeout, armedAt, _options.MaxBlockWaitDuration, out var deadline, out _))
            {
                // Ceiling already elapsed while offline (or unparseable) — resolve as timed out.
                await DeliverAsync(
                    r.ToolCallId,
                    BuildTerminalPayload("timed_out", new ArmedWait { WaitId = r.ToolCallId, Kind = parsed.Kind, Label = parsed.Label, ArmedAt = armedAt, Deadline = armedAt }),
                    isError: false);
                continue;
            }

            var armResult = await ArmCoreAsync(r.ToolCallId, reg, parsed.ArgsJson, parsed.Label, armedAt, deadline, ct);
            if (!armResult.IsArmed)
            {
                // Re-arm was rejected (e.g. concurrency limit, source arm failure). Do NOT leave the
                // parked run hanging — resolve it as a restart failure.
                await DeliverAsync(
                    r.ToolCallId,
                    BuildFailedPayload(r.ToolCallId, parsed.Kind, parsed.Label, armResult.Reason ?? "trigger_lost_on_restart"),
                    isError: false);
            }
        }
    }

    // ---- internal transition machinery -------------------------------------------------------

    private async ValueTask OnSourceFiredAsync(ArmedWait wait, TriggerFireEvent fire)
    {
        var payload = BuildFiredPayload(wait, fire);
        _ = await FinalizeAsync(wait, WaitState.Fired, payload, isError: false);
    }

    /// <summary>
    /// Attempts the single terminal transition for <paramref name="wait"/>. The winner stops the
    /// ceiling timer, disposes the source (no fire after dispose), releases the concurrency slot,
    /// and delivers the one result. Returns true only for the winning caller.
    /// </summary>
    private async Task<bool> FinalizeAsync(ArmedWait wait, WaitState state, string payload, bool isError)
    {
        if (!wait.TryClaim(state))
        {
            return false; // lost the race — already terminal.
        }

        _logger?.LogInformation("trigger.{State} {WaitId} kind={Kind}", state.ToString().ToLowerInvariant(), wait.WaitId, wait.Kind);

        try
        {
            wait.CancelCeiling();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "trigger ceiling-timer teardown failed for {WaitId}", wait.WaitId);
        }

        if (wait.Source != null)
        {
            try
            {
                await wait.Source.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "trigger source dispose failed for {WaitId}", wait.WaitId);
            }
        }

        _ = _waits.TryRemove(wait.WaitId, out _);
        ReleaseGate(wait);

        // Deliver with a fresh, uncancellable token: the terminal result must reach the loop even
        // though disposing the source above cancels the source's own token, and even if the
        // operation that triggered this transition (a CancelWait) carried a cancelled token.
        await DeliverAsync(wait.WaitId, payload, isError);
        return true;
    }

    private async Task DeliverAsync(string toolCallId, string payload, bool isError)
    {
        var ct = CancellationToken.None;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _resolve(toolCallId, payload, isError, ct);
                return;
            }
            catch (ObjectDisposedException)
            {
                return; // loop torn down — nothing to resolve into.
            }
            catch (InvalidOperationException ex)
                when (attempt < DeliverMaxAttempts
                    && !ex.Message.Contains("already been resolved", StringComparison.Ordinal))
            {
                // The deferred placeholder is not in history yet (handler just returned Deferred).
                // Retry briefly until the loop finishes appending it.
                try
                {
                    await Task.Delay(DeliverRetryMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Conflict, or the placeholder never appeared after the retry budget — give up.
                _logger?.LogWarning(ex, "trigger delivery failed for {ToolCallId}", toolCallId);
                return;
            }
        }
    }

    private void StartCeilingTimer(ArmedWait wait)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        wait.CeilingCts = linked;
        var token = linked.Token;

        _ = Task.Run(async () =>
        {
            var grace = TimeSpan.FromMilliseconds(CeilingGraceMs);
            var delay = wait.Deadline + grace - DateTimeOffset.UtcNow;
            if (delay < grace)
            {
                // The deadline itself may already be in the past (e.g. a wait restored after the
                // process was offline past its timeout). The source's own timer also clamps its
                // remaining delay to "fire ASAP" in that case, so the ceiling must still wait the
                // full grace period measured from now - clamping straight to zero here would erase
                // the source's head start and turn the "fired vs timed_out" outcome into a race.
                delay = grace;
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return; // wait already terminated another way.
            }

            _ = await FinalizeAsync(wait, WaitState.TimedOut, BuildTerminalPayload("timed_out", wait), isError: false);
        });
    }

    private void ReleaseGate(ArmedWait wait)
    {
        if (Interlocked.Exchange(ref wait.GateReleased, 1) == 0)
        {
            try
            {
                _ = _concurrencyGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // A fire/timeout finalizing concurrently with runtime disposal may reach here after
                // the gate is disposed. The slot no longer matters once the runtime is shutting down.
            }
        }
    }

    // ---- payload building --------------------------------------------------------------------

    private string BuildFiredPayload(ArmedWait wait, TriggerFireEvent fire)
    {
        var detail = fire.Payload;
        if (detail != null && Encoding.UTF8.GetByteCount(detail) > _options.MaxPayloadBytes)
        {
            // Reserve room for the marker so the resulting detail stays within MaxPayloadBytes.
            var marker = $"\n[truncated to {_options.MaxPayloadBytes} bytes]";
            var budget = Math.Max(0, _options.MaxPayloadBytes - Encoding.UTF8.GetByteCount(marker));
            detail = TruncateUtf8(detail, budget) + marker;
        }

        return Serialize(new Dictionary<string, object?>
        {
            ["status"] = "fired",
            ["kind"] = wait.Kind,
            ["label"] = wait.Label,
            ["waitId"] = wait.WaitId,
            ["firedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["detail"] = detail,
        });
    }

    private static string BuildTerminalPayload(string status, ArmedWait wait) =>
        Serialize(new Dictionary<string, object?>
        {
            ["status"] = status,
            ["kind"] = wait.Kind,
            ["label"] = wait.Label,
            ["waitId"] = wait.WaitId,
            ["deadline"] = wait.Deadline.ToString("o"),
        });

    private static string BuildFailedPayload(string waitId, string kind, string? label, string reason) =>
        Serialize(new Dictionary<string, object?>
        {
            ["status"] = "failed",
            ["reason"] = reason,
            ["kind"] = kind,
            ["label"] = label,
            ["waitId"] = waitId,
        });

    private static string Serialize(Dictionary<string, object?> map)
    {
        // Drop null-valued keys for a compact, stable payload.
        var pruned = map.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.Serialize(pruned);
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        // Trim to a valid UTF-8 boundary at or below maxBytes.
        var count = maxBytes;
        while (count > 0 && (bytes[count] & 0xC0) == 0x80)
        {
            count--;
        }
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _shutdown.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "trigger runtime shutdown cancel failed");
        }

        foreach (var wait in _waits.Values)
        {
            try
            {
                wait.CancelCeiling();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "trigger ceiling teardown failed for {WaitId} during dispose", wait.WaitId);
            }

            if (wait.Source != null)
            {
                try
                {
                    await wait.Source.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "trigger source dispose failed for {WaitId} during dispose", wait.WaitId);
                }
            }
        }

        _waits.Clear();
        _shutdown.Dispose();
        _concurrencyGate.Dispose();
    }

    /// <summary>Runtime-owned mutable state for one armed wait. All transitions latch on <see cref="TryClaim"/>.</summary>
    private sealed class ArmedWait
    {
        private int _state; // WaitState as int; 0 == Pending.
        public int GateReleased; // 0 == not released.

        public required string WaitId { get; init; }
        public required string Kind { get; init; }
        public string? Label { get; init; }
        public required DateTimeOffset ArmedAt { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public IArmedTrigger? Source { get; set; }
        public CancellationTokenSource? CeilingCts { get; set; }

        public WaitState State => (WaitState)Volatile.Read(ref _state);

        /// <summary>Atomically claim the single terminal transition. True only for the first caller.</summary>
        public bool TryClaim(WaitState target) =>
            Interlocked.CompareExchange(ref _state, (int)target, (int)WaitState.Pending) == (int)WaitState.Pending;

        public void CancelCeiling()
        {
            var cts = CeilingCts;
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    /// <summary>Per-wait sink handed to a source; forwards fires to the runtime bound to one wait.</summary>
    private sealed class WaitSink(TriggerRuntime runtime, ArmedWait wait) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken) =>
            runtime.OnSourceFiredAsync(wait, fire);
    }
}
