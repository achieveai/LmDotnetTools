# Daemon SDK-Backed Review-Store Setup Implementation Plan

**Status:** Implemented (code) — shipped in `75d8a0b6`. `ReviewSlotPreparer` now owns store recloning and `Workspace/SlotHygiene.cs` provides `EnsureStoreAsync`. Task 7's live run against MCQdbDEV PR #11229 is an operational step that cannot be verified from this repository; treat it as unconfirmed.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make CodeReviewDaemon use one typed `SandboxClient`-backed run session to prepare and review an in-process pooled checkout, while retaining the host-only scoped commit/push gate, then prove the flow live on MCQdbDEV PR #11229.

**Architecture:** `ReviewSlotPool` becomes an address/lease allocator only. `DaemonReviewStageExecutor` leases a slot, provisions `ReviewRunSession` over that slot before any repository access, constructs `ReviewSlotPreparer` over the session's `SandboxSessionAdapter`, and uses that same runner/filesystem for clone, hygiene, branch/submodule/head setup, diff, manifest, and the review tool context. After the review is terminal, the daemon destroys the session and only then uses host git to stage the approved notes directory, commit, push, strip, and return the slot.

**Tech Stack:** .NET 9, C# 13, xUnit, FluentAssertions, `AchieveAi.LmDotnetTools.Sandbox.SandboxClient`, ASP.NET Core DI, SQLite, Azure DevOps REST/Entra OAuth.

## Global Constraints

- Target only the in-process pooled path used by `appsettings.mcqdb.json`; preserve the S2S hosted-session path.
- Every pre-review clone/read/fetch/branch/submodule/checkout/diff/manifest operation must run through `ReviewRunSession.CommandRunner` / `.FileSystem`, whose production implementation is `SandboxSessionAdapter` over typed `SandboxClient`.
- The same `ReviewRunSession` must serve setup and review; no second setup session and no setup-to-review handoff.
- Do not pass the host ADO write credential into the sandbox session.
- Host capabilities may run only after session destruction and only for the scoped notes commit/push, strip, and existing lifecycle maintenance.
- The pooled SDK-required path fails closed when the slot cannot be mounted or no SDK session is available; it must not downgrade to host setup or a different per-run mount.
- Preserve argument-vector commands; never concatenate attacker-influenced branch/path/URL values into a shell string.
- Preserve exact submodule allow-listing, including the reviewed repo and configured first-party nested MCQdbDEV submodules.
- Do not touch the existing LmStreaming.Sample process on port 5050 during the MCQdb live gate.
- Do not add Co-Authored-By or any AI signature to commits or PR text.
- Format with the repository's gated formatter/hook; do not add `*-v2`, `*-improved`, or `*-enhanced` files.

---

## File structure

**Modify:**

- `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlot.cs` — make the pool allocate slot addresses only; remove clone/reclone repository ownership.
- `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs` — add SDK-side ensure/clone/reclone and container-rooted preparation; remove direct host-directory scratch handling.
- `samples/CodeReviewDaemon.Sample/Workspace/SlotHygiene.cs` — perform stale-lock/in-progress cleanup through injected filesystem/runner so pre-review hygiene is SDK-backed.
- `samples/CodeReviewDaemon.Sample/Orchestration/ReviewSessionProvisioner.cs` — add a strict pooled mount method that never falls back to a per-run mount.
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` — provision before setup, use the run session for all context work, retain host capabilities only for terminal commit/strip.
- `samples/CodeReviewDaemon.Sample/Program.cs` — stop cloning the store in pool construction; provide a session-bound preparer factory while retaining host commit capabilities.
- `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPoolTests.cs` — replace clone-callback tests with address-only lease tests.
- `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPreparerTests.cs` — test SDK ensure/clone/reclone, container paths, scratch cleanup, and error classification.
- `tests/CodeReviewDaemon.Sample.Tests/Workspace/SlotHygieneTests.cs` — pin SDK filesystem stale-lock cleanup and runner-only git hygiene.
- `tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewSessionProvisionerTests.cs` — pin strict pooled mount behavior without changing the generic fallback API.
- `tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs` — prove session-before-setup ordering, same-session identity, no host setup, fail-closed behavior, SDK recovery, and post-destroy host gate.
- `tests/CodeReviewDaemon.Sample.Tests/Infrastructure/FakeSandboxCommandRunner.cs` — add optional call recording/fault behavior needed to distinguish SDK and host phases.
- `tests/CodeReviewDaemon.Sample.Tests/Infrastructure/FakeSandboxFileSystem.cs` — add deletion/directory-state behavior only if required by the final SDK-side reset implementation.
- `scratchPad/conversation_memories/code-review-sample-uses-s2s-api/implementation-progress.md` — record measured RED→GREEN proof and the live MCQdb run.

**Do not create a parallel preparer or v2 pool.** Modify the existing types.

---

### Task 1: Make pooled slot mounting strict without changing generic callers

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/ReviewSessionProvisioner.cs:19-190`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewSessionProvisionerTests.cs:61-111`

**Interfaces:**
- Preserve: `Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)` for existing/S2S behavior.
- Produce: `Task<ReviewRunSession> GetOrCreateRequiredForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)` on `IReviewSessionProvisioner`.
- Contract: the new method throws `InvalidOperationException` if the slot cannot be expressed under `_workspaceBasePath` or provisioning returns no session; it never calls `GetOrCreateAsync`.

- [ ] **Step 1: Write strict-mount failing tests**

Add to `ReviewSessionProvisionerTests`:

```csharp
[Fact]
public async Task GetOrCreateRequiredForSlotAsync_MountsTheExactSlot()
{
    var fake = new FakeSessionSource();
    var provisioner = new ReviewSessionProvisioner(
        fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws");
    var slot = new ReviewSlot(
        0, "/ws/review-pool/slot-0", "/ws/review-pool/slot-0/store", "/ws/review-pool/slot-0/scratch");

    var session = await provisioner.GetOrCreateRequiredForSlotAsync(Run(), slot, default);

    session.SessionId.Should().Be("session-review-run-7");
    fake.LastRef!.DirectoryRelPath.Should().Be("review-pool/slot-0");
}

[Theory]
[InlineData(null, "/ws/review-pool/slot-0")]
[InlineData("/ws", "/other/slot-0")]
public async Task GetOrCreateRequiredForSlotAsync_RejectsAnUnrepresentableSlot(
    string? workspaceBase, string slotPath)
{
    var fake = new FakeSessionSource();
    var provisioner = new ReviewSessionProvisioner(
        fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: workspaceBase);
    var slot = new ReviewSlot(0, slotPath, $"{slotPath}/store", $"{slotPath}/scratch");

    var act = () => provisioner.GetOrCreateRequiredForSlotAsync(Run(), slot, default);

    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*pooled slot*workspace base*");
    fake.CreateCount.Should().Be(0);
}
```

- [ ] **Step 2: Run the strict-mount tests and confirm RED**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~ReviewSessionProvisionerTests.GetOrCreateRequiredForSlotAsync" --nologo
```

Expected: build/test fails because `GetOrCreateRequiredForSlotAsync` does not exist.

- [ ] **Step 3: Add the strict interface and implementation**

In `IReviewSessionProvisioner` add:

```csharp
Task<ReviewRunSession> GetOrCreateRequiredForSlotAsync(
    ReviewRun run,
    ReviewSlot slot,
    CancellationToken ct);
```

Implement by resolving `slotRelPath`, throwing on null, calling `ProvisionAsync`, and throwing if it returns null. Do not alter `GetOrCreateForSlotAsync` yet; existing non-required callers retain fallback behavior.

- [ ] **Step 4: Update all test fakes to implement the new method**

Search:

```bash
rg "IReviewSessionProvisioner" tests/CodeReviewDaemon.Sample.Tests
```

For every fake, implement the strict method with the same session it returns from `GetOrCreateForSlotAsync`; fakes that model missing sessions should throw `InvalidOperationException` explicitly.

- [ ] **Step 5: Run provisioner and compilation regression tests**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~ReviewSessionProvisionerTests|FullyQualifiedName~ReviewToolContextBuildTests|FullyQualifiedName~RunCleanupTests" --nologo
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit**

```bash
git add samples/CodeReviewDaemon.Sample/Orchestration/ReviewSessionProvisioner.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewSessionProvisionerTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/ReviewToolContextBuildTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/RunCleanupTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorSessionTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs
git commit -m "feat(daemon): require exact pooled slot mounts"
```

---

### Task 2: Turn `ReviewSlotPool` into an address-only lease pool

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlot.cs:17-229`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs:591-616`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPoolTests.cs`

**Interfaces:**
- Change constructor to:

```csharp
ReviewSlotPool(
    int maxSlots,
    string? hostRoot,
    string scratchDirName,
    ILogger<ReviewSlotPool> logger,
    string slotDirPrefix = "slot-")
```

- Remove from `IReviewSlotPool`: `RecloneStoreAsync`.
- `LeaseAsync` creates `HostPath` and `ScratchPath` only; it neither creates/reads `StorePath` nor invokes git.

- [ ] **Step 1: Replace clone-ownership tests with address-only tests**

Rewrite the first pool tests to assert:

```csharp
[Fact]
public async Task LeaseAsync_FirstLease_AllocatesSlotAddressWithoutCreatingStore()
{
    var pool = CreatePool(maxSlots: 2);

    var slot = await pool.LeaseAsync(default);

    slot.Index.Should().Be(0);
    Directory.Exists(slot.HostPath).Should().BeTrue();
    Directory.Exists(slot.ScratchPath).Should().BeTrue();
    Directory.Exists(slot.StorePath).Should().BeFalse(
        "repository ownership starts only after the slot is mounted through SandboxClient");
}

[Fact]
public async Task LeaseAsync_AfterReturn_ReusesTheAddressWithoutInspectingStore()
{
    var pool = CreatePool(maxSlots: 1);
    var first = await pool.LeaseAsync(default);
    Directory.CreateDirectory(first.StorePath);
    File.WriteAllText(Path.Combine(first.StorePath, "partial"), "must be handled by SDK preparation");

    await pool.ReturnAsync(first, default);
    var second = await pool.LeaseAsync(default);

    second.Should().Be(first);
    File.Exists(Path.Combine(second.StorePath, "partial")).Should().BeTrue(
        "the pool does not classify or repair repository state");
}
```

Delete tests for clone callback invocation, clone failure cleanup, and `RecloneStoreAsync`; that behavior moves to `ReviewSlotPreparerTests` in Task 3. Keep capacity, prefix, sanitizer, and constructor tests.

- [ ] **Step 2: Run pool tests and confirm RED**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~ReviewSlotPoolTests" --nologo
```

Expected: failures because current pool creates/clones the store and requires a callback.

- [ ] **Step 3: Remove repository ownership from the pool**

Delete `_ensureStoreClonedAsync`, callback constructor parameter, `RecloneStoreAsync`, `TryResetStore`, and `IsDirectoryEmpty`. Keep permit/index exception safety around address-directory creation.

- [ ] **Step 4: Update Program pool construction**

Replace the clone callback in `Program.cs` with the address-only constructor. Do not add a host clone elsewhere:

```csharp
var pool = new ReviewSlotPool(
    daemonOptions.ReviewPoolSize,
    poolRoot,
    daemonOptions.ScratchDirName,
    loggerFactory.CreateLogger<ReviewSlotPool>(),
    slotDirPrefix);
```

Compilation may still fail in executor recovery until Task 3 removes `RecloneStoreAsync`; make the temporary minimal executor change only after the new SDK preparer method exists, or implement Tasks 2 and 3 in one working tree before committing.

- [ ] **Step 5: Run pool tests**

Run the Task 2 filter again. Expected: all pool tests pass.

- [ ] **Step 6: Commit**

```bash
git add samples/CodeReviewDaemon.Sample/Workspace/ReviewSlot.cs \
  samples/CodeReviewDaemon.Sample/Program.cs \
  tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPoolTests.cs
git commit -m "refactor(daemon): make review slots address-only"
```

---

### Task 3: Make store ensure/reclone and hygiene SDK-backed

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/SlotHygiene.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/Sandbox/ISandboxFileSystem.cs` if deletion is added there
- Modify: `samples/CodeReviewDaemon.Sample/Workspace/Sandbox/SandboxSessionAdapter.cs` if the filesystem interface gains deletion
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPreparerTests.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/SlotHygieneTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Infrastructure/FakeSandboxFileSystem.cs`

**Interfaces:**
- Change `IReviewSlotPreparer.PrepareAsync` to operate on explicit container roots rather than host slot paths:

```csharp
Task<PreparedCheckout> PrepareAsync(
    ReviewRun run,
    string storeRoot,
    string scratchRoot,
    string storeUrl,
    string submoduleRelPath,
    string branch,
    string defaultBranch,
    string notesRelPath,
    OperationPolicy policy,
    CancellationToken cancellationToken);
```

- Produce:

```csharp
Task EnsureStoreAsync(
    string storeRoot,
    string storeUrl,
    CancellationToken cancellationToken);

Task RecloneStoreAsync(
    string storeRoot,
    string storeUrl,
    CancellationToken cancellationToken);
```

- `PreparedCheckout` paths are container paths for SDK setup/review. Host notes paths are derived later from `ReviewSlot` + `notesRelPath` for commit.

- [ ] **Step 1: Add missing-store clone tests**

Use a `FakeSandboxCommandRunner` whose `rev-parse --git-dir` probe fails and assert:

```csharp
[Fact]
public async Task EnsureStoreAsync_MissingStore_ClonesThroughTheInjectedRunner()
{
    var runner = new FakeSandboxCommandRunner()
        .OnArgvContains("rev-parse --git-dir", new SandboxCommandResult(128, "", "not a repository"));
    var preparer = NewPreparer(runner, new FakeSandboxFileSystem());

    await preparer.EnsureStoreAsync("/workspace/store", StoreUrl, CancellationToken.None);

    runner.Commands.Select(Join).Should().Contain(
        $"git clone {StoreUrl} /workspace/store");
}
```

Add:

- valid store probe does not clone;
- failed clone throws `InvalidOperationException` with exit/stderr;
- `RecloneStoreAsync` issues explicit `rm -rf -- /workspace/store`, then clone, through the injected runner;
- transient preparation failure remains `InvalidOperationException` and does not call reclone itself;
- corrupt preparation failure remains `SlotCorruptException`.

- [ ] **Step 2: Add container-path and scratch cleanup tests**

Replace host `Directory` assertions with command/filesystem assertions:

```csharp
var result = await preparer.PrepareAsync(
    CreateRun(),
    "/workspace/store",
    "/workspace/scratch",
    StoreUrl,
    SubmoduleRelPath,
    Branch,
    DefaultBranch,
    NotesRelPath,
    BuildPolicy(),
    CancellationToken.None);

result.StoreRoot.Should().Be("/workspace/store");
result.TargetDir.Should().Be("/workspace/store/repos/LmDotnetTools");
result.NotesDir.Should().Be("/workspace/store/PRs/github/achieveai-lmdotnettools/151");
runner.Commands.Select(Join).Should().Contain("rm -rf -- /workspace/scratch");
runner.Commands.Select(Join).Should().Contain("mkdir -p -- /workspace/scratch");
```

- [ ] **Step 3: Run preparer/hygiene tests and confirm RED**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~ReviewSlotPreparerTests|FullyQualifiedName~SlotHygieneTests" --nologo
```

Expected: new signatures/methods absent and host-directory assertions fail.

- [ ] **Step 4: Move stale-lock and in-progress cleanup behind injected capabilities**

`SlotHygiene.EnsureCleanAsync` currently calls `Directory.Exists`, `Directory.EnumerateFiles`, and host deletion helpers. Replace those checks with runner/filesystem operations so production calls flow through `SandboxSessionAdapter`. Prefer explicit argv commands where the SDK does not expose deletion:

```csharp
await git.RunAsync(
    ["-C", storePath, "rev-parse", "--git-dir"],
    storePath,
    ct);

await commandRunner.RunAsync(
    new SandboxCommand(["find", $"{storePath}/.git", "-type", "f", "-name", "*.lock", "-delete"]),
    ct);
```

Do not use a joined shell command. Preserve the deny-network git args and corruption classification.

- [ ] **Step 5: Implement SDK ensure/reclone and container-rooted prepare**

- `EnsureStoreAsync`: probe, clone on definite missing/not-repository.
- `RecloneStoreAsync`: explicit argv removal then clone.
- `PrepareAsync`: call `EnsureStoreAsync`, `SlotHygiene.EnsureCleanAsync`, fetch/branch/submodule/head using container paths, and clear scratch through explicit commands.
- Preserve `SubmoduleInitializer` and the exact `OperationPolicy`.

- [ ] **Step 6: Update fakes only as required**

If `ISandboxFileSystem` remains read/list/write only, no interface change is needed. If a typed delete operation is preferable and supported by `SandboxClient`, add the narrow method to production and fake implementations and test exact missing-path behavior. Do not add a broad generic shell API.

- [ ] **Step 7: Run preparer/hygiene tests**

Run the Task 3 filter. Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs \
  samples/CodeReviewDaemon.Sample/Workspace/SlotHygiene.cs \
  samples/CodeReviewDaemon.Sample/Workspace/Sandbox/ISandboxFileSystem.cs \
  samples/CodeReviewDaemon.Sample/Workspace/Sandbox/SandboxSessionAdapter.cs \
  tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPreparerTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Workspace/SlotHygieneTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Infrastructure/FakeSandboxFileSystem.cs
git commit -m "feat(daemon): prepare review stores through sandbox SDK"
```

---

### Task 4: Reorder pooled context to provision first and use one session

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:640-833, 1280-1435, 1935-1991, 2356-2364`
- Modify: `samples/CodeReviewDaemon.Sample/Program.cs:591-640`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Infrastructure/FakeSandboxCommandRunner.cs`

**Interfaces:**
- Change `ReviewSlotWorkspace` to:

```csharp
internal sealed record ReviewSlotWorkspace(
    IReviewSlotPool Pool,
    Func<ReviewRunSession, IReviewSlotPreparer> CreatePreparer,
    ISandboxCommandRunner HostCommitRunner,
    ISandboxFileSystem HostCommitFileSystem);
```

- Change `LeasedReview` to retain the `ReviewRunSession` used for setup, allowing review tool-context construction to reuse it without another provisioner call.
- `TryPooledFetchContextAsync` must use `/workspace/store` and `/workspace/<scratch>` for setup/diff/manifest.

- [ ] **Step 1: Add ordering and same-session failing tests**

Extend `RecordingProvisioner` to create one stable `SdkRunner` and `SdkFileSystem` and append events to a shared list. Add tests:

```csharp
[Fact]
public async Task ContextReady_provisions_the_slot_before_any_store_access()
{
    using var fixture = Fixture.Create();
    var run = fixture.SeedRun();

    await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

    fixture.Events.Should().StartWith("lease", "provision-slot", "read-gitmodules", "prepare", "diff", "manifest");
}

[Fact]
public async Task Review_reuses_the_exact_session_that_prepared_context()
{
    using var fixture = Fixture.Create();
    var run = fixture.SeedRun();
    await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

    await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

    fixture.Provisioner.GetOrCreateForSlotCalls.Should().Be(1);
    fixture.Factory.LastToolContext!.CommandRunner.Should().BeSameAs(fixture.Provisioner.SdkRunner);
    fixture.Factory.LastToolContext.FileSystem.Should().BeSameAs(fixture.Provisioner.SdkFileSystem);
}
```

Use actual fixture property names after reading `FakeReviewAgentLoopFactory`; do not invent a second context capture type.

- [ ] **Step 2: Add no-host-setup and fail-closed tests**

- Make `HostRunner` throw if any `diff`, `fetch`, `checkout`, `clone`, `rev-parse`, or `ls-files` command occurs before Posted.
- Assert ContextReady still succeeds and all these commands are on `SdkRunner`.
- Script `GetOrCreateRequiredForSlotAsync` to throw; assert no artifact, `ReturnCount == 1`, no review, and `DestroyAsync` is called if a session was partially created.

- [ ] **Step 3: Add SDK recovery tests**

Change recovery assertions from `Pool.RecloneCount` to `SdkRunner` commands:

```csharp
fixture.SdkRunner.Commands.Select(Join)
    .Should().ContainSingle(c => c.StartsWith("rm -rf -- /workspace/store"));
fixture.Preparer.PrepareCount.Should().Be(2);
```

Transient failure must contain no store removal command.

- [ ] **Step 4: Run pooled tests and confirm RED**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~DaemonReviewStageExecutorPooledTests" --nologo
```

Expected: current executor reads/prepares/diffs with host capabilities and provisions only at review time.

- [ ] **Step 5: Reorder `TryPooledFetchContextAsync`**

Implement this exact order:

```csharp
var slot = await _slotWorkspace.Pool.LeaseAsync(ct);
var session = await _provisioner.GetOrCreateRequiredForSlotAsync(run, slot, ct);
var preparer = _slotWorkspace.CreatePreparer(session);
await preparer.EnsureStoreAsync(StoreRoot, storeUrl, ct);
var submodule = await ResolveStoreSubmodulePathAsync(session.FileSystem, StoreRoot, repo, provider);
var prepared = await PrepareWithRecoveryAsync(
    preparer, run, StoreRoot, $"{SandboxWorkspaceRoot}/{_options.ScratchDirName}", ...);
var sdkGit = new GitRunner(session.CommandRunner);
var diff = await sdkGit.RunAsync(... prepared.TargetDir ...);
var manifest = await BuildFileManifestAsync(sdkGit, prepared.TargetDir, ct);
```

If the repo is not a store submodule, destroy the just-created session before returning the slot and using the existing non-pooled fallback. Do not use host setup.

- [ ] **Step 6: Retain and reuse `ReviewRunSession`**

Store the session in `LeasedReview`. In `BuildToolContextAsync`, when a pooled lease exists, build the tool context from `lease.Session` rather than calling the provisioner again. Keep non-pooled behavior unchanged.

- [ ] **Step 7: Preserve S2S behavior explicitly**

The S2S path cannot use the daemon-owned same-session preparer. Branch before the new in-process session-first path so existing S2S pooled preparation remains on its current host/LmStreaming ownership until a separate design addresses it. Add/retain S2S tests asserting:

- LmStreaming adopts the slot;
- no daemon `GetOrCreateRequiredForSlotAsync` call occurs;
- deep-link behavior remains unchanged.

This is the one intentional exception to the new SDK rule, matching the approved spec's non-goal.

- [ ] **Step 8: Wire session-bound preparer factory in Program**

```csharp
return new ReviewSlotWorkspace(
    pool,
    session => new ReviewSlotPreparer(
        new GitRunner(session.CommandRunner),
        session.FileSystem,
        provider: "ado-or-github-resolved-per-run",
        loggerFactory),
    hostRunner,
    hostFileSystem);
```

Do not hard-code `"github"` for the MCQdb store. Either pass provider into `CreatePreparer`:

```csharp
Func<ReviewRunSession, string, IReviewSlotPreparer> CreatePreparer
```

or remove provider constructor state and pass provider into `PrepareAsync`. Prefer the latter if it makes the preparer stateless across providers. The live MCQdb path must construct `SubmoduleInitializer` with `ado`/`azure-devops`, not `github`.

- [ ] **Step 9: Keep terminal host gate after destroy**

Update renamed properties in `CommitPooledNotesAsync`, prior-notes/KB host reads, and `SlotHygiene.StripAsync`. Assert the order remains:

```text
destroy → host notes write/stage/commit/push → host strip → return
```

No host git runs before destroy on the in-process pooled path.

- [ ] **Step 10: Run pooled/S2S tests**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~DaemonReviewStageExecutorPooledTests|FullyQualifiedName~ReviewToolContextBuildTests" --nologo
```

Expected: all pass.

- [ ] **Step 11: Commit**

```bash
git add samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs \
  samples/CodeReviewDaemon.Sample/Program.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Infrastructure/FakeSandboxCommandRunner.cs
git commit -m "feat(daemon): set up pooled reviews in the run sandbox"
```

---

### Task 5: Prove commit-gate isolation and cleanup on every path

**Files:**
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs`
- Modify: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/RunCleanupTests.cs`
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` only if tests reveal ordering gaps

**Interfaces:**
- Consume: `LeasedReview.Session`, `ReviewSlotWorkspace.HostCommitRunner`, and `.HostCommitFileSystem` from Task 4.
- Preserve: `ReleaseReviewLeaseAsync(long runId, CancellationToken)` exactly-once cleanup contract.

- [ ] **Step 1: Add strict host-phase command test**

Record each operation with phase labels and assert:

```csharp
fixture.CleanupOrder.Should().ContainInOrder(
    "destroy",
    $"host-write:{NotesRelPath}/review.md",
    $"host-add:{NotesRelPath}",
    "host-commit",
    "host-push",
    "host-strip",
    "return");

fixture.HostRunner.Commands.Select(Join).Should().NotContain(c =>
    c.Contains(" clone ") || c.Contains(" fetch origin base-sha head-sha") || c.Contains(" diff "));
```

- [ ] **Step 2: Add setup-failure cleanup test**

Script SDK preparation to fail after provisioning. Assert:

- session destroyed once;
- slot returned once;
- host commit runner has no commands;
- no review/context artifact persists.

- [ ] **Step 3: Add terminal-failure cleanup test**

Drive the real orchestrator through ContextReady then fail before Posted. Assert `ReleaseReviewLeaseAsync` destroys the same session before return and does not commit incomplete notes.

- [ ] **Step 4: Run cleanup tests and confirm RED/GREEN**

Run:

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --filter "FullyQualifiedName~DaemonReviewStageExecutorPooledTests|FullyQualifiedName~RunCleanupTests" --nologo
```

Expected after minimal fixes: all pass.

- [ ] **Step 5: Commit**

```bash
git add samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/DaemonReviewStageExecutorPooledTests.cs \
  tests/CodeReviewDaemon.Sample.Tests/Orchestration/RunCleanupTests.cs
git commit -m "test(daemon): prove sandbox setup and host commit isolation"
```

---

### Task 6: Build, format, and run the full regression suite

**Files:**
- Modify only files required to fix failures introduced by Tasks 1-5.
- Update: `scratchPad/conversation_memories/code-review-sample-uses-s2s-api/implementation-progress.md`

- [ ] **Step 1: Run focused daemon tests**

```bash
dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --nologo
```

Expected: 0 failed. Any failure is ours to fix; do not classify it as pre-existing.

- [ ] **Step 2: Run whitespace formatting verification**

```bash
dotnet format whitespace samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj \
  --verify-no-changes --no-restore
dotnet format whitespace tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj \
  --verify-no-changes --no-restore
```

Expected: no output, exit 0. If formatting fails, run the formatter without `--verify-no-changes`, inspect the diff, then re-run verification.

- [ ] **Step 3: Run full solution tests with logs**

```bash
dotnet test LmDotnetTools.sln \
  --logger "trx;LogFileName=results.trx" \
  --results-directory .logs/test-results
```

Expected: every project 0 failed; only existing intentional skips.

- [ ] **Step 4: Record measured proof**

Append a scratchpad section containing:

- exact RED failures proving old host-first behavior;
- exact GREEN test counts;
- assertion that host git did not prepare/diff;
- assertion that setup and review shared one session;
- full solution result.

- [ ] **Step 5: Commit regression fixes/proof**

```bash
git add <only changed source/test files> \
  scratchPad/conversation_memories/code-review-sample-uses-s2s-api/implementation-progress.md
git commit -m "test(daemon): verify SDK-backed pooled setup"
```

- [ ] **Step 6: Push branch and update PR #230**

```bash
git push origin wt4
gh pr view 230 --repo achieveai/LmDotnetTools --json headRefOid,statusCheckRollup,url
```

Expected: PR head equals local HEAD and CI starts for the new commits.

---

### Task 7: Run MCQdbDEV PR #11229 through the corrected daemon

**Files:**
- Do not modify tracked configuration for one-run targeting.
- Runtime artifacts: `B:/sources/LmDotnetTools/.run/mcqdb-sdk-11229-*` (git-ignored).
- Update after verification: `scratchPad/conversation_memories/code-review-sample-uses-s2s-api/implementation-progress.md`.

**Interfaces:**
- Target identity: `mcqdbdev/MCQdb_Development/MCQdbDEV`.
- PR: `11229`.
- Expected head: `eb51ebf2b5c75aa5509a4e45bc9f8a9af5caedd1`.
- Posting: enabled; leave the ADO review visible.

- [ ] **Step 1: Verify target and topology without mutating workspaces**

```powershell
az repos pr show --id 11229 `
  --organization https://dev.azure.com/mcqdbdev `
  --output json
```

Assert active, target `refs/heads/dev`, and source head equals the expected SHA. Read the ADO `MCQdbReview` default branch through API/git metadata and verify `.gitmodules` maps `repos/MCQdbDEV` to the canonical `dev.azure.com` URL. Do not clone or repair the slot manually.

- [ ] **Step 2: Build/publish the corrected daemon if necessary**

```bash
dotnet build samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj --no-restore
```

Expected: 0 warnings/errors introduced by this work.

- [ ] **Step 3: Scope the run to PR #11229**

Do not use a fresh unrestricted DB with 26 active PRs. Choose one of these existing-code-compatible methods after inspecting poller seams:

1. preferred: run with the production DB after confirming #11229 is the only new eligible head and immediately stop after its terminal event;
2. if deterministic single-PR targeting already exists, use it;
3. otherwise seed an isolated DB's poll cursor/run state so all other active heads are already terminal and #11229 is the only new run.

Do not add a permanent single-PR production feature solely for this gate unless no existing seam can safely scope it.

- [ ] **Step 4: Start daemon and watch SDK setup evidence**

Launch with `--review mcqdb` and runtime-only log/DB overrides if using an isolated state. Never print token contents.

Required early log order:

```text
leased slot
created/mounted review-run session on review-pool-mcqdb/slot-N
Bound typed sandbox client ... session ...
clone/reuse MCQdbReview through SDK runner
prepare branch review/mcqdbdev-11229
checkout eb51ebf2...
reviewing
```

If any log shows host-side clone/preparation before SDK binding, stop and fix; do not accept the run.

- [ ] **Step 5: Inspect the live mounted session**

Before terminal teardown, use gateway/SDK diagnostics to run read-only checks in the same session:

```text
git -C /workspace/store remote get-url origin
git -C /workspace/store config -f .gitmodules --get submodule.repos/MCQdbDEV.url
git -C /workspace/store/repos/MCQdbDEV rev-parse HEAD
```

Expected:

- MCQdbReview canonical URL;
- MCQdbDEV canonical `dev.azure.com` URL;
- exact SHA `eb51ebf2b5c75aa5509a4e45bc9f8a9af5caedd1`.

Do not modify the checkout during inspection.

- [ ] **Step 6: Wait for terminal review and stop before the next PR**

Watch for PR #11229 `Posted` or failure. On `Posted`, stop the daemon immediately. Confirm no new `review_run` was created for #11227 during this validation window.

- [ ] **Step 7: Verify ADO delivery via provider API**

Use Azure DevOps REST/CLI to list PR #11229 threads. Verify a new Revobot (MCQdb) review exists, matches the reviewed head/idempotency marker, and contains substantive review text. Local DB `Posted` alone is insufficient.

Leave the thread posted as approved.

- [ ] **Step 8: Verify host commit gate**

Inspect the pushed `review/mcqdbdev-11229` branch in MCQdbReview. Its new commit must contain only `PRs/mcqdbdev-11229/**`; `.gitmodules`, `repos/MCQdbDEV` gitlink, Knowledge Base, and scratch must not be changed by the review retention commit.

- [ ] **Step 9: Record and commit live evidence**

Append:

- daemon PID/start-stop timestamps;
- run/session/slot IDs;
- SDK binding timestamp before first setup command;
- store/submodule/head checks;
- ADO thread URL/id;
- notes branch commit and scoped file list;
- confirmation no other PR was reviewed.

```bash
git add scratchPad/conversation_memories/code-review-sample-uses-s2s-api/implementation-progress.md
git commit -m "docs(daemon): record MCQdb SDK setup verification"
git push origin wt4
```

---

## Plan self-review

- **Spec coverage:** Tasks 1-5 cover strict mounting, address-only pool, SDK clone/hygiene/prepare, same-session setup/review, fail-closed behavior, host-only commit/push, and cleanup ordering. Task 6 covers regression/format/full-suite proof. Task 7 covers the exact live MCQdb target, API verification, posted retention, and stopping before other PRs.
- **Placeholder scan:** no deferred or unspecified implementation step remains. Task 7's scoping choice is bounded to three concrete existing-code-compatible methods and forbids an unrestricted fresh DB.
- **Type consistency:** `GetOrCreateRequiredForSlotAsync`, `ReviewSlotWorkspace.CreatePreparer`, and `LeasedReview.Session` are introduced before use. `ReviewSlotPreparer` methods consistently use container-rooted paths. Host capabilities are consistently named `HostCommitRunner`/`HostCommitFileSystem` after Task 4.
- **Scope:** one implementation unit: move the in-process pooled setup boundary to typed SDK and prove it on MCQdb. S2S redesign and privileged SDK push are explicitly excluded.
