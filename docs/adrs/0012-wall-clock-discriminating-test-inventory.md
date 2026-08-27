# ADR 0012: Inventory wall-clock-discriminating tests; convert the narrow-gap cases, justify the rest

* Status: Accepted
* Date: 2026-08-25
* Related issues, PRs, or commits: [#343](https://github.com/achieveai/LmDotnetTools/issues/343), building on the
  conversion template established by [#332](https://github.com/achieveai/LmDotnetTools/pull/332)

## Context

#343 observes that some CI failures attributed to "flakiness" are actually runner-capacity starvation: a
test asserts that elapsed real time falls on one side of a threshold that is supposed to separate "the
correct code path completed" from "the defective code path (a bug, a missed early-exit, a hung retry)
completed instead." On a contended CI runner, a correct, fast, in-memory operation can occasionally take
longer than the threshold for reasons that have nothing to do with the code under test — GC pauses, thread
pool starvation, a noisy neighbor container — and the test fails even though nothing is wrong. PR #332
established the fix for this class: replace the wall-clock assertion with a structural assertion (an event
count, a call count, a completion signal) that observes the same behavior without racing a clock, or —
where no such conversion exists and the margin is not actually narrow — leave the assertion in place with
that judgment recorded.

This ADR is the inventory #343 asks for: every test in `tests/` whose assertion reads elapsed wall-clock
time, classified by whether it is a genuine narrow-gap discriminator (convert), a safe-by-construction or
generously-margined check (justify, no change), or a hang-prevention ceiling that happens to use the same
API shape as a discriminator but is not one (justify, no change).

**Method.** Grepped `tests/` for `Stopwatch|\.ElapsedMilliseconds|Elapsed\.Should|BeLessThan\(TimeSpan|`
`BeGreaterThan\(TimeSpan|BeCloseTo\(TimeSpan`, which matched 23 files. Each match was read in context and
placed in one of the three buckets below. A test lands in "convert" only when (a) it is an *upper-bound*
assertion — a slow runner can only push it toward failure, never away from it — and (b) the margin between
the expected-correct duration and the threshold is small relative to plausible CI scheduling jitter (tens
of milliseconds, not hundreds or thousands). Lower-bound assertions (`BeGreaterThan`) are safe by
construction: runner slowness makes elapsed time *larger*, which can only make a lower-bound check pass
more easily, never less.

## Decision

### Converted, with mutation proof

**`FailoverEmbeddingServiceTests.GetEmbeddingAsync_AfterCooldown_ProbesPrimary` and
`GetEmbeddingAsync_ProbeFailure_ReExtendsCooldown`**
(`tests/LmEmbeddings.Tests/Core/FailoverEmbeddingServiceTests.cs`), and their exact mirrors
**`FailoverRerankServiceTests.RerankAsync_AfterCooldown_ProbesPrimary` and
`RerankAsync_ProbeFailure_ReExtendsCooldown`** (`tests/LmEmbeddings.Tests/Core/FailoverRerankServiceTests.cs`).

Root cause: `FailoverStateController` (`src/LmEmbeddings/Core/Internal/FailoverStateController.cs`) used
raw `DateTimeOffset.UtcNow` to decide whether the recovery-probe cooldown window had elapsed. All four
tests set `RecoveryInterval = TimeSpan.FromMilliseconds(50)` and then raced that 50ms window against a real
`await Task.Delay(100)` before asserting the probe fired. A 50ms margin is exactly the narrow-gap shape
#343 describes — under runner contention a 100ms delay is not reliably 50ms clear of the window.

Fix: added `public TimeProvider TimeProvider { get; init; } = TimeProvider.System;` to `FailoverOptions`
(`src/LmEmbeddings/Models/FailoverOptions.cs`) — an additive, non-breaking change, since every other caller
keeps the system clock by default (confirmed: `FailoverServiceCollectionExtensions.cs` is the only other
`new FailoverOptions` call site in `src/`, and it doesn't set the property). Threaded it through
`FailoverExecutor`'s constructor into `new FailoverStateController(options.RecoveryInterval,
options.TimeProvider)`, and replaced both `DateTimeOffset.UtcNow` call sites in `FailoverStateController`
with `_timeProvider.GetUtcNow()`. The four tests now construct a `Microsoft.Extensions.Time.Testing.
FakeTimeProvider`, pass it via `TimeProvider = timeProvider`, and replace the real `await Task.Delay(100)`
with `timeProvider.Advance(TimeSpan.FromMilliseconds(100))` — deterministic, zero wall-clock dependency,
and the test runs faster besides.

Mutation proof: widened the probe-window comparison in `ShouldUsePrimary()` to
`_timeProvider.GetUtcNow() >= _nextProbeAt.Value.Add(TimeSpan.FromDays(1))` (the probe window never opens).
`dotnet build` succeeded (not dead code). All four tests failed — the two `AfterCooldown_ProbesPrimary`
tests with a `Moq.MockException: Expected invocation ... exactly 2 times, but was 1 times`, confirming the
primary was never re-probed. Reverted the mutation by hand (restoring the original comparison, keeping the
`TimeProvider` seam); rebuilt; full `LmEmbeddings.Tests` suite green (370/370, 0 warnings).

**`RerankingServiceTests.RerankAsync_WithNonRetryableError_FailsImmediately`**
(`tests/LmEmbeddings.Tests/Core/RerankingServiceTests.cs`).

Was: `stopwatch.ElapsedMilliseconds < 100`, asserting a non-retryable 4xx response fails without incurring
`RerankingService`'s 500ms/1000ms linear retry-backoff schedule. 100ms is a narrow ceiling for an
async, HTTP-mocked call path under CI contention. Converted to a structural assertion: the fake HTTP
handler now increments an `attemptCount` on every invocation, and the test asserts `Assert.Equal(1,
attemptCount)` — directly proving "no retry happened" without timing.

Mutation proof, with a documented false start: the first mutation targeted `RerankingService`'s own
private `IsRetryableStatusCode` helper (used only to pick between calling `EnsureSuccessStatusCode()` or
throwing a bare message-only `HttpRequestException`) and separately the message-substring fallback branch
of `HttpRetryHelper.IsRetryableError`. Both mutations built clean but left the test green — because
`HttpResponseMessage.EnsureSuccessStatusCode()` populates `HttpRequestException.StatusCode`, so
`HttpRetryHelper.IsRetryableError` takes its early `exception.StatusCode is { } statusCode` branch and
calls `HttpRetryHelper.IsRetryableStatusCode` (a same-named but different method, in a different class)
before ever reaching the fallback path either mutation touched. The actual control point was one level
removed from both first guesses. Mutating `HttpRetryHelper.IsRetryableStatusCode` itself (`src/LmCore/
Http/HttpRetryHelper.cs`) to unconditionally `return true`, rebuilding, and rerunning produced the expected
red: `Assert.Equal() Failure: Expected: 1, Actual: 3` (the mock handler was invoked on all three retry
attempts). Reverted; full suite green.

### Explicitly justified — no change

* **`tests/LmMultiTurn.Tests/SubAgents/SubAgentStateLifecycleTests.cs::
  BeginTerminalDisposal_UnblocksWedgedInjectSend_ViaLifecycleTokenCancellation`** — uses
  `await x.WaitAsync(TimeSpan.FromSeconds(5))` purely as a hang-prevention ceiling around a
  signal-based assertion. It does not assert on the *elapsed* duration; a slow runner just makes the
  test take longer within the 5s budget, it does not change the pass/fail outcome. Not a discriminator.

* **`tests/LmStreaming.Sample.Tests/Triggers/FileTailTriggerSourceTests.cs::Fire_Payload_EscapesInjectionAttempts`**
  — **this entry was wrong, and #452 is the correction.** It was originally filed here alongside the
  entry above, on the same reasoning: a 5s `WaitAsync` read as a hang ceiling around a content
  assertion. That reasoning held only if the watcher was guaranteed to observe the append at all, and
  it was not. `FileTailArmedTrigger` captured its starting byte offset *after* `RunAsync`'s opening
  `await Task.Yield()`, while `ArmAsync` returned a completed `ValueTask` and so never yielded — so
  the test's append could land inside an unsynchronized window and be measured into the baseline as
  pre-existing history. Whether the runner lost that race **was** the pass/fail outcome, which is the
  definition of a discriminator this ADR set out to find. Mutation A on PR #462 demonstrates it:
  deferring the baseline read turns this test red deterministically.

  Corrected state: #452 removed the race in production (the baseline is now captured synchronously in
  the constructor, before `ArmAsync` returns), and the wait moved to `Wait.UntilAsync` — a 10s bound
  that fails loudly naming what it waited for. **Now** the bound is a genuine hang guard and not a
  discriminator, because the thing being waited for can no longer be lost. The general lesson is worth
  keeping: a timeout is only "just a ceiling" if the event it waits for is guaranteed to be *produced*.
  Classifying a wait without checking that the producer cannot drop the signal is how a real defect
  gets filed as justified.

* **`tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryPluginSelectionTests.cs`** — two
  `elapsed.Elapsed.Should().BeLessThan(...)` checks (a 500ms operation budget against a 1,100ms ceiling,
  and a zero-budget "must not wait at all" check against a 1s ceiling). Both already carry an explicit
  in-file comment stating the design intent in #332's own terms ("The bound sits between the two so
  neither a slow machine nor the bug can be mistaken for the other"). These already are the target pattern
  for a case where a genuine timing budget is the thing under test (the SUT sleeps for a configured
  duration) and no structural substitute exists; left unchanged.

* **`tests/LmMultiTurn.Tests/SubAgents/SubAgentManagerGateReleaseRegressionTests.cs`** — two
  `elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2))` checks around `DisposeAsync()`, each
  nested inside a 10-second `WaitAsync` outer ceiling. Two seconds is a wide margin for an in-process
  dispose path with no I/O; judged low practical flake risk.

* **`tests/LmWorkflow.Tests/TransitionSettlementBarrierTests.cs`** — four elapsed-time assertions. One is
  a lower bound (`BeGreaterThan(150ms)`, safe by construction). Two are upper bounds with wide margins for
  in-memory workflow routing (`BeLessThan(BarrierBudget / 2)` = 1,000ms, and `BeLessThan(500ms)` for a
  single synchronous tool call). One is a paired lower/upper bound around a 2-second `BarrierBudget`
  (`BeGreaterThan(BarrierBudget - 500ms)` and `BeLessThan(BarrierBudget + 8s)`) — an 8-second grace band on
  the upper side. None of these are narrow relative to CI scheduling jitter for in-memory work.

* **`tests/LmStreaming.Sample.Tests/Services/TranscriptFlushSchedulerTests.cs`** — the `Stopwatch` inside
  `KeyScheduledAsTheDrainIsExiting_IsNotLost` bounds a `SpinWait` poll loop by `FailureTimeout`, i.e. it is
  the loop's own hang guard, not a fixed pass/fail threshold on an unrelated operation. The `Dispose()`
  check (`BeLessThan(FailureTimeout, "the wait on an in-flight drain is bounded")`) is the same category.

* **`tests/OpenAIProvider.Tests/Middleware/OpenRouterUsageMiddlewareTests.cs::PerformanceBudget_*`** — two
  dedicated performance-budget tests, already written with CI safety in mind and already commented as such
  ("keep this broad enough for parallel CI and first-run JIT overhead"): `<= 5000ms` final-chunk latency,
  `<= 250ms` average per-chunk CPU overhead. Wide margins by design.

* **`tests/Misc.Tests/Utils/TaskManagerTests.cs::TaskManager_LargeNumberOfTasks_ShouldHandleEfficiently`**
  — `< 5000ms` for 1,000 in-memory task insertions. Wide margin.

* **`tests/LmEmbeddings.Tests/Core/ServerEmbeddingsTests.cs`, `BaseHttpServiceTests.cs`, and the retained
  parts of `RerankingServiceTests.cs`** — the remaining `Stopwatch` usages are either lower-bound retry-
  backoff checks (`>= 900ms`, `>= 400ms`, `>= 80ms`; safe by construction) or `Debug.WriteLine`-only
  diagnostics with no assertion attached.

* **`tests/LmStreaming.Sample.E2E.Tests/Scenarios/DeferredAuthWebhookTests.cs`** — one lower-bound check
  (safe by construction) and one upper bound (`< 5s`, "host-allowlist denies must not be held") with a
  5-second margin for a full E2E host round trip; judged low risk.

* **`tests/LmCore.Tests/Logging/SimpleLoggingTests.cs`, `LoggingSystemIntegrationTests.cs`, and
  `tests/LmCore.Tests/Integration/LoggingIntegrationTests.cs`** — per-iteration performance-regression
  sanity checks (`< 1.0ms`, `< 2.0ms`, `< 50.0ms` per logged line), a different category from
  correctness-via-timing: they exist to catch a logging-path performance regression, not to distinguish a
  correct code path from a buggy one. The `LoggingIntegrationTests.cs` match is inside a commented-out
  block and does not execute at all. Out of scope for #343's correctness framing; flagged as a residual
  perf-sanity flake-risk category for a future, separately-scoped pass.

* **`tests/LmCore.Tests/MockHttpHandlerBuilderBenchmarks.cs`** — a benchmark harness that records timing
  across runs; not a pass/fail correctness assertion.

* **`tests/CodeReviewDaemon.Sample.Tests/Scenarios/SandboxLimitsTests.cs`** — grep false positive. Its
  `CommandTimeout.Should().BeGreaterThan(TimeSpan.Zero)` checks a static configuration default, not an
  elapsed measurement.

### Deferred

`tests/LmTestUtils.Tests/WaitTests.cs`, `tests/LmEmbeddings.Tests/Core/BaseEmbeddingServiceApiTypeTests.cs`,
`tests/LmCore.Tests/Middleware/FunctionRegistryFilteringTests.cs`, and the three
`tests/LmConfig.Tests/Services/OpenRouterModelService{RealApiIntegration,Comprehensive,Cache}Tests.cs`
files matched the inventory grep but were not read to the same depth for this pass. None appeared in
#343's own examples or in any recent flake report; if one of them is later implicated in a specific CI
flake, it should be triaged with the same convert-or-justify method documented here rather than folded
into this change.

## Consequences

* The four `FailoverEmbeddingService`/`FailoverRerankService` tests and the one `RerankingService` test no
  longer race a real timer or clock against CI scheduling; they are both deterministic and strictly
  faster (no more real `Task.Delay`).
* `FailoverOptions.TimeProvider` is a new, non-breaking seam available to any future failover-recovery
  test that needs deterministic clock control; every existing caller keeps `TimeProvider.System` by
  default.
* The remaining wall-clock assertions catalogued above are left unchanged: each is either safe by
  construction (a lower bound), has a wide margin relative to plausible CI jitter, is itself a
  hang-prevention ceiling rather than a discriminator, or (for `SandboxSessionRegistryPluginSelectionTests.cs`)
  already applies the #332 pattern deliberately. Leaving them in place, rather than converting everything
  the grep matched, keeps this change scoped per #343's own instruction not to invent a large speculative
  change.
* The Logging perf-sanity-check category and the four deferred files are noted as candidates for a
  dedicated follow-up if a future CI flake report names one of them specifically.
