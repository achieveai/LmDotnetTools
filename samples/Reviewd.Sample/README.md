# Reviewd.Sample

Small operator CLI for ADO PR observations and existing Gateway/S2S workspaces. It reuses CodeReviewDaemon's clients and OAuth providers without starting the daemon host.

To run the NOVA reviews (all repos, all slots, CLI only), follow [RUNBOOK.md](RUNBOOK.md).

```bash
dotnet run --project samples/Reviewd.Sample -- --help

dotnet run --project samples/Reviewd.Sample -- pr list --repo org/project/repo --config /path/to/shared/appsettings.json
dotnet run --project samples/Reviewd.Sample -- pr check --repo org/project/repo --db /path/to/reviewd.db --config /path/to/shared/appsettings.json

dotnet run --project samples/Reviewd.Sample -- workspace list --config /path/to/shared/appsettings.json
dotnet run --project samples/Reviewd.Sample -- workspace exec --workspace <id> --directory src --config /path/to/shared/appsettings.json -- python3 check.py

dotnet run --project samples/Reviewd.Sample -- agent start --workspace <id> --directory src --provider <host-provider-id> --prompt 'Describe this directory; do not change files.' --wait --config /path/to/shared/appsettings.json
```

## Existing configuration and sign-in

Use `--config` to read your existing appsettings JSON. Environment variables override it. Without the flag, `appsettings.json` is read from the current directory if present. No secrets are copied into this sample.

| Setting | Purpose |
| --- | --- |
| `Auth:TokenStoreDir` | Explicit path to the already signed-in shared OAuth cache. Required for ADO. |
| `Auth:Ado` | Existing Entra client ID, tenant and scopes. |
| `SandboxGateway:AppId`, `AppKey`, `BaseUrl` | Existing Gateway application identity. Default ID: `codereview-daemon`. |
| `CRD_SANDBOX_APP_ID`, `CRD_SANDBOX_APP_KEY`, `CRD_SANDBOX_GATEWAY` | Same overrides as CodeReviewDaemon. |
| `CodeReviewDaemon:LmStreamingBaseUrl` | Running LmStreaming S2S host; default `http://localhost:5000`. `LMSTREAMING_BASE_URL` overrides it. |
| `CodeReviewDaemon:LmStreamingS2SSecret` | Existing S2S inbound secret. |
| `CodeReviewDaemon:LmStreamingModeId` | Agent mode; default `workspace-agent`. `--mode` overrides it. |

Use standard double-underscore environment names, e.g. `Auth__TokenStoreDir` or `CodeReviewDaemon__LmStreamingS2SSecret`. Sign in through LmStreaming.Sample or CodeReviewDaemon's existing onboarding first. This CLI does not launch sign-in, register applications, or create another credential store. Agents use the running host's model and ADO/GitHub authentication machinery.

For the existing Linux Nova setup, use the **main tree's** `samples/CodeReviewDaemon.Sample/appsettings.nova.json` and the same credential environment as the running daemon. That profile points to S2S port 5000, Gateway port 3000, and its shared Linux OAuth cache. The older `s2s`/`mcqdb` profiles use Windows paths and port 5051; they are not interchangeable.

## Standalone authenticated sandbox commands

These commands use Gateway directly, under the configured app identity. They do not use the review-host workspace catalog or start daemon workers.

```bash
# Preferred: webhook lifetime follows the local task, including its sandbox/agent waits.
reviewd auth run --listen http://127.0.0.1:5203 --callback http://localhost:5203 --config /path/to/shared/appsettings.json -- python3 operator-task.py
# Alternatively, foreground serving lasts until you stop this command.
reviewd auth serve --listen http://127.0.0.1:5203 --callback http://localhost:5203 --config /path/to/shared/appsettings.json
reviewd sandbox create --workspace nova-reviews --callback http://localhost:5203 --marketplaces gb,sandbox-plugins --config /path/to/shared/appsettings.json
reviewd sandbox exec --session <returned-session-id> --timeout 120 --config /path/to/shared/appsettings.json -- git ls-remote https://dev.azure.com/O365Exchange/Weve_DA/_git/NovaClient HEAD
```

- The callback listener must be reachable from Gateway. The local Gateway allowlists HTTP `localhost` and rewrites it to its configured Docker-host address; HTTP `host.docker.internal` itself is rejected at session creation. Verify the remote Gateway's policy rather than assuming this local configuration. For Docker, bind an explicitly approved local bridge address instead of loopback when needed. Remote callbacks require HTTPS. No automatic firewall or Gateway policy changes.
- Keep `auth serve` running while authenticated commands execute. It exposes only the existing `POST /api/auth/webhook/{provider}` route, uses per-session secret validation and destination allowlists, and fails fast when sign-in is required. No login route is added.
- Reuse an explicit existing absolute `Auth:TokenStoreDir`. Tokens are not copied into the sandbox; refresh may update the shared cache. Avoid concurrent token-refresh writers.
- `sandbox create` attaches the existing shared auth/network policy and saves a fresh webhook secret for the returned session. It supports managed ADO/GitHub, not configured keys or M365. Existing session callbacks are not changed. The logical workspace is reused; the session is new and not deleted on exit.
- `sandbox exec` accepts an existing session ID and prints an operation ID before execution. `--timeout` bounds local waiting; the SDK remote command deadline remains two minutes. A timed-out operation must be inspected, not blindly resubmitted.
- Existing `workspace exec` remains the host-catalog-based path and does not automatically attach this webhook. Use the direct `sandbox` commands for daemon-owned workspaces.

## Create and configure workspaces

```bash
# Available marketplace/plugin catalog; add --workspace <id> to show current enablement too.
reviewd workspace plugins

# Create with no plugins enabled.
reviewd workspace create --name MyReview --directory my-review --enabled-plugins '[]'

# Replace the available marketplace set; omitted plugin selection is preserved.
reviewd workspace edit --workspace <id> --marketplaces gb

# Enable just one plugin (use exact marketplace/plugin names from workspace plugins).
reviewd workspace edit --workspace <id> --enabled-plugins '[{"marketplace":"gb","plugin":"code-reviewer"}]'

# Enable all plugins from selected marketplaces, or disable all plugins.
reviewd workspace edit --workspace <id> --enabled-plugins 'null'
reviewd workspace edit --workspace <id> --enabled-plugins '[]'
```

`reviewd` above means the DLL shortcut described in CLI usage; append `--config <path>` when running directly. These commands reuse the existing host APIs. Plugin identity is the **marketplace/plugin pair**, not just a plugin name.

- `--marketplaces` replaces the set. An empty value (`--marketplaces ''`) restores host/Gateway marketplace defaults; it does **not** mean no plugins.
- `--enabled-plugins`: `null` = legacy all/default, `[]` = none, a pair-list = subset. Omit it on edit to preserve selection.
- Edit preserves omitted settings, including environment values. Name and directory are immutable. System workspaces cannot be edited.
- Plugin edits use the freshly read revision. A concurrent edit fails; no automatic retry or overwrite.
- Explicit selection requires Gateway plugin-filtering support. Catalog availability/configured enablement is not proof the plugins loaded successfully.
- **Enabled-plugin edits may replace live sandbox sessions.** Avoid active daemon review workspaces unless you intend to affect those reviews.
- Direct workspace execution passes the stored plugin selection to Gateway. No workspace deletion command is added.

## PR observations

```bash
reviewd pr check --repos org/project/NovaClient,org/project/Astra --db /path/to/reviewd.db --config /path/to/shared/appsettings.json
reviewd pr bind-thread --repo org/project/NovaClient --pr 123 --thread <conversation-id> --db /path/to/reviewd.db
# Save the provisioned conversation before sending its first prompt.
reviewd agent start --workspace <id> --provider <id> --prompt 'Review PR 123' --repo org/project/NovaClient --pr 123 --db /path/to/reviewd.db --config /path/to/shared/appsettings.json
```

- Select exactly one of `--repo` or comma-separated `--repos`. Checks are one-shot; rerun to poll. All identities are validated first, and case-equivalent repositories are deduplicated.
- `pr list` does not save observations. ADO **active** PRs include drafts. `--db` joins existing conversation bindings; an absent database is not created by listing.
- `pr check` stores lifecycle, head SHA, title and activity timestamps in the existing SQLite JSON snapshots. Every row is printed with `new`, `changed` or `unchanged`, repository identity and `threadId`.
- `SourceUpdatedAt` is the latest source-branch **push** time, not a universal PR-modified date. Fork source repositories are not queried; their source timestamp stays unknown. `LastCommentPostedAt` is the latest regular, nondeleted comment's original publication time (including replies/resolved threads), not its edit time. System activity and deleted threads/comments are excluded.
- `ObservedAt` is this poll's observation time. `LastChangedAt` is when a poll detected a lifecycle/head/title/source-time/comment-time difference; it is not an exact server event time. Unknown provider timestamps remain null. Comment bodies/authors are not stored. These fields do not retain full comment or review history.
- Previously open PRs absent from a complete listing are queried individually for lifecycle/head. Absence alone never means completion. Older JSON snapshots remain readable; initial timestamp enrichment can be reported as a change.
- Failed/truncated/malformed required reads leave that repository's snapshot unchanged. With multiple repositories, successful snapshots are saved independently; failed repositories are named and the command exits nonzero. It is not an all-repository transaction.
- Conversation bindings use a separate key in the same database, so polls cannot erase them. `pr bind-thread` may run before the first PR check and needs no OAuth. Same-thread rebinding is idempotent; a different binding is rejected atomically. Existing daemon conversations are not imported automatically—bind the desired conversation explicitly.
- Optional `agent start --repo … --pr … --db …` saves its provisioned conversation before message submission. A send/wait failure retains the binding. An existing binding prevents a new submission; a concurrent binding conflict can leave a provisioned but unused conversation whose ID was printed. Binding failure prevents sending the prompt. This is a single current association, not conversation history.
- Default database: `reviewd.db` in the current directory. Keep it outside the checkout or untracked. Do not run concurrent snapshot checks against the same repository/database. Independent thread bindings use atomic insert-if-absent.

## Pick PRs for review

```bash
reviewd pr pick --repo O365Exchange/Weve_DA/Nova --db .run/reviewd/pending-nondraft.db
```

- Reads saved snapshots and `reviewd/review-run/{repository-key}/{prId}` receipts locally. No OAuth, polling, submission, reservation, or database writes. Output is JSONL; `threadId` identifies the existing conversation to reuse for a re-review.
- Eligible: **active, explicitly non-draft**, with a known source head, and either unreviewed or a different source head **at least 30 minutes after the last verified successful review**. Exactly 30 minutes qualifies. Comment/title changes do not qualify.
- Successful receipts require `stage: completed-verified`, `reviewedHeadSha`, and `reviewCompletedAt` (Unix seconds or ISO timestamp). The cooldown uses review completion, not polling or comment-publication time.
- Unknown draft/head metadata, incomplete/failed/malformed receipts, or bindings without completed receipts are excluded. A missing database/snapshot fails rather than treating history as empty.
- Use the refreshed non-draft operator database. Standard `pr check` still includes drafts and does not populate `IsDraft`; those snapshots are not eligible. Recheck live status/head before submitting. This command does not start reviews or change the immutable conversation binding.

## Restrict new reviews by creation age

Use `--max-age-hours 72` on `pr pick`, `review run`, and `review batch` to admit only PRs created within the rolling previous 72 hours (UTC, inclusive). This is creation age, not last update or comment age. Missing, malformed, offset-free, or future creation timestamps fail closed. Omitting the option preserves the existing behavior.

`pr pick` filters saved snapshot `CreatedAt`; execution rechecks authoritative live ADO `creationDate` before claiming and immediately before the initial review prompt. A PR that ages out during setup retains its claimed owner and artifacts for inspection; no prompt, fabricated success, or automatic release follows.

Age-limited batches require a new immutable selection containing integer `"maxAgeHours": 72`, paired with `--max-age-hours 72`. Missing or mismatched policy on either side is rejected; the policy also binds the persisted execution scope. Fresh expired queue entries become terminal `age-excluded`. A released failed attempt is a fresh admission and remains subject to the age limit. Identified already-admitted reviews may finish, including confirmation and outgoing cleanup; an existing thread, preparation-only attempt, or unresolved send does not authorize that exemption. Existing held, stale, closed, or uncertain owners are not freed or reinterpreted by this policy.

## Operator publication and restart policy

As of 2026-10-09, requested PR review runs are authorized to post appropriate inline feedback, replies and review summaries. The former NOVA collect-only restriction is superseded. This does not authorize source pushes, merges, PR completion or unrelated external actions; actual authentication and publication capability checks still apply.

Restarting the review process means starting fresh: stop or verify quiescence of its writers, reset **every PR review slot** in the configured Gateway workspace/pool through supported cleanup mechanisms, retire stale attempt/ownership state, verify fresh slots, then refill. Do not retain old slot assignments or resume interrupted PR attempts across that restart. Repository roots, shared knowledge, credentials, unrelated data and already-posted comments are outside the reset scope. Historical completed-review records outside slots may remain; they do not reserve slots, and existing posted feedback must not be duplicated.

This is operator policy, not a claim that `review batch` automatically resets its pool on startup. The retained-state and `--resume` contracts below describe existing implementation mechanics. Do not use them to bypass the restart policy; if the supported cleanup path cannot establish quiescence or fresh slots, report that concrete blocker rather than silently resuming retained attempts.

## Run one PR review

```bash
# Preview only. No remote calls, submissions, resets, comments, commits or DB writes.
reviewd review run --repo O365Exchange/Weve_DA/Nova --pr <id> --slot 2 \
  --db .run/reviewd/pending-nondraft.db --session <gateway-session-id> \
  --workspace <host-workspace-id> --provider <provider-id>
# Requested PR review runs authorize comments/replies/summaries under the operator policy above.
# Add --execute only with the intended PR/slot scope and authorization for reset and local artifact commit.
```

`ReviewRunner.RunAsync` runs the reusable **one-PR function**. Pick candidates with `pr pick`; supply an explicit available slot. It is not a scheduler or automatic batch dispatcher.

1. Validate eligibility, host capabilities and session/workspace identity. Re-review reuses the immutable bound conversation, checking its workspace/provider/mode and global idle state.
2. Atomically claim the selected workspace/repository/slot with an immutable SQLite token. Keep PR/slot lifetime leases. Incoming setup requires an already idle native slot; it never resets a predecessor. Run installed setup through Gateway and require complete context plus an unchanged active/non-draft source/merge revision. Save `setup-context.json` inside the review directory.
3. Save thread binding and message idempotency key before submission. Give the hosted agent the absolute **sandbox** context-file path, not instructions to run setup again. The same initial prompt covers remaining context gathering, Bluebird searches, verified review findings, latest discussions, inline feedback/replies and summary publication.
4. Wait for the input, global root run and recursive descendants to finish successfully in two stable observations. Send the publication-confirmation prompt in the **same conversation**, then wait again.
5. Read `publication_receipts.json`; verify fresh thread/comment IDs and exact text against read-only ADO GETs. Require a posted summary, even for no findings. Recheck PR/checkout before committing.
6. Call installed `review_commit.py` through Gateway, passing the selected PR source SHA as `source_sha`. The matching helper must copy outside source evidence into the review directory and commit that directory plus **only the selected submodule pointer**, pinned to the PR source SHA, with normal hooks. The application checkout remains detached at the prepared merge revision; no application commit, branch update or push is permitted. **No push.** Persist verified reviewed head/completion time, publication evidence and artifact commit. Older installed helpers without the explicit source-pin contract fail rather than silently retaining the baseline pointer.

7. After retaining the successful artifact, drain the owner's thread/descendants. Check the exact claim, preserve and hash-verify outside-source evidence, then reset under native same-slot/writer guards. Source dirt or ignored files block destructive cleanup. Verify the clean detached baseline before exact-token release. Review success and cleanup state are separate; failed or uncertain cleanup keeps the claim.

Explicit legacy adoption uses the outgoing PR's retained receipt and thread, not a new PR:

```bash
reviewd review run --repo org/project/Repo --pr <outgoing-pr> --slot <slot> \
  --db pending.db --session <gateway-session> --workspace <workspace> --provider <provider> \
  --cleanup-only --execute
```

This drains historical completed-owner threads and binds exact historical receipt digests to the verified cleanup. Incomplete historical attempts still require their exact release certificates. Native adoption requires the matching prepared PR or an already verified idle baseline. No review turn is resent, no publication is repeated, and the verified review receipt is retained. A pending cleanup operation is never blindly replayed; inspect its original operation before reconciliation.

**Operating boundaries:**
- Reuse a pre-authenticated Gateway session for the selected host workspace, under the same app identity. If its webhook needs a local callback, wrap the command with the existing `auth run`; this runner does not configure credentials, create sessions or start listeners.
- Keep one exclusive operator/database for these slots. The local database lock and recorded-owner checks do not protect against other databases, manual users or external agents writing the same slot.
- Installed `review-setup` helpers and `review_commit.py` must already exist. Nonzero setup exits (including partial-context warnings) stop the attempt; prepared files are preserved for inspection.
- Progress is saved under `reviewd/review-attempt/{repository-key}/{prId}` before side effects. Successful history remains under `reviewd/review-run/...` and is not overwritten by failed attempts. `pr pick` excludes incomplete attempts.
- Cancellation/network/contract failures preserve operation IDs, thread/input IDs and send keys. `--resume` accepts identified review/confirmation stages with matching retained identity. Explicit `verify-pending` recovery requires both original input IDs and retries verification without resending either turn or changing thread metadata. The batch does not automatically select this recovery. Ambiguous reset/send/commit outcomes require reconciliation, not replay.
- Publication verification proves fresh IDs and exact contents exist on this PR. It does not independently judge finding quality, summary semantics or posting identity.
- Validated with local simulated S2S/provider/files flows and focused tests. No live end-to-end execution is claimed.

## Run a durable selected batch

```bash
reviewd review batch --selection selection.json --db pending.db \
  --session <gateway-session-id> --workspace <host-workspace-id> \
  --provider <provider-id> --mode code-review-daemon \
  --listen http://127.0.0.1:5206 --callback http://localhost:5206 --execute
```

- Selection JSON contains `repositories` (repo to rows with `repository`, string `prId`, `headSha`) and `lanes` (repo to distinct slots 0–5). It does not discover replacement or arbitrary new PRs.
- One process serves OAuth and all lanes. Different slots prepare concurrently. Short SQLite claim/release transactions choose owners; lifetime PR/slot leases and native Git locks retain their existing scope. Keep the exclusive operator/database boundary.
- The immutable request digest records provenance, not completion. `reviewd/batch-state/<digest>` checkpoints fixed assignments and retries. Existing retained-state mechanics can resume the queue with the same selection and execution scope, skipping exact-head verified attempts/receipts. This is not the operator restart procedure: apply the all-slot reset policy above before starting fresh. Legacy assignment conflicts park instead of guessing.
- Ordinary failures requeue with capped 15–300 second backoff and fresh provider lifecycle/head/merge checks. Safe sibling lanes continue. An identified admitted owner keeps its slot while retried; its queued followers cannot overtake it.
- Completed/abandoned unadmitted PRs retire without a success receipt. Closed admitted owners hold their slot pending certified release. Drafts/missing merges defer; changed selected heads are not silently replaced. Unknown setup/send/commit outcomes hold ownership. Preparation uncertainty holds only its owning lane. A competing producer still suspends new admission globally. A known completed pre-admission setup failure may preserve evidence, reset only its own native claim, and requeue; missing quiescence or changed ownership holds the lane.
- Batch completion/parking leaves OAuth serving. SIGTERM/Ctrl+C stops admission, waits for admitted local runners while OAuth remains available, then exits. Remote operations can outlive callers; supervisor kill deadlines must be configured separately. No unlimited-drain guarantee.
- Reviews use the requested mode and repo-PR-head8 titles. The follow-up requests a final standalone `verdict:` line; response format is not independently enforced. Artifact/publication verification is not a review-quality verdict.

## Resolve a pinned merge

```bash
# Preview: no remote calls or database writes.
reviewd merge resolve --request merge-request.json --db pending.db \
  --session <gateway-session-id> --workspace <host-workspace-id> --provider <agent-provider-id>
# Add --execute only for an authorized local conflict-resolution attempt.

# Separate publication. Preview first; execute only with source-publication authorization.
reviewd merge push --request merge-request.json --db pending.db \
  --session <same-session> --workspace <same-workspace> --provider <same-agent-provider-id> \
  --result <ready-result-digest>
```

A source request identifies the PR provider separately from the hosted agent provider:

```json
{
  "kind": "source",
  "provider": "azure-devops",
  "org": "organization",
  "project": "project",
  "repo": "repository",
  "prId": "123",
  "repoUrl": "https://dev.azure.com/organization/project/_git/repository",
  "sourceRef": "refs/heads/feature",
  "targetRef": "refs/heads/main",
  "sourceSha": "<full-lowercase-40-character-source-commit>",
  "targetSha": "<full-lowercase-40-character-current-target-commit>",
  "checks": [{ "argv": ["dotnet", "test", "tests/Tests.csproj"], "directory": "." }]
}
```

For GitHub, use `provider: github`, `owner`, `repo`, and the exact HTTPS GitHub repository URL. Fork PRs are not supported. Use fresh branch tips, not a stale provider merge-attempt target.

- Source recovery clones into its own operation-owned scratch checkout through Gateway. It never changes the prepared review slot. Only actual unresolved Git entries enlist the resolver; arbitrary provider, authentication or helper failures do not.
- The resolver cannot establish readiness by saying “success.” Git topology, zero unmerged entries, staged and unstaged whitespace checks, a clean worktree, required checks and an independent exact-tree reviewer determine the result.
- `mergeSuccessful` reports resolved Git conflicts. `readyToPush` additionally requires passing checks and independent review. A missing SDK or failed check stays blocked. Required checks are explicit `dotnet build/test` or `git diff` argv records, not shell text.
- Requests, operations and known agent inputs are retained in SQLite. Resume the same request and scope. Ambiguous preparation, provisioning, send or publication is not automatically repeated. A shell-capable agent's selected directory is not a filesystem jail.
- `merge push` is source-only. It revalidates the reviewed result, PR identity and source/target refs; commits with normal hooks; performs a non-force source-branch push; and reads the remote ref to confirm the commit. It does not complete the PR or update the target branch. Hooks or moved refs block publication. Ambiguous outcomes require inspection, not retry.
- A source update invalidates prior head-pinned review evidence. Select and review the new head separately. Non-force Git push cannot atomically lock both source and target refs against concurrent changes.
- Artifact request previews describe an existing pinned native merge, including `directory`, `rootStartSha`, `reviewTipSha`, `modulePath` and `baselineSha`. Their source/target pins mean root-start/review-tip, not PR branches. **Artifact execution is disabled** until native ownership fences external agent admissions, including keeper failure. A local database lease is insufficient. Native conflict handoff and batch integration are not complete.

## Preview all-slot capacity

```bash
reviewd review capacity --repos organization/project/RepoA,organization/project/RepoB \
  --db pending.db --workspace <host-workspace-id> --session <gateway-session-id> \
  --evidence capacity-evidence.json
```

This local read-only preview checks all historical attempts, receipts, exact release certificates and mutable batch assignments. It never releases, reserves, resets or reshuffles a slot. Missing evidence holds the lane. Supplied evidence must identify `workspaceId`, `sessionId`, `operationId`, `sharedQuiescent`, `producerQuiescent`, and `slots` with full `repository`, integer `slot`, `nativeQuiescent`, `artifactsRetained`, and `quiescentThreads` IDs. Those facts are inputs, not independently certified by the planner. A `candidate` is not execution authorization; recheck evidence and ownership immediately before admission. Certificate-based reuse requires a different PR. Legacy no-admission releases require a different session. An `operator-retired-after-reset` certificate instead names the exact recovery session and may release an admitted or same-session failed owner. `review reconcile-slot --repo <repo> --pr <pr> --db <db> --evidence <proof.json>` validates without writing; add `--execute` only after authorized reset and independent evidence review. The proof binds the raw attempt digest, hashed archive/reset receipts, stopped producers and writers, successful reset postconditions, and timestamp ordering. This command does not perform the reset or authenticate operator attestations. It preserves the original attempt and creates no successful-review receipt. Batch retirement is honored only with its matching certificate.

For an attempt that failed **before acquiring an already-idle native slot**, reconciliation also accepts `classification: preparation-settled-native-idle`. It requires `prepare-pending`, an exact claim token, and no thread, input or send identifiers. The existing `--evidence` proof must hash-bind `nativeIdleReceiptPath`. That receipt identifies the exact attempt digest, workspace/session/repository/PR/slot/claim; identifies every retained preparation/cleanup operation as terminal with an exit code; and certifies released/absent native ownership, no prepared or pool run, stopped producers, quiescent writers and a clean source HEAD matching the 40-character baseline SHA. Its `checkedAt` must be within five minutes and not in the future. `preparationSettled`, `noAdmission`, `threadQuiescent` and `nativeQuiescent` must be true in the proof.

This is **operator-attested evidence, not an authenticated native receipt**. Fresh inspection through Gateway/native APIs and exclusive operator control are prerequisites; the age bound does not close the inspection-to-release race. After independent review and exact execution authorization, `--execute` atomically releases the matching SQLite owner and inserts the immutable certificate. A changed owner or certificate collision rolls back both writes. The original attempt stays byte-for-byte unchanged. Only a **different PR in the same session** may reuse this certificate; no reset, input replay or successful-review receipt is produced. Other reconciliation classifications retain their existing certificate-only behavior.

## Workspace and agent behavior

- Workspace IDs come from the running LmStreaming host's registered catalog, scoped to that host's app. They are not live container IDs or local filesystem paths.
- `workspace exec` passes native argv after `--`. Use an explicit interpreter for scripts. Output goes to stdout/stderr; nonzero remote exit status is returned. This direct execution path does not install an OAuth webhook or private-repository network policy.
- Directories are relative to the remote workspace. Omission selects its root. No Gateway storage is accessed through this machine's filesystem.
- Agent directory selection uses the existing host's `workingDirectoryRelPath` request and `conversationWorkingDirectory` capability. Older hosts fail preflight rather than silently running at the root. The selected directory is a conversation-specific default, **not a filesystem jail**.
- `agent start` prints conversation/input IDs, then waits by default until the submitted run is terminal and every recursive descendant is terminal in two stable observations. Failed/stopped descendants make the command fail; unknown states never count as completion. There is no default overall wait deadline. `--timeout` explicitly limits waiting after provisioning and message submission; `--wait` remains accepted for compatibility. Conversations remain available on the host.
- `--timeout` bounds local waiting, not the remote execution deadline. Direct exec retains the SDK's two-minute remote deadline. Ctrl+C also cancels local waiting; remote execution may continue. Client disposal does not delete workspaces or sandboxes.
- Explicit agent directories require a POSIX sandbox and a middleware provider. Direct-MCP/CLI providers, PowerShell, background Bash, and unknown sandbox tools are refused rather than silently executing at the root.
- Remote services require HTTPS; loopback HTTP is allowed. Redirects are not followed with credential headers.

## Validation scope

Manual CLI scenarios and focused builds. Regression tests are added only for failures observed during those runs. No broad test suite or coverage campaign is required for this sample.
