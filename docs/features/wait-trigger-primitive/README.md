# Wait / Trigger Primitive

## What it is

A generic way for an LLM, mid multi-turn conversation, to **wait for a background or scheduled
event** and resume with a self-describing result — layered on the existing
[deferred tool execution](../deferred-tool-execution/motivation.md) park/wake core.

The model calls a single tool:

```jsonc
Wait({
  kind,      // required — a registered trigger kind (this release ships "timer")
  args,      // optional — per-kind options object
  timeout,   // required — duration ("10m", "30s") or absolute ISO-8601 time; the safety ceiling
  label,     // optional — short, self-describing; shown in ListWaits
})
CancelWait({ id?, label?, kind? })   // total + idempotent
ListWaits()                           // armed waits + registered kinds
```

`Wait` **parks the run** (returns a deferred tool result). When the event fires — or the `timeout`
ceiling is reached — the run resumes automatically and the tool's result is a small JSON payload:

```jsonc
{ "status": "fired" | "timed_out" | "cancelled" | "failed", "kind": "timer", "label": "...", ... }
```

### Built-in `timer` source

```jsonc
Wait({ kind: "timer", args: { delay: "10m" }, timeout: "11m" })          // wake me in 10 minutes
Wait({ kind: "timer", args: { deadline: "2026-07-31T12:00:00Z" }, timeout: "2026-07-31T12:05:00Z" })
Wait({ kind: "timer", timeout: "5m" })                                     // no args → fire at the timeout
```

## Design

### One extensibility seam

Adding a new wake source is "implement `ITriggerSource` + register it" — no change to the agent loop
or the tool surface. A source only **observes** its event and reports a raw fact to a runtime-owned
sink; it holds no lifecycle policy:

```csharp
public interface ITriggerSource
{
    ValueTask<IArmedTrigger> ArmAsync(TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken ct);
}

public interface ITriggerEventSink   // implemented by the runtime, bound to one wait
{
    ValueTask FireAsync(TriggerFireEvent fire, CancellationToken ct);
}

public interface IArmedTrigger : IAsyncDisposable { string WaitId { get; } }  // a cancellable handle
```

Register a host source through `TriggerOptions`:

```csharp
var options = new TriggerOptions
{
    MaxConcurrentWaits = 16,
    MaxBlockWaitDuration = TimeSpan.FromMinutes(15),
    AdditionalRegistrations =
    [
        new TriggerSourceRegistration
        {
            Kind = "my_event",
            Description = "wait for my custom event",
            ArgsSchema = "{ ... }",
            Capabilities = new TriggerCapabilities(SupportsBlock: true, SupportsNotify: false, SupportsRestore: false),
            Source = new MyTriggerSource(),
        },
    ],
};
var loop = new MultiTurnAgentLoop(providerAgent, registry, threadId, triggerOptions: options);
```

### The runtime owns the lifecycle

`TriggerRuntime` owns every state transition so behavior is identical across kinds:

```
Pending ──► Fired      (the source's event fired first)
        ├─► TimedOut   (the ceiling elapsed first)
        ├─► Cancelled  (CancelWait before any fire)
        └─► Failed     (arm error, or trigger_lost_on_restart)
```

- **Exactly one terminal transition** per wait — a single-resolution latch (an atomic
  compare-and-swap in the runtime, not in the source). A fire racing a timeout racing a cancel
  produces exactly one delivery; the losers are no-ops.
- **Ceiling timeout** is runtime-owned and applied to every wait. It fires a hair after the nominal
  deadline so a time-based source whose fire coincides with the ceiling deterministically resolves
  as `fired`, not `timed_out`.
- **Cancellation** stops the underlying source (disposes the handle → no later fire) and resolves the
  parked block wait with `cancelled`.
- **Delivery** goes through the loop's existing public `ResolveToolCallAsync` — no new loop API.

### One wait identity, one persistence model

A block wait's identity **is** its deferred `tool_call_id`. It needs no new persisted record: the
existing `IsDeferred=true` placeholder (plus `DeferredAt`) is the arming record. On restart,
`OnHistoryRestoredAsync` reconciles each recovered wait:

- a restorable source (e.g. `timer`) re-arms for its remaining delay — firing immediately if the
  instant already elapsed while offline;
- a non-restorable source resolves the wait with `trigger_lost_on_restart`.

A restored block wait is **never left hanging**.

### Safety

- Mandatory `timeout` on every wait (clamped to `MaxBlockWaitDuration`) — no leaked waits.
- Bounded concurrency (`MaxConcurrentWaits`); over-limit arms return a structured `rejected` result
  (not an exception, not a park) so the model can react.
- Source payloads are size-capped (`MaxPayloadBytes`) with a truncation marker.
- `ListWaits` exposes only identity/kind/label/timing — never raw payloads or source internals.

## Deferred to follow-ups

Notify mode (keep working + injected `<trigger>` messages), file-tail / process / cron-interval /
sub-agent-completion sources, durable notify-watcher restore, and any shared background-work base
class are intentionally out of this first PR. The seam is designed so each is a drop-in.
