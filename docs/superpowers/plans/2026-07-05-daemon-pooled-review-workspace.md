# Daemon Pooled Review Workspace (Layer 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the daemon's per-run ephemeral clone + read-only reviewer with a warm host-side pool of `AchieveAiReviews` checkouts, a scoped-writable reviewer that takes PR-specific notes, and a persistent per-PR branch that carries memory across re-reviews and merges-or-deletes on PR close.

**Architecture:** Four phases per review — lease a warm slot → privileged host-side prepare (fetch, PR branch, submodule→head, scratch wipe, perms) → scoped agent phase (Read/Grep/Glob/Skill + scoped Write/Edit/Bash; code RO, notes+scratch RW) → host-side commit-notes gate (daemon commits only `PRs/<pr>/`). A poller lifecycle sweep merges the PR branch on close and deletes it on abandon. Two execution identities: privileged daemon git (host-side, write credential) vs restricted `sandbox` agent tools (gateway MCP).

**Tech Stack:** C# / .NET 9, xUnit + FluentAssertions, the daemon's existing sandbox seams (`ISandboxCommandRunner`, `ISandboxFileSystem`, `GitRunner`, `HostGitCommandRunner`), the Docker sandbox gateway MCP.

**Spec:** `docs/superpowers/specs/2026-07-05-daemon-pooled-review-workspace-design.md`

## Global Constraints

- Common code stays in `samples/CodeReviewDaemon.Sample` / `samples/LmSampleShared` — do **not** push daemon-specific code into `src/…`.
- Never add a Co-Authored-By / AI signature to commits.
- Never bypass hooks (`--no-verify`); if the worktree lacks husky, run `dotnet tool restore && dotnet husky install` once.
- CSharpier / `.editorconfig` formatting; the husky `dotnet format whitespace` pre-commit gate must pass.
- The daemon owns all outward actions (posting, pushing, merging). The agent never holds the write credential and cannot push.
- Collect-only posting default is unchanged (`EnableCommentPosting`); the notes-branch push is host-side retention, gated on a configured store URL.
- Work in the existing worktree `.worktrees/pooled-review-workspace` on branch `feat/daemon-pooled-review-workspace`.
- Follow existing test patterns: `FakeSandboxCommandRunner`, `FakeSandboxFileSystem`, `tests/CodeReviewDaemon.Sample.Tests/…`.

---

## File Structure

**Create**
- `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlot.cs` — the slot value + pool.
- `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs` — privileged prep phase.
- `samples/CodeReviewDaemon.Sample/Agents/ScopedToolFilter.cs` — read-only + scoped-write tool registry builder.
- `samples/CodeReviewDaemon.Sample/Orchestration/PrLifecycleSweeper.cs` — merge-on-close / delete-on-abandon.
- Test files mirroring each under `tests/CodeReviewDaemon.Sample.Tests/…`.

**Modify**
- `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs` — pool + scoped-tool + notes-branch options.
- `samples/CodeReviewDaemon.Sample/Workspace/Git/ReviewBotRepoManager.cs` → split into `CommitNotesAsync` / `MergeToDefaultAsync` / `DeleteBranchAsync` (rename to `ReviewBranchManager`).
- `samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs` — expose PR `state` + `merged_at`.
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` — host-side prep + slot lease/return + scoped tool context + commit-notes step.
- `samples/CodeReviewDaemon.Sample/Program.cs` — DI: pool, preparer, sweeper, scoped filter.

---

## Task 1: Spike — resolve the enforcement mechanism (R1–R3)

Research task (no production code). Resolves the §9 open items so later tasks pick the correct enforcement path. Record the outcome in the spec's §9.

**Files:**
- Modify (append findings): `docs/superpowers/specs/2026-07-05-daemon-pooled-review-workspace-design.md`

- [ ] **Step 1: Probe per-path RO mount (R1).** Against the live gateway (`127.0.0.1:3000`), create a session whose workspace mounts a prepared host dir, and check whether the gateway accepts a **read-only** sub-path mount or a `read_only` volume flag. Inspect the created container's mounts:

```bash
# create a session on a prepared host dir, then:
docker inspect <sandbox-container> --format '{{json .Mounts}}' | python -m json.tool
# look for a repos/<Repo> mount with "RW": false, or a workspace "read_only": true option
```
Expected: record whether the gateway can present `repos/<Repo>` RO while `PRs/`+scratch stay RW.

- [ ] **Step 2: Probe `chmod` enforcement on the bind mount (R1 fallback).** In a session, `chmod 0555` a file on the bind-mounted workspace as root, then attempt a write as the `sandbox` user:

```bash
docker exec -u root <c> sh -lc 'chmod 0555 /workspace/store/README.md'
docker exec -u sandbox <c> sh -lc 'echo x >> /workspace/store/README.md; echo rc=$?'
```
Expected: record whether `rc` is non-zero (perms enforce) or zero (NTFS mount ignores perms).

- [ ] **Step 3: Probe Bash egress denial (R3).** In a session, confirm the agent's Bash cannot reach the network while the daemon's host git can:

```bash
# via /mcp Bash tool (agent identity):
curl -s .../mcp -H 'X-Session-ID: <sid>' -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"Bash","arguments":{"command":"curl -sS -m 5 https://api.github.com >/dev/null; echo rc=$?"}}}'
```
Expected: record whether egress is denied for the agent Bash by default / via a network rule.

- [ ] **Step 4: Record the decision.** Append an "R1–R3 resolution" block to spec §9 stating the **primary enforcement** chosen: (a) RO mount if the gateway supports it; else (b) `chmod` if it enforces; else (c) egress-block + no-credential + commit-gate + `ScopedToolFilter` `Write`-path wrapper as the boundary. This selects whether `ReviewSlotPreparer` sets perms via mount options, `docker exec -u root chmod`, or relies on the wrapper.

- [ ] **Step 5: Commit.**

```bash
git add docs/superpowers/specs/2026-07-05-daemon-pooled-review-workspace-design.md
git commit -m "docs(daemon): record R1-R3 enforcement spike outcome for pooled workspace"
```

---

## Task 2: Config options for the pool + scoped tools + notes branch

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Configuration/CodeReviewDaemonOptionsTests.cs`

**Interfaces:**
- Produces: `ReviewPoolSize` (int, default 2), `ReviewPoolHostRoot` (string?), `ScratchDirName` (string, default `"scratch"`), `EnableReviewerWrites` (bool, default false), `WritableToolAllowList` (`IReadOnlyList<string>`, default `["Write","Edit","Bash"]`), `MergeNotesBranchOnClose` (bool, default true).

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void Pool_and_scoped_tool_defaults_are_conservative()
{
    var o = new CodeReviewDaemonOptions();
    o.ReviewPoolSize.Should().Be(2);
    o.EnableReviewerWrites.Should().BeFalse("writes are an explicit opt-in");
    o.WritableToolAllowList.Should().BeEquivalentTo(new[] { "Write", "Edit", "Bash" });
    o.MergeNotesBranchOnClose.Should().BeTrue();
    o.ScratchDirName.Should().Be("scratch");
}
```

- [ ] **Step 2: Run to verify it fails.** `dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj --filter "Pool_and_scoped_tool_defaults_are_conservative"` → FAIL (members don't exist).

- [ ] **Step 3: Add the options** (init properties with the doc-comment style already in the file), e.g.:

```csharp
/// <summary>Warm review-checkout slots kept ready to skip re-cloning. Default 2.</summary>
public int ReviewPoolSize { get; init; } = 2;

/// <summary>Host root the review-checkout pool slots live under; defaults beside the binary.</summary>
public string? ReviewPoolHostRoot { get; init; }

/// <summary>Ephemeral scratch dir name (sibling of the store clone), wiped per lease.</summary>
public string ScratchDirName { get; init; } = "scratch";

/// <summary>When true, the reviewer gets scoped Write/Edit/Bash to take PR notes + do
/// file-level diffs (code stays read-only; writes scoped to the PR notes dir + scratch).</summary>
public bool EnableReviewerWrites { get; init; }

/// <summary>Extra tool names granted when <see cref="EnableReviewerWrites"/> is on.</summary>
public IReadOnlyList<string> WritableToolAllowList { get; init; } = ["Write", "Edit", "Bash"];

/// <summary>Merge the persistent PR notes branch into the store default branch on PR close.</summary>
public bool MergeNotesBranchOnClose { get; init; } = true;
```

- [ ] **Step 4: Run to verify it passes.** Same filter → PASS.

- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): add pool + scoped-tool + notes-branch config options"`

---

## Task 3: Extend `GitHubPrProvider` to expose PR state + merged_at

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/GitHubPrProviderTests.cs`

**Interfaces:**
- Produces: on the PR DTO the provider returns, `string State` (`"open"`/`"closed"`) and `DateTimeOffset? MergedAt`; plus `Task<PrState> GetPrStateAsync(RepoIdentity, string prId, CancellationToken)` returning `enum PrLifecycle { Open, Merged, Abandoned }`.

- [ ] **Step 1: Write the failing test** — a closed+merged PR classifies `Merged`, closed+unmerged classifies `Abandoned`, open classifies `Open`, driven through the existing `FakeHttpMessageHandler` returning canned GitHub JSON (`{"state":"closed","merged_at":"2026-..."}` etc.). Follow the existing `GitHubPrProviderTests` fixture.
- [ ] **Step 2: Run to verify it fails.**
- [ ] **Step 3: Implement** — parse `state` + `merged_at` from the PR JSON; add `GetPrStateAsync` mapping `(state, merged_at) → PrLifecycle` (`open→Open`, `closed & merged_at!=null→Merged`, `closed & merged_at==null→Abandoned`).
- [ ] **Step 4: Run to verify it passes.**
- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): GitHubPrProvider exposes PR state/merged_at + lifecycle"`

---

## Task 4: Refactor `ReviewBotRepoManager` → `ReviewBranchManager` (split ops)

Split the current one-shot "commit + ff-merge + delete" into three independently-callable ops so the notes branch can **persist** across re-reviews and merge/delete only on PR close.

**Files:**
- Modify/rename: `samples/CodeReviewDaemon.Sample/Workspace/Git/ReviewBotRepoManager.cs` → `ReviewBranchManager.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/ReviewBotRepoManagerTests.cs` → `ReviewBranchManagerTests.cs`

**Interfaces:**
- Produces:
  - `Task<CommitNotesResult> CommitNotesAsync(string repoRoot, string branch, string defaultBranch, IReadOnlyList<ReviewArtifactFile> notes, string commitMessage, CancellationToken)` — checkout/create `branch` from `defaultBranch` **only if absent** (else reuse), write notes, `add PRs/<pr>/…` (scoped), single commit, push `branch`, **keep** it. Returns pushed SHA or `GitSyncFailed`.
  - `Task<bool> MergeToDefaultAsync(string repoRoot, string branch, string defaultBranch, CancellationToken)` — merge `branch` into `defaultBranch` (ff-only, else merge commit), push default, then delete `branch` (local+remote). Rebase-retry as today.
  - `Task DeleteBranchAsync(string repoRoot, string branch, CancellationToken)` — delete local+remote, idempotent (missing branch is a no-op).

- [ ] **Step 1: Write failing tests** for the three ops against `FakeSandboxCommandRunner`: (a) `CommitNotes` on a NEW branch runs `checkout -B <branch> <default>`, stages only the notes paths, commits, pushes `<branch>`, and does **not** run `branch -D`; (b) `CommitNotes` on an EXISTING branch (`rev-parse --verify <branch>` succeeds) does **not** re-create from default; (c) `MergeToDefault` merges + pushes default + deletes branch; (d) `DeleteBranch` is idempotent when the branch is absent.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** by splitting the existing `PublishAsync` body. Keep `BuildReviewBranchName`, `TryPushWithRebaseAsync`, slug helpers. `CommitNotesAsync` = steps 1–3 + push-keep; `MergeToDefaultAsync` = steps 4–7; `DeleteBranchAsync` = step 7 alone (guarded). Update `DaemonReviewStageExecutor` + `Program.cs` references (compile-fix only; behavior wired in Task 8).
- [ ] **Step 4: Run to verify they pass** + the existing daemon suite still green.
- [ ] **Step 5: Commit.** `git commit -m "refactor(daemon): split ReviewBotRepoManager into persistent ReviewBranchManager ops"`

---

## Task 5: `ReviewSlotPool`

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlot.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPoolTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ReviewSlot(int Index, string HostPath, string StorePath, string ScratchPath)`.
  - `sealed class ReviewSlotPool` with `Task<ReviewSlot> LeaseAsync(ReviewRun run, CancellationToken)` and `Task ReturnAsync(ReviewSlot slot, CancellationToken)`. Ctor takes `ReviewPoolSize`, `ReviewPoolHostRoot`, `ScratchDirName`, a store-clone callback `Func<ReviewSlot, CancellationToken, Task>` (clone if the slot's store dir is missing), and a logger.

- [ ] **Step 1: Write failing tests:** (a) first lease creates slot 0, calls the clone callback once (store dir absent), returns `HostPath=…/slot-0`, `StorePath=…/slot-0/store`, `ScratchPath=…/slot-0/scratch`; (b) a returned slot is re-leased **without** re-cloning (store dir now present → callback not called); (c) with `ReviewPoolSize=1`, a second concurrent lease awaits the first's return (serial gate); (d) `LeaseAsync`/`ReturnAsync` are idempotent-safe.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** a free-list + `SemaphoreSlim`(N) gate. `LeaseAsync`: await a permit, pop/allocate a slot index, ensure the store clone (callback if `!Directory.Exists(StorePath)`), return the slot. `ReturnAsync`: mark free, release the permit. Paths from `ReviewPoolHostRoot ?? Path.Combine(AppContext.BaseDirectory, "review-pool")`.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): warm review-checkout slot pool"`

---

## Task 6: `ReviewSlotPreparer` (privileged prep phase)

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Workspace/ReviewSlotPreparer.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Workspace/ReviewSlotPreparerTests.cs`

**Interfaces:**
- Consumes: `GitRunner` (over the host runner), `ISandboxFileSystem`, `SubmoduleInitializer`, `DaemonOperationPolicy`, `ReviewSlot`, `ReviewRun`, `RepoIdentity`.
- Produces: `Task<PreparedCheckout> PrepareAsync(ReviewSlot slot, ReviewRun run, RepoIdentity repo, string provider, string storeUrl, CancellationToken)` returning `sealed record PreparedCheckout(string StoreRoot, string TargetDir, string NotesDir, string Branch)` where `NotesDir = PRs/<provider>/<slug>/<pr>`.

- [ ] **Step 1: Write failing tests** against `FakeSandboxCommandRunner`+`FakeSandboxFileSystem` for the git sequence, in order: `fetch origin`; branch resolve — when `rev-parse --verify origin/<branch>` succeeds → `checkout <branch>`, else `checkout -B <branch> <default>`; submodule allow-list init of `repos/<Repo>`; `-C repos/<Repo> fetch origin <base> <head>` + `checkout --force <head>`; scratch wipe (`FileSystem` delete/recreate `scratch`); (perms step per Task 1 outcome — assert the chosen command runs). Assert `PreparedCheckout` paths.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** reusing the existing `TryStoreCheckoutAsync` submodule logic (extract the shared helper) but rooted at the slot's `store/`, plus branch-resolve + scratch-wipe + perms.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): privileged slot preparer (branch, submodule head, scratch, perms)"`

---

## Task 7: `ScopedToolFilter`

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Agents/ScopedToolFilter.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Agents/ScopedToolFilterTests.cs`

**Interfaces:**
- Produces: `static void Apply(FunctionRegistry source, FunctionRegistry target, IReadOnlyList<string> readOnlyAllow, IReadOnlyList<string> writableAllow, string notesDir, string scratchDir)` — keeps read-only tools, adds writable tools, and (per Task 1) wraps `Write`/`Edit` so a `file_path` outside `notesDir`/`scratchDir` is rejected before the gateway call.

- [ ] **Step 1: Write failing tests:** (a) with `writableAllow` empty → identical to today's `ReadOnlyToolFilter` (Read/Grep/Glob/Skill kept, no Write/Edit/Bash); (b) with `writableAllow=[Write,Edit,Bash]` → those are added; (c) a wrapped `Write` to `PRs/<pr>/note.md` is allowed, a `Write` to `repos/<Repo>/x.cs` or `/etc/x` is **rejected** with a scoped-path error (assert via the wrapper's handler result).
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** by extending the `ReadOnlyToolFilter` pattern: copy kept contracts; for each writable tool, either pass through (`Bash` — bounded by OS/egress) or wrap (`Write`/`Edit` — validate `arguments.file_path` starts with `notesDir` or `scratchDir`, else return a tool error).
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): scoped write/bash tool filter for the reviewer"`

---

## Task 8: `PrLifecycleSweeper`

**Files:**
- Create: `samples/CodeReviewDaemon.Sample/Orchestration/PrLifecycleSweeper.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Orchestration/PrLifecycleSweeperTests.cs`

**Interfaces:**
- Consumes: `ReviewStore` (reviewed runs + their PR ids/branches), the provider's `GetPrStateAsync` (Task 3), `ReviewBranchManager` (Task 4), a host `HostRetentionWorkspace`.
- Produces: `Task SweepAsync(CancellationToken)` — for each reviewed PR with a notes branch: `Open`→no-op; `Merged`→`MergeToDefaultAsync`; `Abandoned`→`DeleteBranchAsync`. Idempotent + degrade-not-throw per PR.

- [ ] **Step 1: Write failing tests** with a fake provider-state + fake `ReviewBranchManager`: a merged PR triggers exactly one `MergeToDefault`; abandoned triggers `DeleteBranch`; open triggers neither; a second sweep after merge is a no-op (branch already gone); a per-PR failure is logged and does not abort the sweep.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** the sweep loop with try/catch per PR; only act when `MergeNotesBranchOnClose` (merge) — abandon-delete always runs.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): PR-lifecycle sweep (merge-on-close / delete-on-abandon)"`

---

## Task 9: Wire the executor + DI (pool + prep + scoped context + commit-notes)

**Files:**
- Modify: `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`, `samples/CodeReviewDaemon.Sample/Program.cs`, `samples/CodeReviewDaemon.Sample/Agents/LiveReviewAgentLoopFactory.cs`
- Test: `tests/CodeReviewDaemon.Sample.Tests/Scenarios/DaemonReviewStageExecutorTests.cs`, `.../Orchestration/DaemonReviewStageExecutorSessionTests.cs`

**Interfaces:**
- Consumes: `ReviewSlotPool`, `ReviewSlotPreparer`, `ScopedToolFilter`, `ReviewBranchManager`, `PrLifecycleSweeper`.

- [ ] **Step 1: Write failing tests:** (a) `ContextReady` leases a slot + calls `PrepareAsync` and diffs the prepared `TargetDir` (assert via fakes, `StoreRoot`/`TargetDir` recorded on the artifact); (b) with `EnableReviewerWrites=true`, the tool context is built via `ScopedToolFilter` (Write/Edit/Bash kept alongside Read/Grep/Glob/Skill); (c) after `Reviewed`, the daemon calls `CommitNotesAsync` with only `PRs/<pr>/…` paths and does **not** merge; (d) the slot is returned on terminal stage.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement:** replace `ResolveSandboxAsync`/`ReviewSessionProvisioner` per-run provisioning with `ReviewSlotPool.LeaseAsync` + `ReviewSlotPreparer.PrepareAsync`; open the gateway session over the slot; build the tool context via `ScopedToolFilter` when writes enabled; add the commit-notes step in `PostAsync`/after `ReviewAsync`; return the slot on teardown. Register the new services in `Program.cs`; run the sweeper on the poller cadence.
- [ ] **Step 4: Run to verify they pass** + full daemon suite green (`dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj`).
- [ ] **Step 5: Commit.** `git commit -m "feat(daemon): pooled scoped-writable review flow + notes-branch persistence"`

---

## Task 10: Live verification (opt-in)

**Files:** none (runbook).

- [ ] **Step 1** Run the daemon tool-assisted (collect-only) with `EnableReviewerWrites=true`, `ReviewPoolSize=2`, store configured, on a real LmDotnetTools PR.
- [ ] **Step 2** Assert: two reviews reuse the SAME warm slot (no re-clone in logs); the reviewer writes notes into `PRs/<...>/<pr>/` and the daemon commits **only** that to the PR branch (push, branch kept); scratch is wiped between PRs.
- [ ] **Step 3** Assert the security bounds live: an agent `Write` to `repos/<Repo>/…` is denied; agent Bash cannot `git push` / reach the network; the code submodule pointer only moves via the daemon.
- [ ] **Step 4** Simulate lifecycle: mark the PR merged → sweeper merges the notes branch into store `main` + deletes it; a different PR closed-unmerged → branch deleted.
- [ ] **Step 5** Record findings in a memory + the spec; **do not** enable posting.

---

## Self-Review

- **Spec coverage:** pool (T5), lease/prepare (T6), scoped write+Bash (T2 opt-in, T7 filter, T1 enforcement), persistent branch + commit-notes gate (T4, T9), scratchpad (T6 wipe, T2 name), PR-lifecycle merge/delete (T3 state, T4 ops, T8 sweep), two identities (T6 host-git + T7 scoped agent), degrade/error handling (T5/T6/T8), testing (each task). KB + judge loop are Layer 2/3 (out of scope) — correctly absent.
- **Placeholders:** none — each task names exact files, interfaces (signatures/types), representative test intent, and implementation approach grounded in the existing classes. Full method bodies are reused from `ReviewBotRepoManager`/`ReadOnlyToolFilter`/`TryStoreCheckoutAsync` (cited), not re-invented.
- **Type consistency:** `PreparedCheckout`/`ReviewSlot`/`CommitNotesAsync`/`GetPrStateAsync`/`ScopedToolFilter.Apply` names are used consistently across T5–T9. Task 1's outcome selects the perms mechanism referenced in T6/T7 (single source of truth).
