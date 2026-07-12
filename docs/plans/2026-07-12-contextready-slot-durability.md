# ContextReady Slot Durability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use `development/reference/executing-plans-guide.md` to implement this plan task-by-task.

**Goal:** Make the daemon's `ContextReady` stage self-healing and bounded — every pooled-slot lease begins on a pristine store, a crashed/interrupted prior lease can never wedge or contaminate the next one, and a genuinely stuck commit is parked-and-alerted after bounded backoff instead of hot-looping.

**Architecture:** Approach A (see `2026-07-12-contextready-slot-durability-design.md`). A new `SlotHygiene` engine enforces clean-on-entry (clear stale locks, abort in-progress ops, `reset --hard` + `clean -ffdx` superproject+submodules, health-probe) and clean-on-exit (commit notes → strip). A `GitFailureClassifier` splits git failures into transient vs corrupt, driving a re-clone escalation. An in-memory `RetryGovernor` adds attempt-counting, exponential backoff, and park-after-K (restart clears it → retry all).

**Tech Stack:** .NET 9, C# (CodeReviewDaemon.Sample), xUnit + FluentAssertions, the repo's hardened `GitRunner`. CSharpier / `dotnet format whitespace` gating on commit (Husky).

---

## Conventions for every task

- **Build:** `dotnet build tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj -v:m -nologo`
- **Test a class:** `dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --no-build --filter "FullyQualifiedName~<ClassName>"`
- **Collection expressions** (`[.. x]`) not `.ToArray()`; the repo treats IDE analyzer hints (IDE0005 etc.) as build errors.
- **Read the current file first** before editing — line numbers below are from the map at design time and will drift.
- **Commit** at the end of each task (Husky `format-verify` runs `dotnet format whitespace … --verify-no-changes`; no AI signature in messages).

---

### Task 1: `GitFailureClassifier` — classify git failures

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Workspace/Git/GitFailureClassifier.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/GitFailureClassifierTests.cs`
- Reference (read first): the existing `CloneFailureClassifier` used by the ReviewBot path (`grep -rn CloneFailureClassifier samples/CodeReviewDaemon.Sample`) — mirror its style; do not duplicate its patterns, reuse if a shared helper is natural.

**Step 1: Write the failing test**
```csharp
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class GitFailureClassifierTests
{
    [Theory]
    [InlineData("fatal: Unable to create '/x/.git/index.lock': File exists.")]
    [InlineData("error: object file .git/objects/ab/cd is empty")]
    [InlineData("fatal: not a git repository")]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout")]
    public void Corrupt_slot_stderr_classifies_as_Corrupt(string stderr) =>
        GitFailureClassifier.Classify(stderr, exitCode: 128).Should().Be(GitFailureKind.Corrupt);

    [Theory]
    [InlineData("fatal: unable to access 'https://…': Could not resolve host: github.com")]
    [InlineData("fatal: unable to access 'https://…': The requested URL returned error: 503")]
    [InlineData("Connection timed out")]
    public void Transient_network_stderr_classifies_as_Transient(string stderr) =>
        GitFailureClassifier.Classify(stderr, exitCode: 128).Should().Be(GitFailureKind.Transient);

    [Fact]
    public void Unrecognized_stderr_classifies_as_Unknown() =>
        GitFailureClassifier.Classify("something weird", exitCode: 1).Should().Be(GitFailureKind.Unknown);
}
```

**Step 2: Run test — expect FAIL** (type not defined).

**Step 3: Implement**
```csharp
namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>How a failed git command should be treated by the ContextReady recovery ladder.</summary>
internal enum GitFailureKind
{
    /// <summary>Network/auth/rate-limit — retry (with backoff); do NOT re-clone the slot.</summary>
    Transient,
    /// <summary>Local repo corruption/contention (stale lock, dirty tree, broken object) — re-clone the slot.</summary>
    Corrupt,
    /// <summary>Unrecognized — treated as transient, but logged for pattern-gap review.</summary>
    Unknown,
}

/// <summary>
/// Classifies a git failure from its stderr + exit code so the ContextReady stage can distinguish a
/// transient network fault (retry) from local slot corruption (re-clone). Pattern-matched, ordered
/// corrupt-first because a corrupt marker is more specific than the generic "unable to access".
/// </summary>
internal static class GitFailureClassifier
{
    private static readonly string[] CorruptMarkers =
    [
        "index.lock", "shallow.lock", ".lock': file exists", "unable to create",
        "not a git repository", "object file", "is empty", "loose object", "corrupt",
        "would be overwritten", "cannot lock ref", "bad object",
    ];

    private static readonly string[] TransientMarkers =
    [
        "could not resolve host", "connection timed out", "connection reset",
        "returned error: 5", "returned error: 429", "operation timed out",
        "ssl", "temporary failure", "early eof", "rpc failed",
    ];

    public static GitFailureKind Classify(string? stderr, int exitCode)
    {
        var s = (stderr ?? string.Empty).ToLowerInvariant();
        if (CorruptMarkers.Any(s.Contains))
        {
            return GitFailureKind.Corrupt;
        }

        if (TransientMarkers.Any(s.Contains))
        {
            return GitFailureKind.Transient;
        }

        return GitFailureKind.Unknown;
    }
}
```
> Note: `early eof`/`rpc failed` are network-transient; keep them AFTER the corrupt list so a corrupt marker wins if both appear.

**Step 4: Run test — expect PASS.**

**Step 5: Commit** `git add … && git commit -m "feat(daemon): classify git failures transient vs corrupt for ContextReady recovery"`

---

### Task 2: `SlotHygiene` — clean-on-entry + strip

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Workspace/SlotHygiene.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/SlotHygieneTests.cs`
- Read first: `Workspace/ReviewSlotPreparer.cs` (for `GitRunner _git` usage + `PosixJoin`), `Workspace/ReviewSlot.cs` (`ReviewSlot.StorePath`), `Workspace/Git/GitRunner.cs` (`RunAsync` signature + `file://`-block caveat).

**Step 1: Write the failing tests** (real temp git repo; lock-clearing needs no git server so it works with the hardened runner).
```csharp
using System.Diagnostics;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class SlotHygieneTests
{
    [Fact]
    public async Task EnsureClean_removes_stale_index_lock_in_store_and_submodule_gitdirs()
    {
        using var repo = TempGitRepo.Init();                    // helper: git init + one commit
        var storeLock = repo.Touch(".git/index.lock");
        var moduleLock = repo.Touch(".git/modules/sub/index.lock"); // simulate submodule gitdir lock

        var verdict = await SlotHygiene.EnsureCleanAsync(repo.GitRunner, repo.Path, CancellationToken.None);

        File.Exists(storeLock).Should().BeFalse();
        File.Exists(moduleLock).Should().BeFalse();
        verdict.Should().Be(HygieneVerdict.Clean);
    }

    [Fact]
    public async Task EnsureClean_resets_dirty_tracked_and_removes_untracked()
    {
        using var repo = TempGitRepo.Init();
        repo.Overwrite("tracked.txt", "DIRTY");                 // modify a committed file
        repo.Write("untracked.txt", "junk");                   // agent-created leftover

        await SlotHygiene.EnsureCleanAsync(repo.GitRunner, repo.Path, CancellationToken.None);

        repo.Read("tracked.txt").Should().NotContain("DIRTY", "reset --hard restores committed content");
        File.Exists(Path.Combine(repo.Path, "untracked.txt")).Should().BeFalse("clean -ffdx removes untracked");
    }

    [Fact]
    public async Task EnsureClean_reports_NeedsReclone_when_gitdir_is_broken()
    {
        using var repo = TempGitRepo.Init();
        Directory.Delete(Path.Combine(repo.Path, ".git"), recursive: true); // structurally broken

        var verdict = await SlotHygiene.EnsureCleanAsync(repo.GitRunner, repo.Path, CancellationToken.None);

        verdict.Should().Be(HygieneVerdict.NeedsReclone);
    }
}
```
> Add a small `TempGitRepo` test helper under `tests/.../Workspace/` (or reuse an existing one — `grep -rn "git init" tests/CodeReviewDaemon.Sample.Tests`). It runs real `git` via the same hardened `GitRunner` the preparer uses.

**Step 2: Run — expect FAIL.**

**Step 3: Implement**
```csharp
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>Result of a clean-on-entry pass over a leased slot's store.</summary>
internal enum HygieneVerdict
{
    /// <summary>Store is usable — stale state was cleared in place.</summary>
    Clean,
    /// <summary>Store is structurally broken — the caller must re-clone it before use.</summary>
    NeedsReclone,
}

/// <summary>
/// Brings a leased pooled slot's store to a pristine state at the START of every prepare (clean-on-entry,
/// the durability guarantee) and strips it back to pristine on a successful close (best-effort tidiness).
/// Safe because the pool leases a slot to at most one run at a time, so a leased slot has no concurrent git
/// process — any *.lock in it is stale by definition and safe to remove. See the design doc §3–§4.
/// </summary>
internal static class SlotHygiene
{
    public static async Task<HygieneVerdict> EnsureCleanAsync(
        GitRunner git, string storePath, CancellationToken ct)
    {
        var gitDir = Path.Combine(storePath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            return HygieneVerdict.NeedsReclone;
        }

        // 1. Clear stale locks anywhere under .git (store + every submodule gitdir under .git/modules).
        RemoveStaleLocks(gitDir);

        // 2. Abort any in-progress merge/rebase/cherry-pick left by an interrupted prior lease.
        AbortInProgress(gitDir);

        // 3. Reset + clean the superproject, then every submodule working tree.
        //    Non-zero exits here are tolerated — the health probe (step 4) is the real gate.
        await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive",
                    "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);

        // 4. Health probe — a broken object store or missing gitdir means the warm store is unusable.
        var probe = await git.RunAsync(["-C", storePath, "rev-parse", "--git-dir"], storePath, ct)
            .ConfigureAwait(false);
        return probe.Succeeded ? HygieneVerdict.Clean : HygieneVerdict.NeedsReclone;
    }

    /// <summary>Success-path strip: caller commits+pushes notes first, then this returns the slot pristine.</summary>
    public static async Task StripAsync(GitRunner git, string storePath, CancellationToken ct)
    {
        RemoveStaleLocks(Path.Combine(storePath, ".git"));
        await git.RunAsync(["-C", storePath, "reset", "--hard"], storePath, ct).ConfigureAwait(false);
        await git.RunAsync(["-C", storePath, "clean", "-ffdx"], storePath, ct).ConfigureAwait(false);
        await git.RunAsync(
                ["-C", storePath, "submodule", "foreach", "--recursive",
                    "git reset --hard && git clean -ffdx"],
                storePath, ct)
            .ConfigureAwait(false);
    }

    private static void RemoveStaleLocks(string gitDir)
    {
        if (!Directory.Exists(gitDir))
        {
            return; // .git may be a gitfile (submodule) — its real dir is handled via .git/modules below.
        }

        foreach (var lockFile in Directory.EnumerateFiles(gitDir, "*.lock", SearchOption.AllDirectories))
        {
            TryDelete(lockFile);
        }
    }

    private static void AbortInProgress(string gitDir)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            TryDelete(Path.Combine(gitDir, marker));
        }

        foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = Path.Combine(gitDir, dir);
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* best-effort; next lease retries */ }
    }
}
```

**Step 4: Run — expect PASS.**

**Step 5: Commit.**

---

### Task 3: Wire `EnsureCleanAsync` into `ReviewSlotPreparer` + submodule verify + classification

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs` (`PrepareAsync` head ~100–106; `RunGitOrThrowAsync` ~165–178; submodule init ~122–134)
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/Git/…` if a shared `SlotNeedsRecloneException` / `SlotCorruptException` type is added (create `Workspace/SlotRecoveryExceptions.cs`)
- Test: extend `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPreparerTests.cs`

**Step 1: Write failing tests** — (a) prepare over a warm store seeded with a stale `index.lock` + dirty tree now **succeeds**; (b) a submodule that fails to init throws `SlotCorruptException` (not a silent proceed). Model them on the existing `ReviewSlotPreparerTests` fakes.

**Step 2: Run — expect FAIL.**

**Step 3: Implement**
- Create `Workspace/SlotRecoveryExceptions.cs`:
```csharp
namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>The leased slot's store is corrupt and must be re-cloned before retrying prepare.</summary>
internal sealed class SlotNeedsRecloneException(string message) : Exception(message);

/// <summary>A prepare git step failed in a way classified as slot corruption (drives the re-clone ladder).</summary>
internal sealed class SlotCorruptException(string message) : Exception(message);
```
- In `PrepareAsync`, before step 1 `fetch origin`:
```csharp
var storeRoot = slot.StorePath;
if (await SlotHygiene.EnsureCleanAsync(_git, storeRoot, cancellationToken).ConfigureAwait(false)
    == HygieneVerdict.NeedsReclone)
{
    throw new SlotNeedsRecloneException($"Run {run.Id}: slot {slot.Index} store is unusable; re-clone required.");
}
```
- After the submodule init (`outcome = await initializer.InitializeAsync(...)`), add post-init verification:
```csharp
if (!outcome.InitializedPaths.Contains(submoduleRelPath, StringComparer.Ordinal))
{
    throw new SlotCorruptException(
        $"Run {run.Id}: reviewed submodule '{submoduleRelPath}' did not initialize; slot needs re-clone.");
}
```
- In `RunGitOrThrowAsync`, classify:
```csharp
if (!result.Succeeded)
{
    var kind = GitFailureClassifier.Classify(result.Stderr, result.ExitCode);
    var message = $"Run {run.Id}: {action} failed (exit {result.ExitCode}): {result.Stderr}";
    throw kind == GitFailureKind.Corrupt
        ? new SlotCorruptException(message)
        : new InvalidOperationException(message); // transient/unknown → normal retry, no re-clone
}
```

**Step 4: Run — expect PASS.** **Step 5: Commit.**

---

### Task 4: `ReviewSlotPool.RecloneStoreAsync`

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlot.cs` (the pool; reuse the existing `TryResetStore` wipe+clone, ~104–122/159–183)
- Test: extend `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPoolTests.cs`

**Step 1: Failing test** — lease a slot, dirty/wedge its store, call `RecloneStoreAsync(slot)`, assert the store is a fresh clone (the wedged marker file is gone, `.git` valid).

**Step 3: Implement** a public `Task RecloneStoreAsync(ReviewSlot slot, CancellationToken ct)` that wipes the slot's `StorePath` (clearing read-only, as `TryResetStore` does) and re-runs the pool's clone callback. Extract the wipe+clone shared with `TryResetStore` into one private helper (DRY).

**Steps 2/4/5:** fail → pass → commit.

---

### Task 5: Recovery ladder in `DaemonReviewStageExecutor.TryPooledFetchContextAsync`

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` (`TryPooledFetchContextAsync` ~447–539, around the `PrepareAsync` call ~474–477)
- Test: extend `tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs`

**Step 1: Failing tests** (fake preparer + fake pool): (a) prepare throws `SlotNeedsRecloneException`/`SlotCorruptException` once → executor calls `pool.RecloneStoreAsync` then retries prepare → success; (b) prepare throws corrupt twice → executor rethrows (governor will park); (c) **cross-PR cleanliness**: two sequential leases of the same slot, the first leaving an untracked file, the second starts clean.

**Step 3: Implement** — wrap the `PrepareAsync` call:
```csharp
PreparedCheckout prepared;
try
{
    prepared = await _slotWorkspace.Preparer.PrepareAsync(/* … */).ConfigureAwait(false);
}
catch (Exception ex) when (ex is SlotNeedsRecloneException or SlotCorruptException)
{
    _logger.LogWarning(ex, "Run {RunId}: slot {Index} corrupt; re-cloning and retrying prepare once.",
        run.Id, slot.Index);
    await _slotWorkspace.Pool.RecloneStoreAsync(slot, cancellationToken).ConfigureAwait(false);
    prepared = await _slotWorkspace.Preparer.PrepareAsync(/* … same args … */).ConfigureAwait(false);
}
```
(One re-clone + one retry; a second failure propagates to the stage → RetryGovernor.)

**Steps 2/4/5.**

---

### Task 6: Strip on the success close path

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` — the notes commit+push at close (`PostAsync` / the notes-branch push path; `grep -n "MergeNotesBranchOnClose\|notes" DaemonReviewStageExecutor.cs`)
- Test: extend the pooled tests

**Step 1: Failing test** — after a successful post, the slot store has no untracked/dirty files (the agent's leftover files are gone) while the notes commit is present on the notes branch.

**Step 3: Implement** — after the existing notes commit + push succeeds, call `await SlotHygiene.StripAsync(_slotWorkspace.HostRunner… , storePath, ct)`. Guard it to the success path only; do NOT strip before the notes are committed+pushed (that would discard them).

**Steps 2/4/5.**

---

### Task 7: `RetryGovernor` (in-memory attempts / backoff / park)

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/RetryGovernor.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/RetryGovernorTests.cs`

**Step 1: Failing tests** — inject a fake clock (`Func<DateTimeOffset>`): `ShouldAttempt` true initially; after `RecordFailure` it's false until `nextEligibleAt`; after `MaxContextRetries` failures `RecordFailure` returns `Parked` and `ShouldAttempt` stays false; `RecordSuccess` clears so `ShouldAttempt` is true again. Backoff doubles and caps.

**Step 3: Implement**
```csharp
namespace CodeReviewDaemon.Sample.Orchestration;

internal enum RetryDecision { Retry, Parked }

/// <summary>
/// In-memory retry governance for review runs: attempt-counting, exponential backoff, and park-after-K.
/// State is deliberately NOT persisted — a daemon restart clears it so every backing-off/parked run is
/// retried fresh (operator intent: "restart = retry"). Safe because ContextReady's clean-on-entry
/// self-heals the stuck cause on the first retry. See design doc §5.
/// </summary>
internal sealed class RetryGovernor(
    int maxAttempts, TimeSpan backoffBase, TimeSpan backoffCap, Func<DateTimeOffset> clock,
    ILogger<RetryGovernor> logger)
{
    private sealed class State { public int Attempts; public DateTimeOffset NextEligibleAt; public bool Parked; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, State> _states = new();

    public bool ShouldAttempt(Guid runId)
    {
        if (!_states.TryGetValue(runId, out var s)) { return true; }
        return !s.Parked && clock() >= s.NextEligibleAt;
    }

    public RetryDecision RecordFailure(Guid runId, string lastError)
    {
        var s = _states.GetOrAdd(runId, _ => new State());
        lock (s)
        {
            s.Attempts++;
            if (s.Attempts >= maxAttempts)
            {
                s.Parked = true;
                logger.LogError("review_run PARKED run {RunId} after {Attempts} attempts: {Error}",
                    runId, s.Attempts, lastError);
                return RetryDecision.Parked;
            }

            var delay = TimeSpan.FromTicks(Math.Min(
                backoffCap.Ticks, backoffBase.Ticks * (1L << (s.Attempts - 1))));
            s.NextEligibleAt = clock() + delay;
            return RetryDecision.Retry;
        }
    }

    public void RecordSuccess(Guid runId) => _states.TryRemove(runId, out _);
}
```
> Jitter can be added later; deterministic backoff keeps the test simple. `1L << (attempts-1)` with the cap avoids overflow because the cap clamps first for large attempts.

**Steps 2/4/5.**

---

### Task 8: Wire `RetryGovernor` into `PrOrchestrator`

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/PrOrchestrator.cs` (`RunAsync` entry ~35–39; failure path ~80–87; success ~89–91)
- Test: extend the orchestrator tests — a run that just failed and is inside its backoff window is **skipped** (no stage executed); after K failures it's parked (no further attempts) and the `PARKED` log fires.

**Step 3: Implement**
- At the top of `RunAsync`, after resolving `run`:
```csharp
if (!_retryGovernor.ShouldAttempt(run.Id))
{
    return WorkflowResult.Skipped(run); // still RetryPending; backing off or parked
}
```
(Use whatever "no-op this poll" result the loop already understands; if none, return early leaving state unchanged.)
- In the failure `catch` (before/after persisting RetryPending):
```csharp
_retryGovernor.RecordFailure(run.Id, ex.Message); // logs PARKED on the Kth
```
- On the success fall-through: `_retryGovernor.RecordSuccess(run.Id);`

**Steps 2/4/5.**

---

### Task 9: Config knobs + DI registration

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs` (register `RetryGovernor` singleton)
- Test: extend `CodeReviewDaemonOptionsTests` for the three defaults.

**Step 3: Implement** — add:
```csharp
public int MaxContextRetries { get; init; } = 5;
public int RetryBackoffBaseSeconds { get; init; } = 30;
public int RetryBackoffCapSeconds { get; init; } = 900;
```
Register in `Program.cs`:
```csharp
builder.Services.AddSingleton(sp => new RetryGovernor(
    daemonOptions.MaxContextRetries,
    TimeSpan.FromSeconds(daemonOptions.RetryBackoffBaseSeconds),
    TimeSpan.FromSeconds(daemonOptions.RetryBackoffCapSeconds),
    () => DateTimeOffset.UtcNow,
    sp.GetRequiredService<ILogger<RetryGovernor>>()));
```
Inject it into `PrOrchestrator`.

**Steps 1/2/4/5** (default-value test → fail → pass → commit).

---

### Task 10: Full-suite regression + design doc reconcile

**Step 1:** `dotnet build tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj -v:m` → 0/0.
**Step 2:** `dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --no-build` → all green.
**Step 3:** `dotnet format whitespace LmDotnetTools.sln --verify-no-changes --include <all changed files>` → exit 0.
**Step 4:** If any design detail changed during implementation, update `2026-07-12-contextready-slot-durability-design.md`.
**Step 5:** Commit.

---

### Task 11: Live verification (the incident, reproduced)

Against the running achieveai daemon (rebuild-publish + restart first):
1. Seed a stale lock into a real slot: `touch B:/sandbox-workspaces/workspaces/review-pool/slot-0/store/.git/modules/<repo>/index.lock`.
2. Trigger a poll (or wait ~30 s).
3. Query the JSONL log (DuckDB) for the run: confirm the next lease **cleared the lock and prepared successfully** (no `index.lock` checkout failure), i.e. the exact incident now self-heals.
4. Confirm a genuinely-unrecoverable slot (delete `.git`) → re-clone → succeeds; and force K failures → the `PARKED` Error line appears and the run stops looping.

---

## Deferred (follow-up, not in this plan)
- **RetryPending scanner** — drive RetryPending runs straight from the store so a PR that permanently drops off the provider's open-PR page is still retried (design doc §9).
- **Approach B** — disposable `git worktree` per lease over a persistent object store.
