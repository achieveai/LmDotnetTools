# CodeReviewDaemon SDK-Backed Review-Store Setup

**Date:** 2026-07-29  
**Status:** Approved design  
**First live target:** MCQdbDEV Azure DevOps PR #11229 (`eb51ebf2b5c75aa5509a4e45bc9f8a9af5caedd1`)

## Problem

The daemon advertises typed `SandboxClient` SDK ownership for deterministic sandbox work, but its pooled review path currently prepares the review repository on the host:

- `ReviewSlotPool` invokes a clone callback backed by `HostGitCommandRunner`.
- `ReviewSlotPreparer` is constructed over `HostGitCommandRunner` and `HostFileSystem`.
- `TryPooledFetchContextAsync` resolves `.gitmodules`, fetches, branches, initializes submodules, checks out the PR head, computes the diff, and builds the manifest through those host capabilities.
- Only after preparation does `ReviewSessionProvisioner` mount the slot and expose a `SandboxSessionAdapter` backed by typed `SandboxClient` to the review agent.

This makes the review result operationally valid but fails the required ownership proof: the daemon did not set up the review repository through the Client SDK. It also creates a setup-to-review handoff between different execution boundaries.

The existing MCQdb topology is otherwise valid:

- target: `mcqdbdev/MCQdb_Development/MCQdbDEV`;
- store: `https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbReview`;
- store default branch: `main`;
- reviewed submodule: `repos/MCQdbDEV`, using the mandatory `dev.azure.com` URL and tracking `dev`;
- store contains `KnowledgeBase/`, `PRs/`, and `repos/MCQdbDEV`;
- ADO MSAL token cache exists;
- the daemon's per-app workspace root is `codereview-daemon-mcqdb-7da177c9b5916ebd` under the gateway's configured workspace base.

## Goals

1. Make the typed `SandboxClient` SDK own every pre-review review-store operation:
   - clone/validate/reclone the store;
   - clean stale checkout state;
   - fetch the store remote;
   - reuse or create the persistent PR notes branch;
   - read `.gitmodules`;
   - initialize only allow-listed submodules;
   - fetch and check out the PR base/head commits;
   - clear scratch;
   - compute the bounded diff and file manifest.
2. Use one run-bound gateway session for both setup and review so `/workspace` cannot change between those phases.
3. Preserve the host-only post-review commit/push gate. The ADO write credential must not enter the review session.
4. Fail closed when the leased slot cannot be mounted or SDK preparation cannot be proven. Do not silently downgrade to host setup or an unrelated per-run workspace.
5. Prove the design automatically, then review MCQdbDEV PR #11229 and leave the resulting ADO review posted.

## Non-goals

- Moving the post-review notes commit/push into the sandbox.
- Giving the review session an ADO write credential.
- Redesigning S2S workspace ownership. LmStreaming owns the hosted S2S session; this change targets the in-process pooled path used by the MCQdb profile.
- Changing review methodology, provider/model selection, Knowledge Base semantics, or the ADO posting implementation.
- Reviewing every active MCQdbDEV PR during validation.

## Approved security boundary

### Before review: typed Client SDK

The daemon creates or borrows a gateway session mounted over the leased slot and performs all setup/read/diff work through `SandboxSessionAdapter`. The adapter is a narrow mapping layer over typed `SandboxClient`; the SDK owns the direct operations wire protocol and command execution.

The setup session carries only the existing sandbox egress identity needed to read ADO repositories. It does not receive the host's write credential.

### After review: host commit gate

Once the review is terminal, the daemon destroys the gateway session. Only then may host-side privileged capabilities touch the slot. They may:

1. stage only `PRs/<repo>-<pr>`;
2. commit the retained review notes;
3. push the persistent review branch;
4. strip/reset the slot for reuse;
5. perform existing lifecycle/Knowledge Base maintenance.

The host phase must not prepare the source checkout, calculate the review diff, or influence the files reviewed by the agent.

## Architecture

The approved approach is **session-first leased slots**.

```text
ReviewSlotPool.LeaseAsync
    │ creates only slot mount + scratch addresses
    ▼
ReviewSessionProvisioner.GetOrCreateForSlotAsync
    │ mounts the slot as /workspace
    ▼
ReviewRunSession
    │ CommandRunner + FileSystem = SandboxSessionAdapter = typed SandboxClient
    ├─ validate/clone /workspace/store
    ├─ clean/fetch/checkout review branch
    ├─ parse/init allow-listed submodules
    ├─ fetch/checkout PR head
    ├─ clear /workspace/<scratch>
    └─ diff + manifest
    ▼
Review agent reuses the exact same session and mount
    ▼
Destroy gateway session (quiesce checkout)
    ▼
Host commit gate stages only PRs/<repo>-<pr>, commits, pushes, strips, returns slot
```

Rejected alternatives:

1. **Persistent setup session per slot:** two sessions would share one checkout and could race.
2. **Transient setup session followed by review session:** adds an unnecessary lifecycle handoff and a mutation window between setup and review.
3. **SDK clone only:** leaves fetch/branch/submodule/head/diff host-side and does not satisfy the required boundary.
4. **Everything through SDK:** requires a privileged SDK session or injecting write credentials into the review-adjacent session, expanding the security design without need.

## Component changes

### `ReviewSlotPool`

Keep concurrency, free-list, and stable slot addressing. Change first-lease behavior:

- create the slot mount directory and scratch directory only;
- do not inspect `StorePath` to decide whether it is cloned;
- do not invoke git;
- make reset/reclone a preparation capability rather than a pool filesystem operation.

The pool remains responsible for returning a lease permit after setup failure. It is not responsible for repository validity.

### `ReviewSessionProvisioner`

`GetOrCreateForSlotAsync` becomes mandatory for pooled SDK preparation:

- resolve the slot under the effective workspace base;
- provision the run session before any store read or git command;
- return an error/null that the executor treats as a hard preparation failure when the slot cannot be expressed under the configured base;
- retain the existing same-run session cache so setup and review reuse one `SandboxSessionAdapter`.

The generic per-run fallback remains available to non-pooled call sites but must not be used by the pooled SDK-required path.

### `ReviewSlotWorkspace`

Separate its capabilities explicitly:

- **SDK preparation capability:** create `ReviewSlotPreparer` over the run-bound `ReviewRunSession.CommandRunner` and `.FileSystem`;
- **host commit capability:** retain the privileged `HostRunner`/`HostFileSystem` for the terminal commit gate and lifecycle maintenance only.

This makes accidental pre-review use of host git visible in types and tests.

### `ReviewSlotPreparer`

Preserve its deterministic orchestration and hardened argument-vector git calls, but run it over the SDK-backed adapters and container paths:

- store root: `/workspace/store`;
- target: `/workspace/store/<submodule path>`;
- notes: `/workspace/store/PRs/<repo>-<pr>`;
- scratch: `/workspace/<scratch>`.

Add an SDK-side ensure-store step:

1. check whether `/workspace/store/.git` represents a valid checkout;
2. if missing/empty, `git clone <storeUrl> /workspace/store`;
3. if definitely corrupt, remove/recreate the store through explicit SDK command operations and clone again;
4. if transient/unknown, surface the failure without destructive reclone.

`SlotHygiene` semantics remain: clean stale locks/dirty state on entry; corruption may trigger one reclone/retry.

### `DaemonReviewStageExecutor.TryPooledFetchContextAsync`

Reorder the pooled context stage:

1. lease slot;
2. provision the run session over the slot;
3. construct SDK-backed preparer from that session;
4. ensure/prepare store through SDK;
5. resolve `.gitmodules` through SDK filesystem;
6. compute branch and paths;
7. fetch/checkout through SDK;
8. calculate diff and manifest through SDK runner;
9. persist container-rooted context artifact;
10. hand the same session and lease to review.

No pre-review operation may use `ReviewSlotWorkspace.HostRunner` or `.HostFileSystem`.

### Terminal handling

For the in-process pooled path:

1. destroy the run session before host git touches the slot;
2. commit notes through the existing host gate;
3. stage only the approved notes path;
4. push the review branch;
5. strip/reset through host git;
6. return the slot.

On setup failure, destroy any created session before returning the slot.

## Failure semantics

### Fail closed

When pooled Client SDK preparation is required:

- an unmountable slot is an error;
- a null SDK session is an error;
- session/mount loss is an error, not an empty directory;
- no host-side setup fallback is allowed;
- no per-run workspace downgrade is allowed.

This prevents a plausible review of an empty or different checkout.

### Retry classes

- **Transient ADO/auth/network/throttle:** retain store, destroy session, return slot, and allow stage retry.
- **Definite corruption:** SDK-side delete/reclone once, then retry preparation once.
- **Repeated corruption:** fail stage loudly; normal retry governor bounds subsequent attempts.
- **Cancellation:** destroy session best-effort, return slot, propagate cancellation.
- **Host commit/push failure:** keep current outbox/retry behavior; setup correctness has already been established independently.

### Ordering invariants

- Setup starts only after the run session mounts the leased slot.
- Review starts only after SDK setup and context persistence finish.
- Host commit starts only after session destruction.
- Slot return occurs exactly once on every path.

## Automated verification

1. **Pool address-only behavior:** lease creates slot/scratch addresses without git or store inspection.
2. **Executor ordering:** `GetOrCreateForSlotAsync` occurs before `.gitmodules`, git preparation, diff, or manifest.
3. **Same-session identity:** setup and review tool context receive the same session ID, command runner, and filesystem.
4. **No host setup:** configure fake host git to throw if invoked before the terminal commit phase; context preparation still succeeds.
5. **Fail closed:** unmountable slot/null SDK session prevents review and returns the lease.
6. **SDK clone:** missing store causes clone through the SDK-backed runner.
7. **Recovery:** definite corruption causes one SDK-side reclone/retry; transient failure does not reclone; session loss does not become an empty read.
8. **Container paths:** persisted context uses `/workspace/store`, `/workspace/store/repos/MCQdbDEV`, and `/workspace/store/PRs/...` consistently.
9. **Commit gate regression:** after session teardown, host git stages only `PRs/<repo>-<pr>`; source checkout/submodule/scratch cannot be included.
10. **Regression suite:** daemon tests and full solution pass with no newly accepted failures.

## Live MCQdbDEV verification

Target only PR #11229:

- title: `updating upload pipline`;
- source: `developers/va/17606_slm_dup_check_in_upload_pipeline`;
- target: `dev`;
- head: `eb51ebf2b5c75aa5509a4e45bc9f8a9af5caedd1`.

Run procedure:

1. Use the existing ADO MSAL cache and `appsettings.mcqdb.json` topology.
2. Use an isolated validation DB/log or another deterministic target filter so the run cannot continue through all active PRs.
3. Start the daemon; do not touch the existing LmStreaming.Sample process on port 5050 because MCQdb uses the in-process review path.
4. Prove in logs that `SandboxSessionAdapter` binds typed `SandboxClient` before store clone/preparation.
5. Inspect the live session/mount through the gateway and prove:
   - `/workspace/store` is `MCQdbReview`;
   - `.gitmodules` maps `repos/MCQdbDEV` to the `dev.azure.com` target;
   - `repos/MCQdbDEV` is checked out at `eb51ebf2b5c75aa5509a4e45bc9f8a9af5caedd1`.
6. Let review and host commit gate complete.
7. Verify the ADO review thread through the provider API, not only local `Posted` state.
8. Leave the review posted.
9. Stop the daemon immediately after #11229 completes so it cannot review #11227 or other active PRs.

## Acceptance criteria

- No pre-review pooled setup/read/diff operation uses host git/filesystem.
- The same SDK-backed session prepares and reviews the slot.
- Write credentials remain host-only.
- The pooled path cannot silently downgrade away from the leased mount.
- Tests prove ordering, same-session identity, fail-closed behavior, recovery, and the commit gate.
- MCQdbDEV PR #11229 is reviewed at the specified head, its ADO review is API-verified and left posted, and no other PR is reviewed during validation.
