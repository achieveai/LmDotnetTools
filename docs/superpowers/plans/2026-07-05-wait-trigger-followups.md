# Wait/Trigger Follow-ups (#140–#145) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the six follow-ups deferred from the #107 Wait/trigger primitive — notify mode (#140), a `file_tail` source (#141), a `process` source (#142), a cron+interval `schedule` kind (#143), a sub-agent-completion source (#144), and durable notify-watcher restore (#145).

**Architecture:** Core (`src/LmMultiTurn`) grows notify mode as a *second* lifecycle in `TriggerRuntime` (block mode's one-shot latch is untouched), a `QueuedInput` notify-envelope variant delivered through the existing `EnqueueRawAsync` gate, one thin `SubAgentManager` seam for #144, and a dedicated SQLite table for #145. The four new *sources* live in `LmStreaming.Sample` and register through the existing `TriggerOptions.AdditionalRegistrations` door — core only grows a seam when a source needs core-internal state (#144 only).

**Tech Stack:** C# / .NET 9, xUnit + FluentAssertions + Moq, `Microsoft.Data.Sqlite`, `Cronos` (new, sample-app only), `System.Text.Json`.

## Global Constraints

- **.NET SDK** 8.0+ (`Directory.Build.props`); the sample host + its test project target **net9.0**.
- **Formatting:** CSharpier (`.csharpierrc.json`) + `.editorconfig`. Do not script-fix build errors/warnings — autofix or fix manually.
- **No AI/Claude signature** in any commit message.
- **Never create v2/-enhanced/-improved files** — modify existing files in place.
- **Block mode is frozen:** every #140 change is scoped strictly to the notify branch. Block-mode waits keep today's exact one-shot latch (`Pending → Fired|TimedOut|Cancelled|Failed`), zero behavior change.
- **Terminal envelope is mandatory** for notify waits (locked decision #2): cancellation, TTL expiry, and `trigger_lost_on_restart` each deliver a final `<trigger>` envelope — never silence.
- **Notify delivery is queue-only, never interrupting** (locked decision #1): a fire injects through the loop's internal gate; it never preempts an in-flight generation.
- **New sources are sample-app-first** (locked decision #4): `LmStreaming.Sample` for the class, `tests/LmStreaming.Sample.Tests` for its tests, registered via `TriggerOptions.AdditionalRegistrations`.
- **`#146` is parked** — out of scope entirely.
- Structured logging: message templates with named properties, never string interpolation for variable data.

---

## File Structure

**Core — `src/LmMultiTurn/` (#140, #144 seam, #145):**
- `Triggers/WaitMode.cs` — **new** enum `{ Block, Notify }`.
- `Triggers/WaitToolArgs.cs` — **modify** record: add `Mode`, `MaxFires`; parse `mode`/`maxFires`.
- `Triggers/WaitToolProvider.cs` — **modify** `CreateWaitDescriptor` (add `mode`/`maxFires` params) + `HandleWaitAsync` (branch notify vs block).
- `Triggers/TriggerRuntime.cs` — **modify**: `TriggerNotifyDelegate`, notify-aware `ArmAsync`/`ArmCoreAsync`, `ArmedWait.Mode/MaxFires/FiresSoFar`, notify-branch `OnSourceFiredAsync`, `TryTeardownAsync` extraction, `NotifyAsync`, mode-routed `FinalizeAsync`; (#145) persist-on-arm / delete-on-terminal + `RestoreNotifyWaitsAsync`.
- `Messages/QueuedInput.cs` — **modify**: add `TriggerEnvelope? Trigger = null`.
- `Messages/TriggerEnvelope.cs` — **new** record `(bool IsError)` telemetry marker.
- `MultiTurnAgentLoop.cs` — **modify**: pass `notify:` delegate + notify store to `TriggerRuntime` ctor; add `EnqueueTriggerNotifyAsync`; call `RestoreNotifyWaitsAsync` alongside `OnHistoryRestoredAsync`.
- `SubAgents/SubAgentManager.cs` — **modify** (#144 seam): add `ObserveCompletionAsync(string agentId, CancellationToken)` + `SetNotifyParentOnCompletion(string agentId, bool)`.
- `Persistence/INotifyWaitStore.cs` — **new** interface + `NotifyWaitRecord`.
- `Persistence/Sqlite/SqliteSchemaInitializer.cs` — **modify**: add `CreateNotifyWaitsTableSql` DDL.
- `Persistence/Sqlite/SqliteNotifyWaitStore.cs` — **new** store consuming `ISqliteConnectionFactory`.

**Sample host — `samples/LmStreaming.Sample/` (#141–#144):**
- `Triggers/FileTailTriggerSource.cs` — **new** (#141).
- `Triggers/ProcessTriggerSource.cs` — **new** (#142).
- `Triggers/ScheduleTriggerSource.cs` — **new** (#143).
- `Triggers/SubAgentCompletionTriggerSource.cs` — **new** (#144).
- `Triggers/SampleTriggerRegistrations.cs` — **new** helper assembling `AdditionalRegistrations`.
- `Program.cs` — **modify** (~L907): pass `triggerOptions:` to the `MultiTurnAgentLoop` ctor.
- `LmStreaming.Sample.csproj` — **modify**: add `Cronos` PackageReference.

**Tests:**
- `tests/LmMultiTurn.Tests/Triggers/` — notify lifecycle, ordering, `WaitToolArgs`, `WaitToolProvider`, #145 reconcile.
- `tests/LmMultiTurn.Tests/SubAgents/` — `ObserveCompletionAsync` + flag suppression.
- `tests/LmMultiTurn.Tests/Persistence/` — `SqliteNotifyWaitStore`.
- `tests/LmStreaming.Sample.Tests/Triggers/` — the four sources.

---

## Dependency order

```
Phase 1  #140 core notify mode        (Tasks 1–5)   ── foundational
Phase 2  #144 core seam               (Task 6)      ── independent of Phase 1
Phase 3  sample TriggerOptions wiring (Task 7)      ── needs Phase 1 (runtime notify) to demo notify
Phase 4  #141 file_tail source        (Task 8)      ── needs Task 7
Phase 5  #142 process source          (Task 9)      ── needs Task 7
Phase 6  #143 schedule kind           (Task 10)     ── needs #140 + Task 7
Phase 7  #144 sample source           (Task 11)     ── needs Task 6 + Task 7
Phase 8  #145 durable notify restore  (Tasks 12–14) ── needs #140
```

Tasks 1–5 must land first. Task 6 can run in parallel with Phase 1. Tasks 8/9/11 are mutually independent once Task 7 lands. Tasks 12–14 come last.

---

## Phase 1 — #140 Notify mode (core)

### Task 1: `WaitMode` enum + `WaitToolArgs` parsing

**Files:**
- Create: `src/LmMultiTurn/Triggers/WaitMode.cs`
- Modify: `src/LmMultiTurn/Triggers/WaitToolArgs.cs`
- Test: `tests/LmMultiTurn.Tests/Triggers/WaitToolArgsTests.cs`

**Interfaces:**
- Produces: `enum WaitMode { Block, Notify }`; `WaitToolArgs(string Kind, string ArgsJson, string Timeout, string? Label, WaitMode Mode, int? MaxFires)`; `WaitToolArgs.TryParse` now reads `mode` (default `Block`) and `maxFires` (default `null`).

- [ ] **Step 1: Write the failing test**

Add to `tests/LmMultiTurn.Tests/Triggers/WaitToolArgsTests.cs` (create the file if absent; namespace `AchieveAi.LmDotnetTools.LmMultiTurn.Tests.Triggers`, `using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;`):

```csharp
[Fact]
public void TryParse_DefaultsToBlockMode_WhenModeOmitted()
{
    var ok = WaitToolArgs.TryParse("""{"kind":"timer","timeout":"10m"}""", out var args);
    ok.Should().BeTrue();
    args.Mode.Should().Be(WaitMode.Block);
    args.MaxFires.Should().BeNull();
}

[Fact]
public void TryParse_ReadsNotifyModeAndMaxFires()
{
    var ok = WaitToolArgs.TryParse(
        """{"kind":"timer","timeout":"1h","mode":"notify","maxFires":3}""", out var args);
    ok.Should().BeTrue();
    args.Mode.Should().Be(WaitMode.Notify);
    args.MaxFires.Should().Be(3);
}

[Fact]
public void TryParse_RejectsUnknownMode()
{
    WaitToolArgs.TryParse("""{"kind":"timer","timeout":"1h","mode":"bogus"}""", out _)
        .Should().BeFalse();
}

[Fact]
public void TryParse_RejectsNonPositiveMaxFires()
{
    WaitToolArgs.TryParse("""{"kind":"timer","timeout":"1h","mode":"notify","maxFires":0}""", out _)
        .Should().BeFalse();
}
```

- [ ] **Step 2: Run the test — expect FAIL**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~WaitToolArgsTests"`
Expected: compile error (`Mode`/`MaxFires` don't exist) or assertion failures.

- [ ] **Step 3: Create `WaitMode.cs`**

```csharp
namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// How a <c>Wait</c> delivers its result. <see cref="Block"/> parks the tool call and resolves it
/// once (the merged #107 behavior). <see cref="Notify"/> arms without parking: each fire injects a
/// <c>&lt;trigger&gt;</c> envelope as a fresh turn and the wait stays armed for more fires.
/// </summary>
public enum WaitMode
{
    Block = 0,
    Notify = 1,
}
```

- [ ] **Step 4: Extend `WaitToolArgs`**

Replace the record declaration line and `TryParse` body in `src/LmMultiTurn/Triggers/WaitToolArgs.cs`. New record header:

```csharp
internal sealed record WaitToolArgs(
    string Kind,
    string ArgsJson,
    string Timeout,
    string? Label,
    WaitMode Mode = WaitMode.Block,
    int? MaxFires = null)
```

Inside `TryParse`, change the default seed and add `mode`/`maxFires` parsing before the final `result = ...`. The default seed becomes:

```csharp
result = new WaitToolArgs(string.Empty, "{}", string.Empty, null);
```

After computing `label` (and before `result = new WaitToolArgs(...)`), add:

```csharp
var mode = WaitMode.Block;
if (root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
{
    var modeText = modeEl.GetString();
    if (string.Equals(modeText, "notify", StringComparison.OrdinalIgnoreCase))
    {
        mode = WaitMode.Notify;
    }
    else if (!string.Equals(modeText, "block", StringComparison.OrdinalIgnoreCase))
    {
        return false; // unknown mode
    }
}

int? maxFires = null;
if (root.TryGetProperty("maxFires", out var maxEl) && maxEl.ValueKind != JsonValueKind.Null)
{
    if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out var mf) || mf < 1)
    {
        return false; // maxFires must be a positive integer when present
    }
    maxFires = mf;
}
```

Then set the final result:

```csharp
result = new WaitToolArgs(
    kind,
    argsJson,
    timeout,
    string.IsNullOrWhiteSpace(label) ? null : label,
    mode,
    maxFires);
return true;
```

- [ ] **Step 5: Run the test — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~WaitToolArgsTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LmMultiTurn/Triggers/WaitMode.cs src/LmMultiTurn/Triggers/WaitToolArgs.cs tests/LmMultiTurn.Tests/Triggers/WaitToolArgsTests.cs
git commit -m "feat(triggers): parse notify mode + maxFires in WaitToolArgs (#140)"
```

---

### Task 2: `TriggerRuntime` notify lifecycle

**Files:**
- Modify: `src/LmMultiTurn/Triggers/TriggerRuntime.cs`
- Test: `tests/LmMultiTurn.Tests/Triggers/TriggerRuntimeNotifyTests.cs`

**Interfaces:**
- Consumes: `WaitMode` (Task 1).
- Produces:
  - `public delegate Task TriggerNotifyDelegate(string payload, bool isError, CancellationToken cancellationToken);`
  - New ctor param: `TriggerRuntime(TriggerOptions options, TriggerResolveDelegate resolve, TriggerNotifyDelegate? notify = null, ILogger? logger = null)`.
  - New `ArmAsync` overload signature: `ArmAsync(string waitId, string kind, string argsJson, string timeout, string? label, WaitMode mode, int? maxFires, CancellationToken ct)`.
  - Behavior: notify fires deliver via `notify`; wait stays armed until `maxFires` reached or `timeout` TTL elapses; cancel/TTL deliver a terminal envelope via `notify`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LmMultiTurn.Tests/Triggers/TriggerRuntimeNotifyTests.cs`. Use a manual-fire fake source so the test controls fire timing:

```csharp
using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.Triggers;

public class TriggerRuntimeNotifyTests
{
    // A source whose ArmAsync hands back a sink the test can fire on demand.
    private sealed class ManualSource : ITriggerSource
    {
        public static TriggerCapabilities Caps { get; } =
            new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);
        public readonly ConcurrentDictionary<string, ITriggerEventSink> Sinks = new();

        public ValueTask<IArmedTrigger> ArmAsync(
            TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken ct)
        {
            Sinks[request.WaitId] = eventSink;
            return ValueTask.FromResult<IArmedTrigger>(new Handle(request.WaitId, this));
        }

        private sealed class Handle(string waitId, ManualSource owner) : IArmedTrigger
        {
            public string WaitId { get; } = waitId;
            public ValueTask DisposeAsync()
            {
                owner.Sinks.TryRemove(WaitId, out _);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static (TriggerRuntime rt, ManualSource src, List<(string payload, bool isError)> notified)
        BuildRuntime()
    {
        var notified = new List<(string, bool)>();
        var src = new ManualSource();
        var options = new TriggerOptions();
        var rt = new TriggerRuntime(
            options,
            resolve: (_, _, _, _) => Task.CompletedTask,
            notify: (payload, isError, _) =>
            {
                lock (notified) { notified.Add((payload, isError)); }
                return Task.CompletedTask;
            });
        rt.Register(new TriggerSourceRegistration
        {
            Kind = "manual",
            Description = "test",
            ArgsSchema = "{}",
            Capabilities = ManualSource.Caps,
            Source = src,
        });
        return (rt, src, notified);
    }

    [Fact]
    public async Task NotifyWait_StaysArmed_AcrossMultipleFires()
    {
        var (rt, src, notified) = BuildRuntime();
        var armed = await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: null, CancellationToken.None);
        armed.IsArmed.Should().BeTrue();

        var sink = src.Sinks["w1"];
        await sink.FireAsync(new TriggerFireEvent { Payload = "one" }, CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent { Payload = "two" }, CancellationToken.None);

        notified.Should().HaveCount(2);
        rt.ListWaits().Should().ContainSingle(w => w.WaitId == "w1"); // still armed
    }

    [Fact]
    public async Task NotifyWait_Terminates_WhenMaxFiresReached()
    {
        var (rt, src, notified) = BuildRuntime();
        await rt.ArmAsync("w2", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: 2, CancellationToken.None);
        var sink = src.Sinks["w2"];

        await sink.FireAsync(new TriggerFireEvent { Payload = "a" }, CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent { Payload = "b" }, CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent { Payload = "c" }, CancellationToken.None); // over budget

        notified.Should().HaveCount(2); // exactly maxFires envelopes, none after
        rt.ListWaits().Should().NotContain(w => w.WaitId == "w2"); // terminated
    }

    [Fact]
    public async Task NotifyWait_Cancel_DeliversTerminalEnvelope()
    {
        var (rt, src, notified) = BuildRuntime();
        await rt.ArmAsync("w3", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: null, CancellationToken.None);

        var cancelled = await rt.CancelWaitsAsync("w3", null, null, CancellationToken.None);

        cancelled.Should().Be(1);
        notified.Should().ContainSingle(n => n.payload.Contains("cancelled"));
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (compile: `TriggerNotifyDelegate`, notify ctor, 7-arg `ArmAsync` don't exist).

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~TriggerRuntimeNotifyTests"`

- [ ] **Step 3: Add the notify delegate + ctor field**

In `TriggerRuntime.cs`, after the `TriggerResolveDelegate` delegate (line ~18) add:

```csharp
/// <summary>
/// Injects a notify-mode trigger envelope as a fresh queued turn on the owning loop. Supplied by
/// the loop (partial application of <c>EnqueueTriggerNotifyAsync</c>). Null when the host wired no
/// notify path — arming a notify wait is then rejected.
/// </summary>
public delegate Task TriggerNotifyDelegate(string payload, bool isError, CancellationToken cancellationToken);
```

Add a field beside `_resolve`:

```csharp
private readonly TriggerNotifyDelegate? _notify;
```

Change the ctor to accept and assign it:

```csharp
public TriggerRuntime(
    TriggerOptions options,
    TriggerResolveDelegate resolve,
    TriggerNotifyDelegate? notify = null,
    ILogger? logger = null)
{
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(resolve);
    _options = options;
    _resolve = resolve;
    _notify = notify;
    _logger = logger;
    _concurrencyGate = new SemaphoreSlim(options.MaxConcurrentWaits, options.MaxConcurrentWaits);
}
```

- [ ] **Step 4: Thread `mode`/`maxFires` through `ArmAsync` → `ArmCoreAsync`**

Replace `ArmAsync`'s signature and add notify validation (after the `SupportsBlock` check, guard notify):

```csharp
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
```

Update `ArmCoreAsync`'s signature to accept `WaitMode mode, int? maxFires` (insert before `armedAt`) and set them on the `ArmedWait`:

```csharp
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
    // ... unchanged gate acquisition ...

    var wait = new ArmedWait
    {
        WaitId = waitId,
        Kind = reg.Kind,
        Label = label,
        Mode = mode,
        MaxFires = maxFires,
        ArmedAt = armedAt,
        Deadline = deadline,
    };
    // ... rest unchanged ...
```

Update the sole existing `ArmCoreAsync` caller inside `ReconcileRestoredAsync` (block restore) to pass block mode:

```csharp
var armResult = await ArmCoreAsync(r.ToolCallId, reg, parsed.ArgsJson, parsed.Label, WaitMode.Block, maxFires: null, armedAt, deadline, ct);
```

- [ ] **Step 5: Notify-branch `OnSourceFiredAsync` + teardown extraction + `NotifyAsync`**

Replace `OnSourceFiredAsync` with a mode-aware version:

```csharp
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

    if (isFinalFire)
    {
        // The last fire's envelope IS the terminal message (decision #2 satisfied) — tear down
        // without delivering a second, redundant envelope.
        _ = await TryTeardownAsync(wait, WaitState.Fired);
    }
}
```

Extract teardown from `FinalizeAsync` into a reusable helper, and route delivery on mode. Replace `FinalizeAsync` with:

```csharp
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
    return true;
}

private async Task<bool> FinalizeAsync(ArmedWait wait, WaitState state, string payload, bool isError)
{
    if (!await TryTeardownAsync(wait, state))
    {
        return false;
    }

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
        await _notify(payload, isError, CancellationToken.None);
    }
    catch (ObjectDisposedException)
    {
        // Loop torn down — nothing to inject into.
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "trigger notify delivery failed for {WaitId}", wait.WaitId);
    }
}
```

> Note: `CancelWaitsAsync` and `StartCeilingTimer` already call `FinalizeAsync`, so a notify wait's cancel/TTL now automatically route their terminal payload through `NotifyAsync` — no change needed there.

- [ ] **Step 6: Add `Mode`/`MaxFires`/`FiresSoFar` to `ArmedWait`**

In the private `ArmedWait` class, add:

```csharp
public required WaitMode Mode { get; init; }
public int? MaxFires { get; init; }
public int FiresSoFar; // interlocked; notify mode only.
```

- [ ] **Step 7: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~TriggerRuntimeNotifyTests"`
Expected: PASS.

- [ ] **Step 8: Run the full trigger suite — verify block mode is untouched**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~Triggers"`
Expected: PASS (all pre-existing `TriggerRuntimeTests` green — block mode unchanged).

- [ ] **Step 9: Commit**

```bash
git add src/LmMultiTurn/Triggers/TriggerRuntime.cs tests/LmMultiTurn.Tests/Triggers/TriggerRuntimeNotifyTests.cs
git commit -m "feat(triggers): notify-mode multi-fire lifecycle in TriggerRuntime (#140)"
```

---

### Task 3: `QueuedInput.Trigger` + loop notify wiring

**Files:**
- Create: `src/LmMultiTurn/Messages/TriggerEnvelope.cs`
- Modify: `src/LmMultiTurn/Messages/QueuedInput.cs`
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs`
- Test: `tests/LmMultiTurn.Tests/Triggers/NotifyEnvelopeDeliveryTests.cs`

**Interfaces:**
- Consumes: `TriggerNotifyDelegate` (Task 2).
- Produces: `record TriggerEnvelope(bool IsError)`; `QueuedInput(..., TriggerEnvelope? Trigger = null)`; the loop now constructs `TriggerRuntime` with a `notify:` delegate bound to `EnqueueTriggerNotifyAsync(string payload, bool isError, CancellationToken)`.

- [ ] **Step 1: Write the failing test**

`tests/LmMultiTurn.Tests/Triggers/NotifyEnvelopeDeliveryTests.cs` — arm a notify `timer`… but `timer` is `SupportsNotify: false`. Use a test harness that registers a notify-capable manual source via `TriggerOptions.AdditionalRegistrations`, then drives a `MultiTurnAgentLoop` and asserts a fired envelope shows up as a new user turn in history. Follow the existing `WaitTriggerLoopIntegrationTests.cs` construction pattern (it already builds a loop with `TriggerOptions`). Minimal assertion:

```csharp
[Fact]
public async Task NotifyFire_InjectsTriggerTaggedUserTurn()
{
    // Arrange: loop with a notify-capable manual source registered via AdditionalRegistrations,
    // mock provider that, on the first user turn, calls Wait(kind:"manual", mode:"notify", timeout:"1h").
    // (Mirror WaitTriggerLoopIntegrationTests' harness.)
    // Act: fire the source's sink once.
    // Assert: history gains a user TextMessage whose Text contains "<trigger>" and the fire payload.
    var history = await RunNotifyScenarioAsync(fireCount: 1);
    history.Should().Contain(m =>
        m is TextMessage tm && tm.Role == Role.User && tm.Text.Contains("<trigger>"));
}
```

> Implementation note for the executor: copy `WaitTriggerLoopIntegrationTests.cs`'s loop+mock-provider scaffold into this file's `RunNotifyScenarioAsync` helper; register the `ManualSource` from Task 2 (make it `internal` in a shared test-support file, or re-declare locally) through `TriggerOptions { AdditionalRegistrations = [ manualRegistration ] }`.

- [ ] **Step 2: Run — expect FAIL** (`Trigger` param / `EnqueueTriggerNotifyAsync` / notify wiring absent).

- [ ] **Step 3: Create `TriggerEnvelope.cs`**

```csharp
namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Marks a <see cref="QueuedInput"/> that was injected by a notify-mode trigger fire rather than a
/// real user. The actual content rides in <c>Input.Messages</c> (a <c>&lt;trigger&gt;</c>-tagged
/// user message); this marker exists only for telemetry/log correlation, mirroring how
/// <c>ResumeSentinel</c> marks an internal resume. <see cref="IsError"/> is true for a failure
/// envelope (e.g. a source fault surfaced as a fire).
/// </summary>
public record TriggerEnvelope(bool IsError);
```

- [ ] **Step 4: Extend `QueuedInput`**

```csharp
public record QueuedInput(
    UserInput Input,
    string ReceiptId,
    DateTimeOffset QueuedAt,
    ResumeSentinel? Resume = null,
    TriggerEnvelope? Trigger = null);
```

Add a doc line above `Trigger` (kept concise): `/// <param name="Trigger">Non-null when this entry was injected by a notify-mode trigger fire (telemetry only; content is in Input.Messages, Resume is null so it drives a real run).</param>` — merge into the existing XML doc block.

- [ ] **Step 5: Wire the notify delegate + helper in `MultiTurnAgentLoop`**

In the trigger-construction block (`MultiTurnAgentLoop.cs` ~L149), add the `notify:` argument:

```csharp
_triggerRuntime = new TriggerRuntime(
    triggerOptions,
    resolve: (toolCallId, result, isError, ct) =>
        ResolveToolCallAsync(toolCallId, result, isError, contentBlocks: null, ct),
    notify: (payload, isError, ct) => EnqueueTriggerNotifyAsync(payload, isError, ct),
    logger: logger);
```

Add the helper near `EnqueueResumeSentinel` (after `WriteResumeSentinelAsync`, ~L1058). It mirrors that method but builds a *non-empty* `UserInput` with `Resume == null` so the run loop treats it as a real input:

```csharp
/// <summary>
/// Injects a notify-mode trigger fire as a fresh user turn through the same internal gate
/// ResumeSentinel uses. Resume is null and Messages is non-empty, so RunLoopAsync adds it to
/// history and drives a new run — queued strictly behind any in-flight turn (never interrupting,
/// per locked decision #1). Supplied to <see cref="TriggerRuntime"/> as its notify delegate.
/// </summary>
private async Task EnqueueTriggerNotifyAsync(string payload, bool isError, CancellationToken ct)
{
    var envelope = new TextMessage
    {
        Role = Role.User,
        Text = $"<trigger>\n{payload}\n</trigger>",
    };
    var input = new UserInput([envelope], InputId: null, ParentRunId: null);
    var queued = new QueuedInput(
        input,
        ReceiptId: $"notify:{Guid.NewGuid():N}",
        QueuedAt: DateTimeOffset.UtcNow,
        Resume: null,
        Trigger: new TriggerEnvelope(isError));

    try
    {
        await EnqueueRawAsync(queued, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Expected on shutdown.
    }
    catch (ObjectDisposedException)
    {
        // Loop torn down mid-fire — drop the envelope.
    }
}
```

> Confirm the `using` for `AchieveAi.LmDotnetTools.LmMultiTurn.Messages` and `AchieveAi.LmDotnetTools.LmCore.Messages` (for `TextMessage`/`Role`) are already present in `MultiTurnAgentLoop.cs` — they are (the file already uses `TextMessage`/`UserInput`/`QueuedInput`).

- [ ] **Step 6: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~NotifyEnvelopeDeliveryTests"`

- [ ] **Step 7: Commit**

```bash
git add src/LmMultiTurn/Messages/TriggerEnvelope.cs src/LmMultiTurn/Messages/QueuedInput.cs src/LmMultiTurn/MultiTurnAgentLoop.cs tests/LmMultiTurn.Tests/Triggers/NotifyEnvelopeDeliveryTests.cs
git commit -m "feat(triggers): inject notify envelopes through the loop's queue gate (#140)"
```

---

### Task 4: `WaitToolProvider` notify branch

**Files:**
- Modify: `src/LmMultiTurn/Triggers/WaitToolProvider.cs`
- Test: `tests/LmMultiTurn.Tests/Triggers/WaitToolProviderTests.cs` (append)

**Interfaces:**
- Consumes: `WaitToolArgs.Mode/MaxFires` (Task 1), `TriggerRuntime.ArmAsync(...,mode,maxFires,...)` (Task 2).
- Produces: `Wait` tool contract gains `mode` + `maxFires` params; `HandleWaitAsync` returns `Deferred()` for block, `FromText({status:"armed",...})` for notify.

- [ ] **Step 1: Write the failing tests** (append to `WaitToolProviderTests.cs`):

```csharp
[Fact]
public async Task HandleWait_BlockMode_ReturnsDeferred()
{
    // existing block behavior (regression guard): a timer block wait defers.
    var result = await InvokeWaitAsync("""{"kind":"timer","timeout":"10m"}""");
    result.Should().BeOfType<ToolHandlerResult.Deferred>();
}

[Fact]
public async Task HandleWait_NotifyMode_ReturnsArmedAcknowledgment_NotDeferred()
{
    // Register a notify-capable source in the test runtime, then:
    var result = await InvokeWaitAsync("""{"kind":"manual","timeout":"1h","mode":"notify","maxFires":2}""");
    result.Should().NotBeOfType<ToolHandlerResult.Deferred>();
    var text = ExtractText(result);
    text.Should().Contain("\"status\":\"armed\"");
    text.Should().Contain("\"mode\":\"notify\"");
}
```

> `InvokeWaitAsync`/`ExtractText` helpers mirror the existing test file's setup (construct a `TriggerRuntime` + `WaitToolProvider`, grab the `Wait` descriptor's `Handler`, invoke with a `ToolCallContext` carrying a non-empty `ToolCallId`). Register the notify-capable `ManualSource` from Task 2.

- [ ] **Step 2: Run — expect FAIL** (notify arm currently returns `Deferred`, and `ArmAsync` signature mismatch).

- [ ] **Step 3: Add `mode`/`maxFires` to `CreateWaitDescriptor`**

Insert two parameter contracts into the `Parameters` array (after `label`):

```csharp
new FunctionParameterContract
{
    Name = "mode",
    Description =
        "\"block\" (default) parks the run until the event fires or times out — the result is "
        + "this call's return value. \"notify\" arms without parking: each fire is delivered as a "
        + "new message and the wait stays armed for more fires until maxFires or timeout.",
    ParameterType = new JsonSchemaObject { Type = new("string") },
    IsRequired = false,
},
new FunctionParameterContract
{
    Name = "maxFires",
    Description =
        "Notify mode only: stop after this many fires (positive integer). Omit for unlimited "
        + "(bounded only by timeout). Ignored in block mode.",
    ParameterType = new JsonSchemaObject { Type = new("integer") },
    IsRequired = false,
},
```

- [ ] **Step 4: Branch `HandleWaitAsync`**

Replace the arm + return section (after `WaitToolArgs.TryParse`):

```csharp
var result = await _runtime.ArmAsync(
    context.ToolCallId,
    parsed.Kind,
    parsed.ArgsJson,
    parsed.Timeout,
    parsed.Label,
    parsed.Mode,
    parsed.MaxFires,
    cancellationToken);

if (!result.IsArmed)
{
    return ToolHandlerResult.FromError(
        Reject(result.Reason ?? "rejected", result.Message ?? "Wait was rejected."),
        result.Reason);
}

if (parsed.Mode == WaitMode.Notify)
{
    // Notify arms do NOT park the run; acknowledge immediately. Fires arrive later as
    // <trigger> turns via the loop's queue gate.
    return ToolHandlerResult.FromText(JsonSerializer.Serialize(new
    {
        status = "armed",
        mode = "notify",
        waitId = context.ToolCallId,
        kind = parsed.Kind,
        maxFires = parsed.MaxFires,
    }));
}

// Block mode: park the run; the runtime resolves this tool call when the wait terminates.
return new ToolHandlerResult.Deferred();
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~WaitToolProviderTests"`

- [ ] **Step 6: Commit**

```bash
git add src/LmMultiTurn/Triggers/WaitToolProvider.cs tests/LmMultiTurn.Tests/Triggers/WaitToolProviderTests.cs
git commit -m "feat(triggers): Wait tool notify mode + maxFires params (#140)"
```

---

### Task 5: Notify ordering integration tests

**Files:**
- Test: `tests/LmMultiTurn.Tests/Triggers/NotifyOrderingTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4. No production code — this task proves locked decision #1 (fires never interrupt).

- [ ] **Step 1: Write the ordering tests**

Cover the four cases from the spec's Testing section. Reuse the `RunNotifyScenarioAsync` harness from Task 3:

```csharp
[Fact]
public async Task Fire_DuringActiveGeneration_QueuesBehindCurrentTurn() { /* fire while provider is mid-stream; assert the <trigger> turn lands AFTER the in-flight assistant message */ }

[Fact]
public async Task Fire_DuringToolExecution_QueuesBehindToolResult() { /* fire while a tool handler is running; assert ordering */ }

[Fact]
public async Task Fire_WhileAnotherBlockWaitDeferred_DoesNotResolveTheBlockWait() { /* a notify fire must not touch a parked block wait's tool_call_id */ }

[Fact]
public async Task MultipleFires_DeliverInFireOrder() { /* fire 3x rapidly; assert 3 <trigger> turns in order */ }
```

> Use the mock provider's scripted turns (see `WaitTriggerLoopIntegrationTests.cs`) to control when generation/tool-exec is "in flight," and fire the manual source's sink at that point. Assert on final history ordering, not timing.

- [ ] **Step 2: Run — expect PASS** (behavior already implemented; these lock it in).

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~NotifyOrderingTests"`
Expected: PASS. If any FAIL, the fire is interrupting — fix the enqueue path, do not weaken the test.

- [ ] **Step 3: Commit**

```bash
git add tests/LmMultiTurn.Tests/Triggers/NotifyOrderingTests.cs
git commit -m "test(triggers): notify-mode ordering guarantees (#140)"
```

---

## Phase 2 — #144 core seam (SubAgentManager)

### Task 6: `ObserveCompletionAsync` + `SetNotifyParentOnCompletion`

**Files:**
- Modify: `src/LmMultiTurn/SubAgents/SubAgentManager.cs`
- Test: `tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerObserveCompletionTests.cs`

**Interfaces:**
- Produces:
  - `public Task<string> ObserveCompletionAsync(string agentId, CancellationToken ct)` — awaits the sub-agent's completion latch by id; throws `ArgumentException` for an unknown id (same guard as `Peek`).
  - `public void SetNotifyParentOnCompletion(string agentId, bool value)` — flips the automatic-relay flag for one sub-agent by id; throws `ArgumentException` for an unknown id.

- [ ] **Step 1: Write the failing tests**

```csharp
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.SubAgents;

public class SubAgentManagerObserveCompletionTests
{
    [Fact]
    public async Task ObserveCompletionAsync_ReturnsResult_WhenSubAgentCompletes()
    {
        // Arrange a manager with a spawned sub-agent whose run completes with text "done".
        // (Reuse the existing SubAgentManager test scaffold / builder in this test project.)
        var (manager, agentId) = await SpawnCompletingSubAgentAsync(result: "done");
        var observed = await manager.ObserveCompletionAsync(agentId, CancellationToken.None);
        observed.Should().Be("done");
    }

    [Fact]
    public async Task ObserveCompletionAsync_Throws_ForUnknownId()
    {
        var manager = BuildEmptyManager();
        var act = () => manager.ObserveCompletionAsync("nope", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void SetNotifyParentOnCompletion_Throws_ForUnknownId()
    {
        var manager = BuildEmptyManager();
        var act = () => manager.SetNotifyParentOnCompletion("nope", false);
        act.Should().Throw<ArgumentException>();
    }
}
```

> `SpawnCompletingSubAgentAsync`/`BuildEmptyManager` reuse whatever scaffold the existing `SubAgentManager` tests use (check `tests/LmMultiTurn.Tests/SubAgents/` for the existing builder before writing new setup).

- [ ] **Step 2: Run — expect FAIL** (methods don't exist).

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerObserveCompletionTests"`

- [ ] **Step 3: Add the two methods**

Add near `Peek(string agentId)` (~L333) in `SubAgentManager.cs`:

```csharp
/// <summary>
/// Awaits a sub-agent's completion by id, returning its final text (or throwing its
/// <see cref="SubAgentExecutionException"/> on failure). Used by the sample-app
/// SubAgentCompletionTriggerSource so a Wait can observe a sub-agent the same way the internal
/// synchronous path does. If the caller's <paramref name="ct"/> fires, the sub-agent is cancelled
/// (identical to the internal synchronous wait).
/// </summary>
public Task<string> ObserveCompletionAsync(string agentId, CancellationToken ct)
{
    if (!_agents.TryGetValue(agentId, out var state))
    {
        throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
    }

    return AwaitCompletionAsync(state, ct);
}

/// <summary>
/// Sets whether a specific sub-agent's completion is automatically relayed to the parent. A
/// trigger source waiting on this sub-agent flips it to <c>false</c> at arm time (so the result
/// arrives once, via the trigger envelope, not twice) and MUST restore it to <c>true</c> if the
/// wait is cancelled before completion.
/// </summary>
public void SetNotifyParentOnCompletion(string agentId, bool value)
{
    if (!_agents.TryGetValue(agentId, out var state))
    {
        throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
    }

    state.NotifyParentOnCompletion = value;
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SubAgentManagerObserveCompletionTests"`

- [ ] **Step 5: Commit**

```bash
git add src/LmMultiTurn/SubAgents/SubAgentManager.cs tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerObserveCompletionTests.cs
git commit -m "feat(subagents): observe-completion-by-id + relay-flag seam for #144 (#144)"
```

---

## Phase 3 — Sample TriggerOptions wiring

### Task 7: Assemble `AdditionalRegistrations` + pass `triggerOptions` to the loop

**Files:**
- Create: `samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs` (~L907, the `MultiTurnAgentLoop` ctor call)
- Test: `tests/LmStreaming.Sample.Tests/Triggers/SampleTriggerRegistrationsTests.cs`

**Interfaces:**
- Produces: `static class SampleTriggerRegistrations { static TriggerOptions Build(bool sandboxEnabled, /* deps added per source in later tasks */); }` returning a `TriggerOptions` whose `AdditionalRegistrations` list is populated. Later tasks (#141/#142/#143/#144) append their registration here.

- [ ] **Step 1: Write the failing test**

```csharp
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;
using FluentAssertions;
using Xunit;

namespace LmStreaming.Sample.Tests.Triggers;

public class SampleTriggerRegistrationsTests
{
    [Fact]
    public void Build_ReturnsTriggerOptions_WithNoDuplicateKinds()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true);
        var kinds = options.AdditionalRegistrations.Select(r => r.Kind).ToList();
        kinds.Should().OnlyHaveUniqueItems();
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (class doesn't exist).

Run: `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~SampleTriggerRegistrationsTests"`

- [ ] **Step 3: Create the registration helper** (starts empty; sources are appended in later tasks)

```csharp
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Assembles the sample host's <see cref="TriggerOptions"/> — the built-in <c>timer</c> kind plus
/// the sample-app sources (file_tail, schedule, subagent always; process only when Sandbox is on),
/// registered through <see cref="TriggerOptions.AdditionalRegistrations"/>.
/// </summary>
public static class SampleTriggerRegistrations
{
    public static TriggerOptions Build(bool sandboxEnabled)
    {
        var registrations = new List<TriggerSourceRegistration>();

        // (#141) file_tail, (#143) schedule, (#144) subagent registrations appended here in later tasks.
        // (#142) process registration appended here, guarded by `if (sandboxEnabled)`, in Task 9.

        return new TriggerOptions
        {
            AdditionalRegistrations = registrations,
        };
    }
}
```

- [ ] **Step 4: Pass `triggerOptions` in `Program.cs`**

At the `MultiTurnAgentLoop` ctor call (~L907–935), add the argument after `loggerFactory: loggerFactory`:

```csharp
        loggerFactory: loggerFactory,
        triggerOptions: SampleTriggerRegistrations.Build(sandboxEnabled: sandboxSession is not null)
    );
```

Add `using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;` to `Program.cs`'s usings if not present.

- [ ] **Step 5: Run — expect PASS + sample builds**

Run: `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~SampleTriggerRegistrationsTests"`
Then: `dotnet build samples/LmStreaming.Sample/LmStreaming.Sample.csproj`
Expected: both succeed.

- [ ] **Step 6: Commit**

```bash
git add samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs samples/LmStreaming.Sample/Program.cs tests/LmStreaming.Sample.Tests/Triggers/SampleTriggerRegistrationsTests.cs
git commit -m "feat(sample): wire TriggerOptions into the sample agent loop (#141)"
```

---

## Phase 4 — #141 FileTailTriggerSource

### Task 8: `FileTailTriggerSource` (sample-app)

**Files:**
- Create: `samples/LmStreaming.Sample/Triggers/FileTailTriggerSource.cs`
- Modify: `samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs`
- Test: `tests/LmStreaming.Sample.Tests/Triggers/FileTailTriggerSourceTests.cs`

**Interfaces:**
- Consumes: `ITriggerSource`/`ITriggerEventSink`/`IArmedTrigger`/`TriggerArmRequest`/`TriggerFireEvent`/`TriggerCapabilities` (core).
- Produces: `sealed class FileTailTriggerSource(IReadOnlyList<string> allowedRoots) : ITriggerSource` with `KindName = "file_tail"`, `Capabilities = (SupportsBlock: true, SupportsNotify: true, SupportsRestore: false)`; args `{ path: string, pattern?: string }`; host-assigned aliasing so `ListWaits` never leaks the raw path.

- [ ] **Step 1: Write the failing tests** (path-security first — the highest-risk surface):

```csharp
public class FileTailTriggerSourceTests
{
    [Fact]
    public async Task Arm_Rejects_PathOutsideAllowedRoots()
    {
        var root = CreateTempDir();
        var src = new FileTailTriggerSource(new[] { root });
        var req = ArmReq(path: Path.Combine(Path.GetTempPath(), "elsewhere.log"));
        var act = () => src.ArmAsync(req, NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>(); // arm-time rejection, not runtime
    }

    [Fact]
    public async Task Arm_Rejects_TraversalEscape()
    {
        var root = CreateTempDir();
        var src = new FileTailTriggerSource(new[] { root });
        var req = ArmReq(path: Path.Combine(root, "..", "escape.log"));
        var act = () => src.ArmAsync(req, NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Fire_WhenMatchingLineAppended()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "app.log");
        await File.WriteAllTextAsync(file, "");
        var src = new FileTailTriggerSource(new[] { root });
        var fired = new TaskCompletionSource<TriggerFireEvent>();
        var sink = SinkThatCompletes(fired);

        await using var handle = await src.ArmAsync(ArmReq(path: file, pattern: "ERROR"), sink, CancellationToken.None);
        await File.AppendAllTextAsync(file, "INFO ok\nERROR boom\n");

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("ERROR boom");
    }

    [Fact]
    public async Task Fire_Payload_EscapesInjectionAttempts()
    {
        // A log line containing "</trigger>" or fake instructions must be neutralized in Payload.
        // Assert the raw closing tag does not survive verbatim.
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (class doesn't exist).

- [ ] **Step 3: Implement `FileTailTriggerSource`** (model the yield-before-fire + per-arm handle pattern on `TimerTriggerSource`):

```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Notify/block source that fires when a new line appended to an allowed file matches an optional
/// regex. Path is canonicalized against host-supplied allowed roots at arm time; anything outside
/// them (or a symlink escape) is an arm-time rejection. Not restorable (a file offset can't be
/// trusted across a restart) — a restored file_tail resolves <c>trigger_lost_on_restart</c>.
/// </summary>
public sealed class FileTailTriggerSource : ITriggerSource
{
    public const string KindName = "file_tail";

    public const string ArgsSchemaText =
        "{ path: \"<absolute path under an allowed root>\", pattern?: \"<regex; matches trigger a fire>\" }";

    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    private const int MaxLineBytes = 4096;
    private const int MaxLinesPerBatch = 20;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    private readonly List<string> _allowedRoots;

    public FileTailTriggerSource(IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _allowedRoots = allowedRoots
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .ToList();
    }

    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var (path, pattern) = ParseArgs(request.ArgsJson);
        var canonical = CanonicalizeAndValidate(path); // throws ArgumentException on escape

        Regex? regex = null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.NonBacktracking, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"file_tail 'pattern' is not a valid regex: {ex.Message}", ex);
            }
        }

        var handle = new FileTailArmedTrigger(canonical, regex, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    private string CanonicalizeAndValidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("file_tail 'path' must be an absolute path.");
        }

        var full = Path.GetFullPath(path);
        // Resolve symlinks to their real target so a symlinked file inside a root that points out
        // is rejected. (File may not exist yet; resolve the directory that does.)
        var real = ResolveRealPath(full);

        var inRoot = _allowedRoots.Any(root =>
            real.Equals(root, StringComparison.OrdinalIgnoreCase)
            || real.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        if (!inRoot)
        {
            throw new ArgumentException("file_tail 'path' is outside the allowed roots.");
        }
        return real;
    }

    private static string ResolveRealPath(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (info.Exists && info.LinkTarget != null)
            {
                return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full);
            }
            var dir = info.Directory;
            if (dir != null && dir.Exists && dir.LinkTarget != null)
            {
                var realDir = dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName;
                return Path.GetFullPath(Path.Combine(realDir, info.Name));
            }
        }
        catch (IOException)
        {
            // Fall through to the lexical path — still validated against roots below.
        }
        return full;
    }

    private static (string Path, string? Pattern) ParseArgs(string argsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("file_tail args must be a JSON object.");
        }
        var path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var pattern = root.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.String ? pat.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("file_tail requires a 'path'.");
        }
        return (path!, pattern);
    }

    /// <summary>Neutralizes control tokens so file content can't break out of the envelope.</summary>
    internal static string Redact(string line)
    {
        var trimmed = line.Length > MaxLineBytes ? line[..MaxLineBytes] : line;
        return trimmed.Replace("<trigger>", "<​trigger>", StringComparison.OrdinalIgnoreCase)
                      .Replace("</trigger>", "<​/trigger>", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FileTailArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _tailTask;
        private int _disposed;

        public FileTailArmedTrigger(string path, Regex? regex, ITriggerEventSink sink)
        {
            WaitId = path; // internal only; runtime keys by its own WaitId
            _tailTask = RunAsync(path, regex, sink, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(string path, Regex? regex, ITriggerEventSink sink, CancellationToken ct)
        {
            await Task.Yield();
            // Open at end-of-file; poll appended lines with a debounce. (FileSystemWatcher may be
            // used instead; polling keeps the sample deterministic and cross-platform for tests.)
            long offset = File.Exists(path) ? new FileInfo(path).Length : 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DebounceWindow, ct);
                }
                catch (OperationCanceledException) { return; }

                if (!File.Exists(path)) { continue; }
                var len = new FileInfo(path).Length;
                if (len <= offset) { if (len < offset) { offset = 0; } continue; }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                var batch = 0;
                string? line;
                while (batch < MaxLinesPerBatch && (line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (regex == null || SafeMatch(regex, line))
                    {
                        await sink.FireAsync(new TriggerFireEvent { Payload = Redact(line) }, ct);
                        batch++;
                    }
                }
                offset = fs.Position;
            }
        }

        private static bool SafeMatch(Regex regex, string line)
        {
            try { return regex.IsMatch(line); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            await _cts.CancelAsync();
            _ = _tailTask.ContinueWith(_ => { try { _cts.Dispose(); } catch (ObjectDisposedException) { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
```

> **Verify the `TriggerFireEvent` shape** before writing: confirm `Payload` is a settable `string?` property (`src/LmMultiTurn/Triggers/TriggerFireEvent.cs`). If it's a positional record, use `new TriggerFireEvent(Redact(line))` instead.

- [ ] **Step 4: Register it** — in `SampleTriggerRegistrations.Build`, add before the return (unconditional):

```csharp
var fileTailRoots = new[] { Path.Combine(Path.GetTempPath(), "lmstreaming-tails") };
registrations.Add(new TriggerSourceRegistration
{
    Kind = FileTailTriggerSource.KindName,
    Description = "Fire when a matching line is appended to an allowed log file.",
    ArgsSchema = FileTailTriggerSource.ArgsSchemaText,
    Capabilities = FileTailTriggerSource.Capabilities,
    Source = new FileTailTriggerSource(fileTailRoots),
});
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~FileTailTriggerSourceTests"`

- [ ] **Step 6: Commit**

```bash
git add samples/LmStreaming.Sample/Triggers/FileTailTriggerSource.cs samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs tests/LmStreaming.Sample.Tests/Triggers/FileTailTriggerSourceTests.cs
git commit -m "feat(sample): file_tail trigger source with path confinement (#141)"
```

---

## Phase 5 — #142 ProcessTriggerSource

### Task 9: `ProcessTriggerSource` (sample-app, Sandbox-gated)

**Files:**
- Create: `samples/LmStreaming.Sample/Triggers/ProcessTriggerSource.cs`
- Modify: `samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs`
- Test: `tests/LmStreaming.Sample.Tests/Triggers/ProcessTriggerSourceTests.cs`

**Interfaces:**
- Produces: `sealed class ProcessTriggerSource(...) : ITriggerSource`, `KindName = "process"`, `Capabilities = (SupportsBlock: true, SupportsNotify: true, SupportsRestore: false)`; observes an exit event and applies an exit-code / stdout-regex predicate. **Execution policy stays in the Bash tool** — this source only observes; it does not spawn arbitrary commands itself outside the existing confinement.

- [ ] **Step 1: Write the failing tests**

```csharp
public class ProcessTriggerSourceTests
{
    [Fact]
    public async Task Fire_WhenObservedProcessExitsWithMatchingCode()
    {
        // Inject a fake "process observer" that lets the test signal exit(code, stdout).
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>();
        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""), SinkThatCompletes(fired), CancellationToken.None);

        observer.SignalExit("h1", exitCode: 0, stdout: "ok");

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("\"exitCode\":0");
    }

    [Fact]
    public async Task NoFire_WhenExitCodePredicateFails()
    {
        // exit(1) with expectExitCode:0 → no fire before disposal.
    }

    [Fact]
    public void Registration_OmittedWhenSandboxDisabled()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: false);
        options.AdditionalRegistrations.Should().NotContain(r => r.Kind == ProcessTriggerSource.KindName);
    }

    [Fact]
    public void Registration_PresentWhenSandboxEnabled()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true);
        options.AdditionalRegistrations.Should().Contain(r => r.Kind == ProcessTriggerSource.KindName);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**.

- [ ] **Step 3: Implement `ProcessTriggerSource`** with an injectable observer abstraction (so the source only *observes* exit events; the actual command lifecycle stays in the Bash-tool infra):

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>Abstraction over the Bash-tool process registry: lets a trigger await one process's exit
/// without owning its spawn/kill lifecycle (that stays in the Bash tool's confinement).</summary>
public interface IProcessExitObserver
{
    /// <summary>Completes when the process identified by <paramref name="handle"/> exits.</summary>
    Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct);
}

public readonly record struct ProcessExit(int ExitCode, string Stdout);

/// <summary>
/// Notify/block source that fires when a Bash-tool-managed process exits and matches an
/// exit-code / stdout-regex predicate. Registered only when Sandbox is enabled. Not restorable —
/// a process can't be resumed across a restart.
/// </summary>
public sealed class ProcessTriggerSource : ITriggerSource
{
    public const string KindName = "process";

    public const string ArgsSchemaText =
        "{ handle: \"<bash process handle>\", expectExitCode?: <int>, stdoutPattern?: \"<regex>\" }";

    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly IProcessExitObserver _observer;

    public ProcessTriggerSource(IProcessExitObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observer = observer;
    }

    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var (handle, expectCode, stdoutPattern) = ParseArgs(request.ArgsJson);
        Regex? regex = string.IsNullOrEmpty(stdoutPattern)
            ? null
            : new Regex(stdoutPattern, RegexOptions.NonBacktracking, MatchTimeout);

        var armed = new ProcessArmedTrigger(request.WaitId, handle, expectCode, regex, _observer, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(armed);
    }

    private static (string Handle, int? ExpectCode, string? StdoutPattern) ParseArgs(string argsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("process args must be a JSON object."); }
        var handle = root.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        if (string.IsNullOrWhiteSpace(handle)) { throw new ArgumentException("process requires a 'handle'."); }
        int? expect = root.TryGetProperty("expectExitCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
        var pattern = root.TryGetProperty("stdoutPattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return (handle!, expect, pattern);
    }

    private sealed class ProcessArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _watch;
        private int _disposed;

        public ProcessArmedTrigger(string waitId, string handle, int? expectCode, Regex? regex,
            IProcessExitObserver observer, ITriggerEventSink sink)
        {
            WaitId = waitId;
            _watch = RunAsync(handle, expectCode, regex, observer, sink, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(string handle, int? expectCode, Regex? regex,
            IProcessExitObserver observer, ITriggerEventSink sink, CancellationToken ct)
        {
            await Task.Yield();
            ProcessExit exit;
            try { exit = await observer.WaitForExitAsync(handle, ct); }
            catch (OperationCanceledException) { return; }

            var codeOk = expectCode is null || exit.ExitCode == expectCode.Value;
            var stdoutOk = regex is null || SafeMatch(regex, exit.Stdout);
            if (codeOk && stdoutOk)
            {
                var payload = JsonSerializer.Serialize(new { handle, exitCode = exit.ExitCode, stdout = exit.Stdout });
                await sink.FireAsync(new TriggerFireEvent { Payload = payload }, ct);
            }
        }

        private static bool SafeMatch(Regex regex, string s)
        {
            try { return regex.IsMatch(s); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            await _cts.CancelAsync();
            _ = _watch.ContinueWith(_ => { try { _cts.Dispose(); } catch (ObjectDisposedException) { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
```

- [ ] **Step 4: Register it (Sandbox-gated)** — in `SampleTriggerRegistrations.Build`:

```csharp
if (sandboxEnabled)
{
    registrations.Add(new TriggerSourceRegistration
    {
        Kind = ProcessTriggerSource.KindName,
        Description = "Fire when a sandbox process exits with a matching exit code / stdout.",
        ArgsSchema = ProcessTriggerSource.ArgsSchemaText,
        Capabilities = ProcessTriggerSource.Capabilities,
        Source = new ProcessTriggerSource(/* IProcessExitObserver from the Bash-tool infra */ NoopProcessObserver.Instance),
    });
}
```

> Wire a real `IProcessExitObserver` over the Bash-tool process registry if one is exposed; otherwise register a `NoopProcessObserver` placeholder (documented as "process kind requires a Bash-tool exit observer") so the kind advertises but never fires until the observer is wired. Decide this when the Bash-tool infra is confirmed — do NOT duplicate execution policy here.

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~ProcessTriggerSourceTests"`

- [ ] **Step 6: Commit**

```bash
git add samples/LmStreaming.Sample/Triggers/ProcessTriggerSource.cs samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs tests/LmStreaming.Sample.Tests/Triggers/ProcessTriggerSourceTests.cs
git commit -m "feat(sample): process trigger source observing bash-tool exits (#142)"
```

---

## Phase 6 — #143 schedule kind

### Task 10: `ScheduleTriggerSource` (cron + interval, sample-app)

**Files:**
- Modify: `samples/LmStreaming.Sample/LmStreaming.Sample.csproj` (add `Cronos`)
- Create: `samples/LmStreaming.Sample/Triggers/ScheduleTriggerSource.cs`
- Modify: `samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs`
- Test: `tests/LmStreaming.Sample.Tests/Triggers/ScheduleTriggerSourceTests.cs`

**Interfaces:**
- Produces: `sealed class ScheduleTriggerSource : ITriggerSource`, `KindName = "schedule"`, `Capabilities = (SupportsBlock: true, SupportsNotify: true, SupportsRestore: true)`; args `{ cron?: string, intervalSeconds?: int }` (exactly one). Block mode resolves on the first fire; notify mode repeats per #140's multi-fire lifecycle.

- [ ] **Step 1: Add the Cronos package reference**

In `LmStreaming.Sample.csproj`, inside the first `<ItemGroup>` of `PackageReference`s, add:

```xml
    <PackageReference Include="Cronos" Version="0.11.0" />
```

Run `dotnet restore samples/LmStreaming.Sample/LmStreaming.Sample.csproj` and confirm it resolves.

- [ ] **Step 2: Write the failing tests**

```csharp
public class ScheduleTriggerSourceTests
{
    [Fact]
    public async Task Interval_FiresRepeatedly()
    {
        var src = new ScheduleTriggerSource();
        var fires = 0;
        var sink = SinkCounting(() => Interlocked.Increment(ref fires));
        await using var handle = await src.ArmAsync(
            ArmReq("""{"intervalSeconds":1}"""), sink, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        fires.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Cron_FiresOnSchedule()
    {
        var src = new ScheduleTriggerSource();
        var fired = new TaskCompletionSource();
        var sink = SinkThatSignals(fired);
        // "* * * * * *" every second (Cronos with seconds enabled)
        await using var handle = await src.ArmAsync(ArmReq("""{"cron":"* * * * * *"}"""), sink, CancellationToken.None);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Arm_Rejects_WhenBothCronAndInterval()
    {
        var src = new ScheduleTriggerSource();
        var act = () => src.ArmAsync(ArmReq("""{"cron":"* * * * *","intervalSeconds":5}"""), NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Arm_Rejects_IntervalBelowFloor()
    {
        var src = new ScheduleTriggerSource();
        var act = () => src.ArmAsync(ArmReq("""{"intervalSeconds":0}"""), NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
```

- [ ] **Step 3: Run — expect FAIL**.

- [ ] **Step 4: Implement `ScheduleTriggerSource`**

```csharp
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
    public const string KindName = "schedule";

    public const string ArgsSchemaText =
        "{ cron?: \"<cron expr, 5 or 6 fields>\", intervalSeconds?: <int ≥ 1> } — supply exactly one.";

    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: true);

    private const int MinIntervalSeconds = 1;

    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
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
            try { expr = CronExpression.Parse(cron, format); }
            catch (CronFormatException ex) { throw new ArgumentException($"schedule 'cron' is invalid: {ex.Message}", ex); }
        }
        else if (intervalSeconds!.Value < MinIntervalSeconds)
        {
            throw new ArgumentException($"schedule 'intervalSeconds' must be ≥ {MinIntervalSeconds}.");
        }

        var handle = new ScheduleArmedTrigger(request.WaitId, expr, intervalSeconds, request.ArmedAt, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    private static (string? Cron, int? IntervalSeconds) ParseArgs(string argsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("schedule args must be a JSON object."); }
        var cron = root.TryGetProperty("cron", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        int? interval = root.TryGetProperty("intervalSeconds", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        return (cron, interval);
    }

    private sealed class ScheduleArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _disposed;

        public ScheduleArmedTrigger(string waitId, CronExpression? expr, int? intervalSeconds,
            DateTimeOffset armedAt, ITriggerEventSink sink)
        {
            WaitId = waitId;
            _loop = RunAsync(expr, intervalSeconds, armedAt, sink, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(CronExpression? expr, int? intervalSeconds,
            DateTimeOffset armedAt, ITriggerEventSink sink, CancellationToken ct)
        {
            await Task.Yield();
            var last = armedAt;
            while (!ct.IsCancellationRequested)
            {
                DateTimeOffset next;
                if (expr != null)
                {
                    var n = expr.GetNextOccurrence(last.UtcDateTime, TimeZoneInfo.Utc);
                    if (n is null) { return; } // no further occurrences
                    next = new DateTimeOffset(n.Value, TimeSpan.Zero);
                }
                else
                {
                    next = last.AddSeconds(intervalSeconds!.Value);
                }

                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { return; }
                }

                await sink.FireAsync(new TriggerFireEvent { Payload = next.ToString("o") }, ct);
                last = next;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            await _cts.CancelAsync();
            _ = _loop.ContinueWith(_ => { try { _cts.Dispose(); } catch (ObjectDisposedException) { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
```

- [ ] **Step 5: Register it** — in `SampleTriggerRegistrations.Build` (unconditional):

```csharp
registrations.Add(new TriggerSourceRegistration
{
    Kind = ScheduleTriggerSource.KindName,
    Description = "Fire on a cron expression or a fixed interval (block resolves once; notify repeats).",
    ArgsSchema = ScheduleTriggerSource.ArgsSchemaText,
    Capabilities = ScheduleTriggerSource.Capabilities,
    Source = new ScheduleTriggerSource(),
});
```

- [ ] **Step 6: Run — expect PASS**

Run: `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~ScheduleTriggerSourceTests"`

- [ ] **Step 7: Commit**

```bash
git add samples/LmStreaming.Sample/LmStreaming.Sample.csproj samples/LmStreaming.Sample/Triggers/ScheduleTriggerSource.cs samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs tests/LmStreaming.Sample.Tests/Triggers/ScheduleTriggerSourceTests.cs
git commit -m "feat(sample): schedule trigger source (cron + interval via Cronos) (#143)"
```

---

## Phase 7 — #144 sample source

### Task 11: `SubAgentCompletionTriggerSource` (sample-app)

**Files:**
- Create: `samples/LmStreaming.Sample/Triggers/SubAgentCompletionTriggerSource.cs`
- Modify: `samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs`
- Test: `tests/LmStreaming.Sample.Tests/Triggers/SubAgentCompletionTriggerSourceTests.cs`

**Interfaces:**
- Consumes: `SubAgentManager.ObserveCompletionAsync` + `SetNotifyParentOnCompletion` (Task 6).
- Produces: `sealed class SubAgentCompletionTriggerSource(SubAgentManager manager) : ITriggerSource`, `KindName = "subagent"`, `Capabilities = (SupportsBlock: true, SupportsNotify: true, SupportsRestore: false)`; args `{ agentId: string }`; flips `NotifyParentOnCompletion=false` at arm, **restores it to true on dispose if the sub-agent hasn't completed** (avoids stranding the result).

- [ ] **Step 1: Write the failing tests**

```csharp
public class SubAgentCompletionTriggerSourceTests
{
    [Fact]
    public async Task Fire_WhenSubAgentCompletes_AndSuppressesRelay()
    {
        var (manager, agentId) = await SpawnCompletingSubAgentAsync(result: "sub-done");
        var src = new SubAgentCompletionTriggerSource(manager);
        var fired = new TaskCompletionSource<TriggerFireEvent>();

        await using var handle = await src.ArmAsync(ArmReq($$"""{"agentId":"{{agentId}}"}"""), SinkThatCompletes(fired), CancellationToken.None);

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("sub-done");
        // Relay was suppressed: the flag was flipped false at arm.
        manager.PeekNotifyParentOnCompletion(agentId).Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_BeforeCompletion_RestoresRelayFlag()
    {
        var (manager, agentId) = await SpawnNeverCompletingSubAgentAsync();
        var src = new SubAgentCompletionTriggerSource(manager);
        var handle = await src.ArmAsync(ArmReq($$"""{"agentId":"{{agentId}}"}"""), NoopSink, CancellationToken.None);

        await handle.DisposeAsync(); // simulates CancelWait / timeout before completion

        manager.PeekNotifyParentOnCompletion(agentId).Should().BeTrue(); // restored — result not stranded
    }
}
```

> These tests need a read accessor for the flag. If none exists, add a tiny `internal bool PeekNotifyParentOnCompletion(string agentId)` to `SubAgentManager` (guarded like the others) in Task 6's file, or assert via observed relay behavior instead. Prefer asserting behavior (relay happened / didn't) if adding a peek accessor feels like test-only surface.

- [ ] **Step 2: Run — expect FAIL**.

- [ ] **Step 3: Implement `SubAgentCompletionTriggerSource`**

```csharp
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Fires when a specific background sub-agent completes. Flips that sub-agent's
/// <c>NotifyParentOnCompletion</c> to false at arm time so the completion arrives once (via the
/// trigger envelope), not twice. Restores the flag on dispose if the sub-agent hasn't yet
/// completed — otherwise a cancel/timeout before completion would strand the result (no trigger
/// fire and no automatic relay). Not restorable: an in-process Task can't survive a restart.
/// </summary>
public sealed class SubAgentCompletionTriggerSource : ITriggerSource
{
    public const string KindName = "subagent";
    public const string ArgsSchemaText = "{ agentId: \"<id of a spawned sub-agent>\" }";

    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    private readonly SubAgentManager _manager;

    public SubAgentCompletionTriggerSource(SubAgentManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var agentId = ParseAgentId(request.ArgsJson);
        // Throws ArgumentException (arm-time rejection) if the id is unknown.
        _manager.SetNotifyParentOnCompletion(agentId, false);

        var handle = new SubAgentArmedTrigger(request.WaitId, agentId, _manager, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    private static string ParseAgentId(string argsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("subagent args must be a JSON object."); }
        var id = root.TryGetProperty("agentId", out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) { throw new ArgumentException("subagent requires an 'agentId'."); }
        return id!;
    }

    private sealed class SubAgentArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SubAgentManager _manager;
        private readonly string _agentId;
        private readonly Task _watch;
        private int _completed; // 1 once the sub-agent completed (relay stays suppressed).
        private int _disposed;

        public SubAgentArmedTrigger(string waitId, string agentId, SubAgentManager manager, ITriggerEventSink sink)
        {
            WaitId = waitId;
            _agentId = agentId;
            _manager = manager;
            _watch = RunAsync(sink, _cts.Token);
        }

        public string WaitId { get; }

        private async Task RunAsync(ITriggerEventSink sink, CancellationToken ct)
        {
            await Task.Yield();
            string result;
            try
            {
                result = await _manager.ObserveCompletionAsync(_agentId, ct);
            }
            catch (OperationCanceledException)
            {
                return; // cancelled before completion — dispose restores the flag.
            }
            catch (SubAgentExecutionException ex)
            {
                Interlocked.Exchange(ref _completed, 1);
                var errPayload = JsonSerializer.Serialize(new { agentId = _agentId, error = ex.Message });
                await sink.FireAsync(new TriggerFireEvent { Payload = errPayload }, ct);
                return;
            }

            Interlocked.Exchange(ref _completed, 1);
            var payload = JsonSerializer.Serialize(new { agentId = _agentId, result });
            await sink.FireAsync(new TriggerFireEvent { Payload = payload }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            await _cts.CancelAsync();

            // If the sub-agent never completed under this wait, restore automatic relay so its
            // eventual result is not stranded. If it already completed, the trigger delivered it —
            // leave the flag suppressed.
            if (Volatile.Read(ref _completed) == 0)
            {
                try { _manager.SetNotifyParentOnCompletion(_agentId, true); }
                catch (ArgumentException) { /* sub-agent already gone — nothing to restore. */ }
            }

            _ = _watch.ContinueWith(_ => { try { _cts.Dispose(); } catch (ObjectDisposedException) { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
```

- [ ] **Step 4: Expose the loop's `SubAgentManager` + register a lazy source**

Confirmed constraint: `MultiTurnAgentLoop._subAgentManager` is **private, built inside the ctor** (`MultiTurnAgentLoop.cs:125`), so the sample can't hand the manager to a source at registration time (the manager doesn't exist until the loop is constructed, and the loop consumes `AdditionalRegistrations` *inside* that same ctor). Core also can't reference the sample-side source. Resolve with a **lazy Func-resolving wrapper** registered before construction, plus one minimal core read accessor:

**Core seam — add a read accessor** to `MultiTurnAgentLoop.cs` (beside the `_subAgentManager` field, ~L39):

```csharp
/// <summary>The sub-agent manager for this loop, or null when no sub-agent options were supplied.
/// Exposed so a host-side trigger source (e.g. the sample's subagent-completion source) can observe
/// sub-agent completions; the manager itself is still owned and disposed by the loop.</summary>
public SubAgentManager? SubAgentManager => _subAgentManager;
```

**Sample — make `SubAgentCompletionTriggerSource` resolve its manager lazily.** Change its ctor to take `Func<SubAgentManager?>` instead of `SubAgentManager`, and resolve (throwing an arm-time rejection if still null) at the top of `ArmAsync`:

```csharp
private readonly Func<SubAgentManager?> _managerAccessor;

public SubAgentCompletionTriggerSource(Func<SubAgentManager?> managerAccessor)
{
    ArgumentNullException.ThrowIfNull(managerAccessor);
    _managerAccessor = managerAccessor;
}

// inside ArmAsync, first line after the null checks:
var manager = _managerAccessor()
    ?? throw new ArgumentException("subagent waits require a sub-agent-enabled conversation.");
```

(Adjust the tests from Step 1 to pass `() => manager`.)

**Wire it in `Program.cs`** — the loop variable exists after construction, so capture it in the closure. `SampleTriggerRegistrations.Build` gains an optional `Func<SubAgentManager?>? subAgentManagerAccessor` param; when non-null it appends the `subagent` registration:

```csharp
if (subAgentManagerAccessor != null)
{
    registrations.Add(new TriggerSourceRegistration
    {
        Kind = SubAgentCompletionTriggerSource.KindName,
        Description = "Fire when a specific spawned sub-agent completes.",
        ArgsSchema = SubAgentCompletionTriggerSource.ArgsSchemaText,
        Capabilities = SubAgentCompletionTriggerSource.Capabilities,
        Source = new SubAgentCompletionTriggerSource(subAgentManagerAccessor),
    });
}
```

In `Program.cs`, declare the loop with a forward-captured accessor so the closure reads the just-built loop:

```csharp
MultiTurnAgentLoop agent = null!;
var triggerOptions = SampleTriggerRegistrations.Build(
    sandboxEnabled: sandboxSession is not null,
    subAgentManagerAccessor: () => agent?.SubAgentManager);
agent = new MultiTurnAgentLoop( /* ...existing args... */, triggerOptions: triggerOptions);
```

> This supersedes Task 7's simpler `triggerOptions:` line — update that call site to the two-step form above.

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~SubAgentCompletionTriggerSourceTests"`

- [ ] **Step 6: Commit**

```bash
git add samples/LmStreaming.Sample/Triggers/SubAgentCompletionTriggerSource.cs samples/LmStreaming.Sample/Triggers/SampleTriggerRegistrations.cs tests/LmStreaming.Sample.Tests/Triggers/SubAgentCompletionTriggerSourceTests.cs
git commit -m "feat(sample): subagent-completion trigger source with relay-flag restore (#144)"
```

---

## Phase 8 — #145 durable notify-watcher restore (core)

### Task 12: `INotifyWaitStore` + SQLite schema + `SqliteNotifyWaitStore`

**Files:**
- Create: `src/LmMultiTurn/Persistence/INotifyWaitStore.cs`
- Modify: `src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs`
- Create: `src/LmMultiTurn/Persistence/Sqlite/SqliteNotifyWaitStore.cs`
- Test: `tests/LmMultiTurn.Tests/Persistence/SqliteNotifyWaitStoreTests.cs`

**Interfaces:**
- Produces:
  - `record NotifyWaitRecord(string WaitId, string ThreadId, string Kind, string Args, string? Label, int? MaxFires, int FiresSoFar, long TimeoutAtUnixMs, long ArmedAtUnixMs, string Status);`
  - `interface INotifyWaitStore { Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default); Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default); Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default); }`
  - `SqliteNotifyWaitStore(ISqliteConnectionFactory factory) : INotifyWaitStore`.

- [ ] **Step 1: Write the failing tests** (round-trip + thread scoping):

```csharp
public class SqliteNotifyWaitStoreTests
{
    [Fact]
    public async Task Save_Then_LoadActive_ReturnsRow_ScopedByThread()
    {
        await using var factory = InMemorySqliteFactory(); // shared-cache in-memory factory
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);

        var rec = new NotifyWaitRecord("w1", "threadA", "schedule", "{}", "hourly", 3, 0, 0, 0, "active");
        await store.SaveAsync(rec);

        (await store.LoadActiveAsync("threadA")).Should().ContainSingle(r => r.WaitId == "w1");
        (await store.LoadActiveAsync("threadB")).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        await using var factory = InMemorySqliteFactory();
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);
        await store.SaveAsync(new NotifyWaitRecord("w1", "t", "schedule", "{}", null, null, 0, 0, 0, "active"));

        await store.DeleteAsync("t", "w1");

        (await store.LoadActiveAsync("t")).Should().BeEmpty();
    }
}
```

> Reuse whatever in-memory `ISqliteConnectionFactory` the existing `SqliteConversationStoreTests` use for `InMemorySqliteFactory` (check that file first).

- [ ] **Step 2: Run — expect FAIL**.

- [ ] **Step 3: Add the DDL to `SqliteSchemaInitializer`**

Add a new `const string` beside `CreateMetadataTableSql`:

```csharp
internal const string CreateNotifyWaitsTableSql =
    """
    CREATE TABLE IF NOT EXISTS notify_waits (
        wait_id       TEXT NOT NULL,
        thread_id     TEXT NOT NULL,
        kind          TEXT NOT NULL,
        args          TEXT NOT NULL,
        label         TEXT NULL,
        max_fires     INTEGER NULL,
        fires_so_far  INTEGER NOT NULL DEFAULT 0,
        timeout_at    INTEGER NOT NULL,
        armed_at      INTEGER NOT NULL,
        status        TEXT NOT NULL,
        PRIMARY KEY (thread_id, wait_id)
    );
    """;

internal const string CreateNotifyWaitsIndexSql =
    "CREATE INDEX IF NOT EXISTS ix_notify_waits_thread ON notify_waits (thread_id);";
```

In BOTH `InitializeSchemaAsync` overloads' transaction, execute the new DDL alongside the existing three statements (add two more `command.CommandText = ...; await command.ExecuteNonQueryAsync(...)` blocks, or append to the executed set, following the existing pattern exactly).

- [ ] **Step 4: Create `INotifyWaitStore.cs`**

```csharp
namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// One persisted live notify-watcher arming record. Notify waits have no deferred tool_call_id to
/// reuse as their arming record (unlike block waits), so they persist here to survive a restart.
/// </summary>
public sealed record NotifyWaitRecord(
    string WaitId,
    string ThreadId,
    string Kind,
    string Args,
    string? Label,
    int? MaxFires,
    int FiresSoFar,
    long TimeoutAtUnixMs,
    long ArmedAtUnixMs,
    string Status);

/// <summary>
/// Durable store for live notify-mode waits. Separate from <see cref="IConversationStore"/> on
/// purpose: notify waits are not message history, and forcing every conversation store to
/// implement wait persistence would be a leaky abstraction. Only a host that configures durable
/// notify restore wires an implementation.
/// </summary>
public interface INotifyWaitStore
{
    Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default);

    Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default);

    Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default);
}
```

- [ ] **Step 5: Create `SqliteNotifyWaitStore.cs`** (model the ctor/connection pattern on `SqliteConversationStore`):

```csharp
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>SQLite-backed <see cref="INotifyWaitStore"/>. Upserts by (thread_id, wait_id); deletes on terminal.</summary>
public sealed class SqliteNotifyWaitStore : INotifyWaitStore
{
    private readonly ISqliteConnectionFactory _factory;

    public SqliteNotifyWaitStore(ISqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var conn = await _factory.GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO notify_waits
                (wait_id, thread_id, kind, args, label, max_fires, fires_so_far, timeout_at, armed_at, status)
            VALUES ($id, $thread, $kind, $args, $label, $max, $fires, $timeout, $armed, $status)
            ON CONFLICT(thread_id, wait_id) DO UPDATE SET
                fires_so_far = excluded.fires_so_far,
                status = excluded.status;
            """;
        cmd.Parameters.AddWithValue("$id", record.WaitId);
        cmd.Parameters.AddWithValue("$thread", record.ThreadId);
        cmd.Parameters.AddWithValue("$kind", record.Kind);
        cmd.Parameters.AddWithValue("$args", record.Args);
        cmd.Parameters.AddWithValue("$label", (object?)record.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$max", (object?)record.MaxFires ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fires", record.FiresSoFar);
        cmd.Parameters.AddWithValue("$timeout", record.TimeoutAtUnixMs);
        cmd.Parameters.AddWithValue("$armed", record.ArmedAtUnixMs);
        cmd.Parameters.AddWithValue("$status", record.Status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default)
    {
        var conn = await _factory.GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notify_waits WHERE thread_id = $thread AND wait_id = $id;";
        cmd.Parameters.AddWithValue("$thread", threadId);
        cmd.Parameters.AddWithValue("$id", waitId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default)
    {
        var conn = await _factory.GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT wait_id, thread_id, kind, args, label, max_fires, fires_so_far, timeout_at, armed_at, status
            FROM notify_waits WHERE thread_id = $thread AND status = 'active';
            """;
        cmd.Parameters.AddWithValue("$thread", threadId);

        var results = new List<NotifyWaitRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new NotifyWaitRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9)));
        }
        return results;
    }
}
```

- [ ] **Step 6: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~SqliteNotifyWaitStoreTests"`

- [ ] **Step 7: Commit**

```bash
git add src/LmMultiTurn/Persistence/INotifyWaitStore.cs src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs src/LmMultiTurn/Persistence/Sqlite/SqliteNotifyWaitStore.cs tests/LmMultiTurn.Tests/Persistence/SqliteNotifyWaitStoreTests.cs
git commit -m "feat(persistence): SQLite notify-wait store + schema (#145)"
```

---

### Task 13: `TriggerRuntime` persist-on-arm / delete-on-terminal + `RestoreNotifyWaitsAsync`

**Files:**
- Modify: `src/LmMultiTurn/Triggers/TriggerOptions.cs` (add `INotifyWaitStore? NotifyWaitStore` + `string? ThreadId`)
- Modify: `src/LmMultiTurn/Triggers/TriggerRuntime.cs`
- Test: `tests/LmMultiTurn.Tests/Triggers/TriggerRuntimeNotifyRestoreTests.cs`

**Interfaces:**
- Consumes: `INotifyWaitStore`/`NotifyWaitRecord` (Task 12), notify lifecycle (Task 2).
- Produces: `TriggerRuntime.RestoreNotifyWaitsAsync(CancellationToken ct)` — reads active rows for the runtime's thread, re-arms restorable ones (from remaining fire budget + TTL), delivers one `trigger_lost_on_restart` envelope for non-restorable ones and deletes their rows. Notify arms persist a row; notify terminals delete it.

- [ ] **Step 1: Add store + threadId to `TriggerOptions`**

```csharp
/// <summary>When set (with <see cref="ThreadId"/>), notify-mode waits persist here so they survive
/// a restart. Null disables durable notify restore (notify waits are then process-lifetime only).</summary>
public INotifyWaitStore? NotifyWaitStore { get; init; }

/// <summary>Thread scope for <see cref="NotifyWaitStore"/> rows. Required when the store is set.</summary>
public string? ThreadId { get; init; }
```

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public async Task RestoreNotifyWaits_ReArmsRestorableRows()
{
    var store = new InMemoryNotifyWaitStore(); // simple test double implementing INotifyWaitStore
    await store.SaveAsync(new NotifyWaitRecord("w1", "t", "manual-restorable", "{}", null, null, 0,
        FutureUnixMs(minutes: 30), NowUnixMs(), "active"));

    var (rt, src, _) = BuildRuntime(store, threadId: "t", restorableManual: true);
    await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

    rt.ListWaits().Should().ContainSingle(w => w.WaitId == "w1"); // re-armed
}

[Fact]
public async Task RestoreNotifyWaits_NonRestorable_DeliversTriggerLostAndDeletesRow()
{
    var store = new InMemoryNotifyWaitStore();
    await store.SaveAsync(new NotifyWaitRecord("w2", "t", "manual", "{}", null, null, 0,
        FutureUnixMs(30), NowUnixMs(), "active")); // "manual" = SupportsRestore:false

    var (rt, _, notified) = BuildRuntime(store, threadId: "t", restorableManual: false);
    await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

    notified.Should().ContainSingle(n => n.payload.Contains("trigger_lost_on_restart"));
    (await store.LoadActiveAsync("t")).Should().BeEmpty(); // row deleted
}
```

- [ ] **Step 3: Persist on notify arm, delete on notify terminal**

In `ArmCoreAsync`, after a successful notify arm (`_waits[waitId] = wait; StartCeilingTimer(wait);`), persist when a store is configured:

```csharp
if (mode == WaitMode.Notify && _options.NotifyWaitStore != null && _options.ThreadId != null)
{
    await _options.NotifyWaitStore.SaveAsync(new NotifyWaitRecord(
        waitId, _options.ThreadId, reg.Kind, request.ArgsJson, label, maxFires, 0,
        deadline.ToUnixTimeMilliseconds(), armedAt.ToUnixTimeMilliseconds(), "active"), ct);
}
```

In `TryTeardownAsync` (Task 2), after `ReleaseGate(wait)`, delete the row for a notify wait:

```csharp
if (wait.Mode == WaitMode.Notify && _options.NotifyWaitStore != null)
{
    try { await _options.NotifyWaitStore.DeleteAsync(_options.ThreadId!, wait.WaitId, CancellationToken.None); }
    catch (Exception ex) { _logger?.LogWarning(ex, "notify-wait row delete failed for {WaitId}", wait.WaitId); }
}
```

> Optionally update `fires_so_far` on each notify fire via `SaveAsync` (upsert) so a restart resumes with the correct remaining budget. Add this in `OnSourceFiredAsync`'s notify branch after `NotifyAsync`, guarded on `_options.NotifyWaitStore != null`. Keep it best-effort (swallow+log on failure).

- [ ] **Step 4: Implement `RestoreNotifyWaitsAsync`**

```csharp
/// <summary>
/// Re-arms notify-mode waits persisted for this runtime's thread after a restart. Restorable kinds
/// re-arm from their remaining fire budget and TTL; non-restorable kinds (or unparseable rows)
/// deliver one final <c>trigger_lost_on_restart</c> envelope and are deleted. No-op when no store
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
        var lost = () => NotifyAsync(
            new ArmedWait { WaitId = row.WaitId, Kind = row.Kind, Label = row.Label, Mode = WaitMode.Notify, ArmedAt = default, Deadline = default },
            BuildFailedPayload(row.WaitId, row.Kind, row.Label, "trigger_lost_on_restart"),
            isError: false);

        if (!_registrations.TryGetValue(row.Kind, out var reg) || !reg.Capabilities.SupportsRestore || !reg.Capabilities.SupportsNotify)
        {
            await lost();
            await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
            continue;
        }

        var armedAt = row.ArmedAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(row.ArmedAtUnixMs) : DateTimeOffset.UtcNow;
        var deadline = row.TimeoutAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(row.TimeoutAtUnixMs) : armedAt;
        if (deadline <= DateTimeOffset.UtcNow)
        {
            // TTL already elapsed while offline — terminal envelope, delete.
            await NotifyAsync(
                new ArmedWait { WaitId = row.WaitId, Kind = row.Kind, Label = row.Label, Mode = WaitMode.Notify, ArmedAt = armedAt, Deadline = deadline },
                BuildTerminalPayload("timed_out", new ArmedWait { WaitId = row.WaitId, Kind = row.Kind, Label = row.Label, Mode = WaitMode.Notify, ArmedAt = armedAt, Deadline = deadline }),
                isError: false);
            await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
            continue;
        }

        var remainingMaxFires = row.MaxFires is int mf ? Math.Max(0, mf - row.FiresSoFar) : (int?)null;
        var armResult = await ArmCoreAsync(row.WaitId, reg, row.Args, row.Label, WaitMode.Notify, remainingMaxFires, armedAt, deadline, ct);
        if (!armResult.IsArmed)
        {
            await lost();
            await store.DeleteAsync(row.ThreadId, row.WaitId, ct);
        }
    }
}
```

> `BuildFailedPayload`/`BuildTerminalPayload` are existing private methods. If constructing throwaway `ArmedWait`s for `NotifyAsync` feels heavy, add a small `NotifyRawAsync(string payload, bool isError)` overload that skips the `ArmedWait` and calls `_notify` directly — cleaner. Prefer that overload.

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~TriggerRuntimeNotifyRestoreTests"`

- [ ] **Step 6: Commit**

```bash
git add src/LmMultiTurn/Triggers/TriggerOptions.cs src/LmMultiTurn/Triggers/TriggerRuntime.cs tests/LmMultiTurn.Tests/Triggers/TriggerRuntimeNotifyRestoreTests.cs
git commit -m "feat(triggers): persist + restore notify waits across restart (#145)"
```

---

### Task 14: Loop wires the notify store + calls `RestoreNotifyWaitsAsync`

**Files:**
- Modify: `src/LmMultiTurn/MultiTurnAgentLoop.cs`
- Test: `tests/LmMultiTurn.Tests/Triggers/NotifyRestoreLoopTests.cs`

**Interfaces:**
- Consumes: `TriggerRuntime.RestoreNotifyWaitsAsync` (Task 13), `OnHistoryRestoredAsync` (existing).
- Produces: after `OnHistoryRestoredAsync` reconciles block waits, the loop also invokes `_triggerRuntime.RestoreNotifyWaitsAsync(ct)` (when `_triggerRuntime != null`), so a restored thread re-arms its notify watchers.

- [ ] **Step 1: Write the failing test**

Drive a loop whose `TriggerOptions` carries a `SqliteNotifyWaitStore` (or in-memory double) pre-seeded with an active restorable notify row for the thread; construct the loop with restore enabled; assert the wait is re-armed (visible in `ListWaits`) after history restore runs.

```csharp
[Fact]
public async Task RestoredThread_ReArmsPersistedNotifyWaits()
{
    // Seed store with an active "schedule" notify row for thread "t".
    // Build a loop for thread "t" with TriggerOptions { NotifyWaitStore = store, ThreadId = "t",
    //   AdditionalRegistrations = [scheduleRegistration] }.
    // Trigger the restore path (load history) and assert the runtime re-armed the wait.
}
```

- [ ] **Step 2: Run — expect FAIL** (restore not called).

- [ ] **Step 3: Call `RestoreNotifyWaitsAsync` alongside `OnHistoryRestoredAsync`**

In `OnHistoryRestoredAsync` (MultiTurnAgentLoop.cs ~L852–944), after the existing block-wait reconcile block that calls `_triggerRuntime.ReconcileRestoredAsync(...)`, add:

```csharp
if (_triggerRuntime != null)
{
    // Notify waits aren't backed by deferred tool calls, so they restore from their own table
    // by thread — separate from the block-wait reconcile above.
    await _triggerRuntime.RestoreNotifyWaitsAsync(ct);
}
```

> Confirm the exact cancellation token in scope inside `OnHistoryRestoredAsync` (it may be a method param or `CancellationToken.None`); match the existing `ReconcileRestoredAsync` call's token.

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/LmMultiTurn.Tests/LmMultiTurn.Tests.csproj --filter "FullyQualifiedName~NotifyRestoreLoopTests"`

- [ ] **Step 5: Full suite — no regressions**

Run: `dotnet test LmDotnetTools.sln`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/LmMultiTurn/MultiTurnAgentLoop.cs tests/LmMultiTurn.Tests/Triggers/NotifyRestoreLoopTests.cs
git commit -m "feat(triggers): restore persisted notify waits on thread rehydrate (#145)"
```

---

## Final verification

- [ ] **Full solution build + test**

Run: `dotnet build LmDotnetTools.sln -bl:.logs/build.binlog`
Run: `dotnet test LmDotnetTools.sln --logger "trx;LogFileName=results.trx" --results-directory .logs/test-results`
Expected: build clean (no new warnings), all tests green.

- [ ] **CSharpier format check**

Run: `dotnet csharpier check .` (or the repo's configured invocation)
Expected: no formatting diffs. If any, run the formatter and amend.

---

## Traceability (spec → tasks)

| Spec section | Tasks |
|---|---|
| #140 notify mode (mode/maxFires args) | 1, 4 |
| #140 notify multi-fire lifecycle + terminal envelope | 2 |
| #140 QueuedInput envelope + queue-gate delivery | 3 |
| #140 ordering guarantees | 5 |
| #144 core seam (ObserveCompletionAsync + flag) | 6 |
| sample TriggerOptions wiring | 7 |
| #141 file_tail source | 8 |
| #142 process source (Sandbox-gated) | 9 |
| #143 schedule kind (cron + interval, Cronos) | 10 |
| #144 sample source (relay-flag flip + restore) | 11 |
| #145 store + schema | 12 |
| #145 runtime persist/restore | 13 |
| #145 loop restore hook | 14 |
| #146 | out of scope (parked) |
