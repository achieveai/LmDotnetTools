using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
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
/// Injects a notify-mode trigger envelope as a fresh queued turn on the owning loop. Supplied by
/// the loop (partial application of <c>EnqueueTriggerNotifyAsync</c>). Null when the host wired no
/// notify path — arming a notify wait is then rejected.
/// </summary>
public delegate Task TriggerNotifyDelegate(string payload, bool isError, CancellationToken cancellationToken);

/// <summary>
/// Non-blocking counterpart to <see cref="TriggerNotifyDelegate"/>, used ONLY by
/// <see cref="TriggerRuntime.RestoreNotifyWaitsAsync"/> to deliver restart-terminal envelopes.
/// Recovery can run before the loop's run-loop starts reading its bounded input channel, so
/// restore-time delivery must never block a full channel — it must attempt a non-blocking enqueue
/// and report whether the envelope was actually accepted. Supplied by the loop (partial
/// application of a non-blocking channel write, e.g. <c>TryEnqueueTriggerNotify</c>). Null when
/// the host wired no such delegate — restore then falls back to the (potentially blocking)
/// <see cref="TriggerNotifyDelegate"/>.
/// </summary>
/// <returns>True if the envelope was accepted for delivery; false if it could not be (e.g. the
/// channel is currently full) — the caller must retain the persisted row for redelivery on the
/// next recovery rather than losing it.</returns>
public delegate bool TriggerTryNotifyDelegate(string payload, bool isError);

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
    private readonly TriggerNotifyDelegate? _notify;
    private readonly TriggerTryNotifyDelegate? _tryNotify;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ConcurrentDictionary<string, TriggerSourceRegistration> _registrations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ArmedWait> _waits = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public TriggerRuntime(
        TriggerOptions options,
        TriggerResolveDelegate resolve,
        TriggerNotifyDelegate? notify = null,
        TriggerTryNotifyDelegate? tryNotify = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolve);
        if (options.NotifyWaitStore != null && string.IsNullOrEmpty(options.ThreadId))
        {
            throw new ArgumentException(
                "TriggerOptions.NotifyWaitStore requires a non-empty ThreadId so durable notify restore is scoped to a conversation.",
                nameof(options));
        }
        _options = options;
        _resolve = resolve;
        _notify = notify;
        _tryNotify = tryNotify;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(options.MaxConcurrentWaits, options.MaxConcurrentWaits);
    }

    /// <summary>
    /// Binary-compatibility overload for callers using the pre-notify-mode positional shape
    /// <c>(options, resolve, logger)</c>. Delegates to the primary constructor with no notify
    /// delegate wired (arming a notify-mode wait is then rejected — see <see cref="ArmAsync"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="logger"/> intentionally has no default value here: giving it one would make
    /// this constructor ambiguous with the primary constructor for a 2-arg call (both would be
    /// applicable via default-substitution, and neither is preferred by the language's tie-break
    /// rules). Omitting the default keeps this overload applicable only to genuine 3-arg calls
    /// (where it is unambiguously preferred, since it substitutes no optional parameters) while
    /// 2-arg calls still resolve — unambiguously — to the primary constructor.
    /// </remarks>
    public TriggerRuntime(
        TriggerOptions options,
        TriggerResolveDelegate resolve,
        ILogger? logger)
        : this(options, resolve, notify: null, tryNotify: null, logger: logger)
    {
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
        WaitMode mode,
        int? maxFires,
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

        if (mode == WaitMode.Block && !reg.Capabilities.SupportsBlock)
        {
            return WaitArmResult.Reject("unsupported_mode", $"Kind '{kind}' does not support block waits.");
        }

        if (mode == WaitMode.Notify)
        {
            if (!reg.Capabilities.SupportsNotify)
            {
                return WaitArmResult.Reject("unsupported_mode", $"Kind '{kind}' does not support notify waits.");
            }
            if (_notify == null)
            {
                return WaitArmResult.Reject("notify_unavailable", "This host did not enable notify-mode waits.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (!TriggerDurations.TryResolveDeadline(timeout, now, _options.MaxBlockWaitDuration, out var deadline, out var timeoutError))
        {
            return WaitArmResult.Reject("invalid_timeout", timeoutError ?? "invalid timeout");
        }

        return await ArmCoreAsync(waitId, reg, argsJson, label, mode, maxFires, now, deadline, ct);
    }

    private async Task<WaitArmResult> ArmCoreAsync(
        string waitId,
        TriggerSourceRegistration reg,
        string argsJson,
        string? label,
        WaitMode mode,
        int? maxFires,
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

        var request = new TriggerArmRequest
        {
            WaitId = waitId,
            Kind = reg.Kind,
            ArgsJson = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson,
            Label = label,
            ArmedAt = armedAt,
            Deadline = deadline,
        };

        var wait = new ArmedWait
        {
            WaitId = waitId,
            Kind = reg.Kind,
            Label = label,
            Mode = mode,
            MaxFires = maxFires,
            ArmedAt = armedAt,
            Deadline = deadline,
            ArgsJson = request.ArgsJson,
        };

        try
        {
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

            // Notify-mode waits have no deferred tool_call_id to reuse as a persisted arming record
            // (unlike block waits, restored from history), so persist here when the host wired a
            // durable store. Best-effort: a transient persistence failure should not fail an
            // otherwise-successful arm — it just means this wait won't survive a restart.
            if (mode == WaitMode.Notify && _options.NotifyWaitStore != null && _options.ThreadId != null)
            {
                try
                {
                    await _options.NotifyWaitStore.SaveAsync(new NotifyWaitRecord(
                        waitId, _options.ThreadId, reg.Kind, request.ArgsJson, label, maxFires, 0,
                        deadline.ToUnixTimeMilliseconds(), armedAt.ToUnixTimeMilliseconds(), "active"), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The caller abandoned the call — do not report an armed wait it never asked
                    // for. Rethrow so the outer catch below cleans up and the cancellation
                    // propagates, unlike a genuine persistence failure (handled below), which is
                    // best-effort and must not fail an otherwise-successful arm.
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "notify-wait persist failed for {WaitId}", waitId);
                }
            }

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Reached only via the rethrow above, after the source was already armed (registered,
            // ceiling running, tracked in _waits). Use the single-claim teardown path — not a bare
            // ReleaseGate/TryRemove — so a stray late fire or ceiling timeout on this same wait
            // can't win a second claim and double-release the gate or double-notify.
            _ = await TryTeardownAsync(wait, WaitState.Failed);
            throw;
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
                    BuildTerminalPayload("timed_out", new ArmedWait { WaitId = r.ToolCallId, Kind = parsed.Kind, Label = parsed.Label, Mode = WaitMode.Block, ArmedAt = armedAt, Deadline = armedAt }),
                    isError: false);
                continue;
            }

            var armResult = await ArmCoreAsync(r.ToolCallId, reg, parsed.ArgsJson, parsed.Label, WaitMode.Block, maxFires: null, armedAt, deadline, ct);
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

    /// <summary>
    /// Re-arms notify-mode waits persisted for this runtime's thread after a restart. Restorable kinds
    /// re-arm from their remaining fire budget and TTL (no envelope enqueued — safe to await, never
    /// touches the loop's input channel). Non-restorable kinds (or unregistered ones) and elapsed-TTL
    /// rows deliver one final terminal envelope; that delivery goes through
    /// <see cref="TryDeliverRestoreNotifyAsync"/> so it can NEVER block recovery on a bounded input
    /// channel whose reader may not have started yet (see the caller docs on
    /// <see cref="TriggerTryNotifyDelegate"/>). A row is deleted only once its terminal envelope was
    /// actually accepted for delivery; if the channel is currently full, the row is deliberately left
    /// in the store so it is retried on the next recovery — no loss, no deadlock. No-op when no store
    /// or thread is configured.
    /// </summary>
    public async Task RestoreNotifyWaitsAsync(CancellationToken ct)
    {
        var store = _options.NotifyWaitStore;
        var threadId = _options.ThreadId;
        if (store == null || threadId == null)
        {
            return;
        }

        var rows = await store.LoadActiveAsync(threadId, ct);
        foreach (var row in rows)
        {
            if (!_registrations.TryGetValue(row.Kind, out var reg) || !reg.Capabilities.SupportsRestore || !reg.Capabilities.SupportsNotify)
            {
                if (await TryDeliverRestoreNotifyAsync(BuildFailedPayload(row.WaitId, row.Kind, row.Label, "trigger_lost_on_restart"), isError: false))
                {
                    await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
                }
                else
                {
                    _logger?.LogDebug(
                        "notify-wait restore: input channel unavailable for {WaitId}, retaining row for redelivery on next recovery",
                        row.WaitId);
                }
                continue;
            }

            // An exhausted row (fires_so_far already reached maxFires) is stale terminal state: the
            // final fire's envelope was already delivered by OnSourceFiredAsync before this row's own
            // row-delete could complete (e.g. the process crashed between persisting fires_so_far and
            // TryTeardownAsync's DeleteAsync). Re-arming would either double-fire or, if the remaining
            // budget clamps to zero, arm a wait that can never terminate on its own — so treat it as
            // terminal here too: clean up the row, no re-arm, no redundant notification.
            if (row.MaxFires is int exhaustedMaxFires && row.FiresSoFar >= exhaustedMaxFires)
            {
                await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
                continue;
            }

            // A missing arm timestamp (older/hand-authored data) would otherwise map to the Unix
            // epoch and expire instantly — restart the clock from now instead.
            var armedAt = row.ArmedAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(row.ArmedAtUnixMs) : DateTimeOffset.UtcNow;
            var deadline = row.TimeoutAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(row.TimeoutAtUnixMs) : armedAt;
            if (deadline <= DateTimeOffset.UtcNow)
            {
                // TTL already elapsed while offline — terminal envelope, delete only if accepted.
                if (await TryDeliverRestoreNotifyAsync(
                    BuildTerminalPayload("timed_out", new ArmedWait { WaitId = row.WaitId, Kind = row.Kind, Label = row.Label, Mode = WaitMode.Notify, ArmedAt = armedAt, Deadline = deadline }),
                    isError: false))
                {
                    await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
                }
                else
                {
                    _logger?.LogDebug(
                        "notify-wait restore: input channel unavailable for {WaitId}, retaining row for redelivery on next recovery",
                        row.WaitId);
                }
                continue;
            }

            var remainingMaxFires = row.MaxFires is int mf ? Math.Max(0, mf - row.FiresSoFar) : (int?)null;
            var armResult = await ArmCoreAsync(row.WaitId, reg, row.Args, row.Label, WaitMode.Notify, remainingMaxFires, armedAt, deadline, ct);
            if (!armResult.IsArmed)
            {
                if (await TryDeliverRestoreNotifyAsync(BuildFailedPayload(row.WaitId, row.Kind, row.Label, armResult.Reason ?? "trigger_lost_on_restart"), isError: false))
                {
                    await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
                }
                else
                {
                    _logger?.LogDebug(
                        "notify-wait restore: input channel unavailable for {WaitId}, retaining row for redelivery on next recovery",
                        row.WaitId);
                }
            }
        }
    }

    // ---- internal transition machinery -------------------------------------------------------

    private async ValueTask OnSourceFiredAsync(ArmedWait wait, TriggerFireEvent fire)
    {
        if (wait.Mode == WaitMode.Block)
        {
            var blockPayload = BuildFiredPayload(wait, fire);
            _ = await FinalizeAsync(wait, WaitState.Fired, blockPayload, isError: false);
            return;
        }

        // Notify mode: deliver each fire as a queued envelope; stay armed until maxFires or TTL.
        if (wait.State != WaitState.Pending)
        {
            return; // already terminal (cancelled / timed_out) — ignore a late fire.
        }

        var fireNumber = Interlocked.Increment(ref wait.FiresSoFar);
        if (wait.MaxFires is int over && fireNumber > over)
        {
            return; // a concurrent fire already consumed the last budget slot.
        }

        var isFinalFire = wait.MaxFires is int cap && fireNumber >= cap;
        var payload = BuildFiredPayload(wait, fire);
        await NotifyAsync(wait, payload, isError: false);

        // Best-effort: keep the persisted fire count current so a restart resumes with the correct
        // remaining budget. A failure here does not affect delivery of the fire already sent above.
        // Debounced ONLY for unlimited waits (no maxFires): a high-frequency notify source (e.g. a
        // file-tail matching every line) would otherwise issue one SQLite write per fire, and an
        // unlimited wait's remaining budget is unbounded anyway so an approximately-current
        // fires_so_far is fine. A maxFires-bounded wait persists every fire — debouncing it would
        // let a crash lose up to 9 fires and over-grant the remaining budget on restore.
        var shouldPersist = wait.MaxFires is not null || fireNumber == 1 || fireNumber % 10 == 0 || isFinalFire;
        if (shouldPersist && _options.NotifyWaitStore != null && _options.ThreadId != null)
        {
            try
            {
                await _options.NotifyWaitStore.SaveAsync(new NotifyWaitRecord(
                    wait.WaitId, _options.ThreadId, wait.Kind, wait.ArgsJson, wait.Label, wait.MaxFires, fireNumber,
                    wait.Deadline.ToUnixTimeMilliseconds(), wait.ArmedAt.ToUnixTimeMilliseconds(), "active"), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "notify-wait fires_so_far update failed for {WaitId}", wait.WaitId);
            }
        }

        if (isFinalFire)
        {
            // The last fire's envelope IS the terminal message (decision #2 satisfied) — tear down
            // without delivering a second, redundant envelope.
            _ = await TryTeardownAsync(wait, WaitState.Fired);
        }
    }

    /// <summary>
    /// Claims the single terminal transition and runs teardown (stop ceiling, dispose source, release
    /// gate, deregister). Returns true only for the winning caller. Does NOT deliver — callers that
    /// owe a terminal message call this then deliver.
    /// </summary>
    private async Task<bool> TryTeardownAsync(ArmedWait wait, WaitState state)
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

        if (wait.Mode == WaitMode.Notify && _options.NotifyWaitStore != null)
        {
            try
            {
                await _options.NotifyWaitStore.DeleteAsync(_options.ThreadId!, wait.WaitId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "notify-wait row delete failed for {WaitId}", wait.WaitId);
            }
        }

        return true;
    }

    /// <summary>
    /// Attempts the single terminal transition for <paramref name="wait"/>. The winner stops the
    /// ceiling timer, disposes the source (no fire after dispose), releases the concurrency slot,
    /// and delivers the one result. Returns true only for the winning caller.
    /// </summary>
    private async Task<bool> FinalizeAsync(ArmedWait wait, WaitState state, string payload, bool isError)
    {
        if (!await TryTeardownAsync(wait, state))
        {
            return false;
        }

        // Deliver with a fresh, uncancellable token: the terminal result must reach the loop even
        // though disposing the source above cancels the source's own token, and even if the
        // operation that triggered this transition (a CancelWait) carried a cancelled token.
        if (wait.Mode == WaitMode.Notify)
        {
            await NotifyAsync(wait, payload, isError);
        }
        else
        {
            await DeliverAsync(wait.WaitId, payload, isError);
        }
        return true;
    }

    private async Task NotifyAsync(ArmedWait wait, string payload, bool isError)
    {
        if (_notify == null)
        {
            _logger?.LogWarning("trigger notify fired for {WaitId} but no notify delegate is wired", wait.WaitId);
            return;
        }

        try
        {
            // Use the runtime's own shutdown token, not CancellationToken.None: _notify writes to
            // the loop's bounded input channel, and a full channel would otherwise block this
            // source task indefinitely even after the runtime is disposed.
            await _notify(payload, isError, _shutdown.Token);
        }
        catch (ObjectDisposedException)
        {
            // Loop torn down — nothing to inject into.
        }
        catch (OperationCanceledException)
        {
            // Runtime shutting down — drop the fire; the loop side swallows this too.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "trigger notify delivery failed for {WaitId}", wait.WaitId);
        }
    }

    /// <summary>
    /// Delivers a restart-terminal envelope (<c>trigger_lost_on_restart</c>, <c>timed_out</c>) with
    /// no backing <see cref="ArmedWait"/> — used exclusively by <see cref="RestoreNotifyWaitsAsync"/>.
    /// Prefers the non-blocking <see cref="_tryNotify"/> delegate so recovery can never block on the
    /// loop's bounded input channel before its reader has started (the deadlock this exists to avoid
    /// — see <see cref="TriggerTryNotifyDelegate"/>). When no non-blocking delegate is wired (e.g.
    /// older/simple test wiring where the plain <see cref="_notify"/> callback never blocks), falls
    /// back to the original blocking delivery so existing behavior is preserved.
    /// </summary>
    /// <returns>
    /// True if the envelope was accepted for delivery (the caller should delete the persisted row);
    /// false if the non-blocking channel could not accept it right now (the caller must retain the
    /// row for redelivery on the next recovery — never delete on a rejected/failed delivery).
    /// </returns>
    private async Task<bool> TryDeliverRestoreNotifyAsync(string payload, bool isError)
    {
        if (_tryNotify != null)
        {
            try
            {
                return _tryNotify(payload, isError);
            }
            catch (ObjectDisposedException)
            {
                // Loop torn down — nothing to inject into. Treat as "not delivered": the row is
                // retained, which is harmless since a torn-down loop won't run RestoreNotifyWaitsAsync
                // again until a future recovery picks the row back up.
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "trigger notify-restore delivery failed");
                return false;
            }
        }

        if (_notify == null)
        {
            _logger?.LogWarning("trigger notify-restore fired but no notify delegate is wired");
            return false;
        }

        try
        {
            await _notify(payload, isError, _shutdown.Token);
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Loop torn down — nothing to inject into.
            return true; // matches prior behavior: row is deleted, not retried, on teardown.
        }
        catch (OperationCanceledException)
        {
            // Runtime shutting down — drop the fire; the loop side swallows this too.
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "trigger notify-restore delivery failed");
            return true;
        }
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
        public required WaitMode Mode { get; init; }
        public int? MaxFires { get; init; }
        public int FiresSoFar; // interlocked; notify mode only.
        public string ArgsJson { get; init; } = "{}"; // notify mode only; used to persist fires_so_far updates.
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
