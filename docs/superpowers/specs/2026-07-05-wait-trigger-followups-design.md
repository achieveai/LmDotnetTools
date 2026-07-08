# Wait/trigger follow-ups (#140-#145): notify mode, new sources, durable restore

**Date:** 2026-07-05
**Status:** Approved via brainstorm (sections presented, LGTM'd 2026-07-05)
**Target repo:** `B:/sources/LmDotnetTools` (work-item-107 worktree; branch off `main`)
**Base:** PR #139 (issue #107), merged `01eec768`, `docs/features/wait-trigger-primitive/README.md`

## Goal

Design, as one coherent plan, the six follow-ups deferred from the #107 Wait/trigger primitive:
notify mode (#140), a `file_tail` source (#141), a `process` source (#142), cron+interval timer
capabilities (#143), a sub-agent-completion source (#144), and durable notify-watcher restore
(#145). They come out of the same work item and share dependencies (#143/#144 need #140; #145
needs #140), so they get a single combined spec rather than six independent ones. #146 (a shared
background-work base class) is explicitly parked until issue #106 produces a second concrete
consumer of that substrate — out of scope here.

## Locked decisions (from HITL + brainstorm)

1. **Notify-mode delivery is queue-only, not interrupting**: a fire injects a message through the
   loop's existing internal queue gate; it never preempts an in-flight generation.
2. **Notify-mode delivers a terminal envelope, never silence**: cancellation, expiry, and
   `trigger_lost_on_restart` all produce a final envelope like a fire does — nothing disappears
   without a message.
3. **Notify mode ships as part of #140 itself** (not deferred again) — `SupportsNotify` stops being
   a dead capability flag.
4. **Architecture pivot (mid-brainstorm): new trigger-source *implementations* are sample-app-first.**
   #141, #142, #143, and #144 all get their concrete classes in `LmStreaming.Sample`
   (`tests/LmStreaming.Sample.Tests` for their tests), registered via the existing
   `TriggerOptions.AdditionalRegistrations` door. Core (`src/LmMultiTurn`) only grows the smallest
   possible enabling seam when a source genuinely needs core-internal state. Mirrors the same
   "don't generalize until there's a second real consumer" reasoning already applied to #146.
5. **#145 persistence uses a dedicated new SQLite table**, not the existing deferred-tool-call
   history mechanism — because notify waits, unlike block waits, are never backed by a parked
   `tool_call_id`.
6. **QueuedInput gets a new notify-envelope variant**, approved as an extension of the existing
   `ResumeSentinel`/`EnqueueRawAsync` mechanism rather than new machinery.

## Current state (grounding)

- `src/LmMultiTurn/Triggers/TriggerRuntime.cs` — owns the `Pending → Fired|TimedOut|Cancelled|Failed`
  state machine; **exactly one terminal transition per wait** (CAS latch). Ceiling timer +
  grace period (`CeilingGraceMs`) let a source's own fire beat a coincident timeout.
  `ReconcileRestoredAsync` (L295-342) re-arms restorable sources on restart.
- `src/LmMultiTurn/Triggers/WaitToolProvider.cs` — the three tools (`Wait`/`CancelWait`/`ListWaits`);
  `HandleWaitAsync` (L154-190) is the one place block-mode results get built today.
- `src/LmMultiTurn/Triggers/Sources/TimerTriggerSource.cs` — the only shipped kind (`"timer"`).
  `Capabilities = (SupportsBlock: true, SupportsNotify: false, SupportsRestore: true)` — confirmed
  today's only source hard-codes notify off.
- `src/LmMultiTurn/Triggers/TriggerCapabilities.cs` — `SupportsNotify`'s doc comment: *"Reserved for
  a follow-up — no source ships notify support in this release."* Grep across
  `src/LmMultiTurn/Triggers` confirms no `MaxFires` concept exists anywhere yet.
- `docs/features/wait-trigger-primitive/README.md` — canonical design reference; explicitly lists
  notify mode, file-tail/process/cron-interval/sub-agent sources, durable notify restore, and the
  shared background-work base class as "Deferred to follow-ups."
- `src/LmMultiTurn/MultiTurnAgentLoop.cs` — `OnHistoryRestoredAsync` (L852-944) is the block-wait
  restore path; `TryScheduleAutoResume`/`EnqueueResumeSentinel` (L967-1016) →
  `EnqueueRawAsync` (`MultiTurnAgentBase.cs` L598) is the existing internal mid-run injection gate
  that #140 extends.
- `src/LmMultiTurn/Messages/ResumeSentinel.cs`, `QueuedInput.cs` — existing record types;
  `QueuedInput(UserInput Input, string ReceiptId, DateTimeOffset QueuedAt, ResumeSentinel? Resume)`.
- `src/LmMultiTurn/SubAgents/SubAgentManager.cs` — `_agents` (L29) is a `private
  ConcurrentDictionary<string, SubAgentState>`; `AwaitCompletionAsync` (L766) is `private static`,
  takes a `SubAgentState` not an id. `NotifyParentOnCompletion` (settable, `SubAgentState.cs` L52)
  and the two relay points (`SubAgentManager.cs` L718, L738, calling `SendToParentAsync`) already
  implement automatic background-completion relay today, independent of the trigger primitive.
  **No public method today lets an external caller observe a sub-agent's completion by id** — this
  is the one confirmed gap #144 needs to close in core.
- No cron/scheduling library is referenced anywhere in the solution today; no
  `Directory.Packages.props` exists (each `.csproj` declares its own `PackageReference`s).

## Design

### #140 — Notify mode (core: `src/LmMultiTurn`)

- `Wait` gains `mode: "notify" | "block"` (default `"block"`, fully backward compatible).
- A notify-mode arm does **not** park a deferred tool call. On fire, the runtime builds the same
  kind of payload block mode would've returned, wraps it as a `<trigger>`-tagged message, and
  injects it via a new `QueuedInput` notify-envelope variant through the existing
  `EnqueueRawAsync` gate — the same mechanism `ResumeSentinel` already uses for auto-resume, so no
  new loop-level plumbing is needed.
- **Lifecycle split, not a replacement of the existing state machine**: block-mode waits keep
  today's exact one-shot latch (`Pending → Fired|TimedOut|Cancelled|Failed`, zero behavior change,
  zero regression risk to what's already merged). Notify-mode waits get a second, notify-only
  lifecycle: a fire delivers its envelope and, unless `maxFires` is reached or the wait's own
  `timeout` TTL elapses, **the wait stays armed** for another fire. Termination is explicit
  `CancelWait`, `maxFires` reached, or TTL elapse — never "first fire." This is the `MaxFires`
  capability #143's interval source depends on; it is built once, here, generically — #143 adds
  no additional core surface of its own.
- Terminal envelopes are mandatory: cancellation, TTL expiry, and (for restored waits)
  `trigger_lost_on_restart` all inject a final `<trigger>` envelope, matching decision #2.
- `timeout` stays mandatory on every `Wait` (per the existing schema, clamped to
  `MaxBlockWaitDuration`), but for a notify-mode wait it means the **TTL for how long the whole
  wait stays armed**, not a ceiling on a single fire — a notify wait that never gets cancelled
  auto-terminates (with a terminal envelope) once `timeout` elapses. `maxFires` is optional and
  defaults to unlimited (bounded only by `timeout`'s TTL) when omitted.
- **Ordering**: fires that land while a generation is in flight, mid-tool-execution, or while other
  deferred calls are pending all queue behind the current turn via the existing gate — never
  interrupt. Ordering/interleaving tests cover: fire during active generation, fire during
  tool-exec, fire while another wait is deferred, and fire discovered during restore reconcile
  (which hands off to #145's reconcile path below).

### #141 — `FileTailTriggerSource` (sample-app: `LmStreaming.Sample`, zero core changes)

`ArmAsync` canonicalizes the requested path against host-supplied allowed-roots, rejects symlink
escapes and anything outside those roots as an **arm-time rejection**, not a runtime failure.
Watches via `FileSystemWatcher` with a coalescing debounce window; matches new lines against a
`Regex` compiled with `RegexOptions.NonBacktracking` and a match timeout; enforces a per-line size
cap and a lines-per-batch cap. Delivered content is escaped/redacted so file content can't break
out of the injected envelope or mimic instructions, and `ListWaits` never surfaces the raw path
(host-assigned alias only). `Capabilities(SupportsBlock: true, SupportsNotify: true,
SupportsRestore: false)` — a restored file-tail resolves `trigger_lost_on_restart`. Tests in
`tests/LmStreaming.Sample.Tests`.

### #142 — `ProcessTriggerSource` (sample-app: `LmStreaming.Sample`, zero core changes)

Registered only when Sandbox is enabled. Delegates actual execution to the existing Bash tool
infrastructure (allowlist/env/working-dir confinement/kill-tree already enforced there) — this
source only observes the exit event and applies an exit-code/stdout-regex predicate; no execution
policy is duplicated. `Capabilities(SupportsRestore: false)` — a process can't be resumed across a
restart. Tests in `tests/LmStreaming.Sample.Tests`.

### #143 — Cron + interval `"schedule"` kind (sample-app: `LmStreaming.Sample`, zero *additional*
core changes beyond #140)

A new `"schedule"` kind — deliberately not a modification of core's `"timer"` kind — covering both
cron expressions (via the **Cronos** dependency, MIT/no native deps, added to
`LmStreaming.Sample.csproj` only) and fixed-rate intervals (arm-relative, with a floor).
`Capabilities(SupportsBlock: true, SupportsNotify: true, SupportsRestore: true)`: block mode
resolves on the first fire only (same rule as timer, no special-casing needed); notify mode repeats
per #140's generic multi-fire lifecycle; restore is safe because the next-fire instant is a pure
function of the cron expression/interval plus the last-fired timestamp — the same restorability
argument `TimerTriggerSource` already relies on. Tests in `tests/LmStreaming.Sample.Tests`.

### #144 — `SubAgentCompletionTriggerSource` (sample-app: `LmStreaming.Sample`, **one minimal core
seam required**)

Per the issue text itself: *"flip the existing background sub-agent `NotifyParentOnCompletion=false`
and consume the completion latch directly so the completion arrives once via the trigger envelope,
not twice."* This is a redirect of existing behavior, not new infra:

- **Core change** (`SubAgentManager`): add one thin public method, e.g.
  `public Task<string> ObserveCompletionAsync(string agentId, CancellationToken ct)`, following the
  same `_agents.TryGetValue(agentId, out var state)` guard `Peek(string agentId)` (L333) already
  uses, then delegating to the existing completion-await logic. No new business logic — a
  visibility widening plus a lookup guard.
- **Core change** (`SubAgentManager`): at arm time, the source sets that specific sub-agent's
  `NotifyParentOnCompletion = false`, suppressing the automatic relay **only for sub-agents an
  agent has explicitly `Wait`-ed on**. Un-waited background sub-agents keep today's automatic
  relay untouched — zero behavior change for the default path.
- Sample-app class: `SubAgentCompletionTriggerSource.ArmAsync` calls the new accessor and fires the
  trigger sink on completion (or fault). `Capabilities(SupportsBlock: true, SupportsNotify: true,
  SupportsRestore: false)` — an in-process `Task` can't be resumed across a restart, so a restored
  wait resolves `trigger_lost_on_restart`.
- **Cancellation must restore the flag**: if the wait is cancelled (or times out) before the
  sub-agent completes, `NotifyParentOnCompletion` is flipped back to `true` for that sub-agent as
  part of disposing the armed trigger. Otherwise the completion signal is stranded — no trigger
  fire (wait already resolved `cancelled`) and no automatic relay either, so the sub-agent's result
  silently vanishes. This restore-on-dispose step is required, not optional.
- **Why keep it at all, given the relay already delivers results**: composability (racing
  sub-agent completion against another trigger in one `Wait`), explicit `CancelWait` control (the
  relay has no "never mind" path), and a uniform `Wait`/`CancelWait`/`ListWaits` surface instead of
  sub-agents being a special unwaited case.

This is the only one of the four sample-app-first sources needing a core touch outside the trigger
subsystem itself; #141/#142 need none, and #143 only depends on #140 (which was always core).

### #145 — Durable notify-watcher restore (core: `src/LmMultiTurn`)

Notify waits have no deferred `tool_call_id` to reuse as their arming record (unlike block waits),
so a **new dedicated SQLite table** is required:

```
wait_id, thread_id, kind, args (serialized), label,
max_fires, fires_so_far, timeout_at, armed_at, status
```

A new restore hook runs alongside (not inside) `OnHistoryRestoredAsync` — since it doesn't discover
waits by walking deferred tool calls — reading active rows by `thread_id`. For each: a restorable
source (`SupportsRestore: true`) re-arms from the persisted args and remaining fire budget/TTL; a
non-restorable source delivers one final `trigger_lost_on_restart` envelope and the row is deleted.
Rows are deleted on any terminal outcome (cancel, `maxFires` reached, TTL expiry) to prevent
unbounded growth.

### #146 — Parked (out of scope)

Shared background-work base class stays parked until issue #106 produces a second real consumer of
that substrate — revisit only then.

## Testing

- Core (`tests/LmMultiTurn.Tests/Triggers/`): notify-mode ordering (fire during generation/tool-exec
  /deferral/restore), the notify-only multi-fire lifecycle (`maxFires`, TTL expiry, terminal
  envelope on cancel), the new `SubAgentManager.ObserveCompletionAsync` accessor + the
  `NotifyParentOnCompletion` suppression (no double-relay), and #145's persistence/reconcile paths
  (restorable vs. non-restorable rows, row cleanup).
- Sample (`tests/LmStreaming.Sample.Tests`): `FileTailTriggerSource` (allowed-roots/symlink
  rejection, regex timeout, coalescing/caps, redaction), `ProcessTriggerSource` (Sandbox gating,
  exit-code/stdout predicates), `"schedule"` kind (cron parsing via Cronos, interval fixed-rate,
  restore-from-pure-function).

## Out of scope (YAGNI)

- #146's shared background-work base class (parked, see above).
- Any generalized cron/scheduling abstraction beyond the `"schedule"` kind's own needs.
- Changing today's automatic sub-agent background-completion relay for sub-agents nobody `Wait`s
  on — that path is untouched.

## Risks

- The notify-only multi-fire lifecycle is new runtime complexity in `TriggerRuntime`; scoping it
  strictly to the notify branch (block mode's one-shot latch is untouched) bounds the regression
  surface to code that doesn't exist yet.
- #144's `NotifyParentOnCompletion` flip must only apply to the specific sub-agent instance being
  waited on, not globally — a scoping bug here would silently suppress relays for unrelated
  background sub-agents. Missing the restore-on-cancel/dispose step (see #144 design) strands the
  completion signal entirely — neither a trigger fire nor a relay.
- #145's new table needs the same thread-scoping/cleanup discipline as existing persistence, or it
  leaks rows across restarts.
