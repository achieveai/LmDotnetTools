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
- `sandbox exec` accepts an existing session ID and prints an operation ID before execution. `--timeout` (default 120 s) bounds both local waiting and the gateway's execution deadline. A timed-out operation must be inspected, not blindly resubmitted.
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
- `pr check` saves each PR's `IsDraft` from the ADO listing, so a fresh snapshot is enough; drafts are listed but never picked. Snapshots saved before 2026-10-15 lack `IsDraft` and are not eligible until refreshed. Recheck live status/head before submitting. This command does not start reviews or change the immutable conversation binding.

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
# Preview only. No submissions, resets, comments or commits. It does read the host (see Preview below).
reviewd review run --repo O365Exchange/Weve_DA/Nova --pr <id> --slot 2 \
  --db .run/reviewd/pending-nondraft.db --session <gateway-session-id> \
  --workspace <host-workspace-id> --provider <provider-id>
# Requested PR review runs authorize comments/replies/summaries under the operator policy above.
# Add --execute only with the intended PR/slot scope and authorization for reset and local artifact commit.
```

`ReviewRunner.RunAsync` runs the reusable **one-PR function**. Pick candidates with `pr pick`. It is not a scheduler or automatic batch dispatcher.

**Slot choice.** `--slot` names the slot. Without it, the runner picks one (`AutoSlot.cs`):

- A PR that already owns a slot keeps it, and so does `--resume` / `--cleanup-only`. Recovery never moves a PR.
- Otherwise it tries each slot once, in order (`--slots 2,4` limits and orders them). Without `--slots` it tries the first `Reviewd:LanesPerRepo` slots (configuration, 1–6, default 6), so set it to the runner's lane count. A slot is skipped, with nothing claimed, when a queued batch entry holds it, another local run holds its lease, or the DB records an owner.
- A **stale** owner counts as free. Stale means another PR's claim lease has lapsed, the same test `review reap-slots` uses. It is reclaimed with the reaper's own reclaim (freeze, confirm quiesced, discard, release) under the held slot lease, and the slot is then used. A reclaim that fails, such as a review that is still running, leaves the slot held, and the runner moves on. Owners claimed before leasing are never stale here; `reap-slots --include-undated` handles those.
- If `prepare` finds the native slot busy, it exits 3 with a `slot-not-idle` status before writing anything. The runner then releases its own claim and tries the next slot. With an explicit `--slot`, the run stops, but the claim is still released. A slot off the idle baseline counts as busy, such as one left behind after a commit to the store's main. Admission never repairs it; warming the slot does.
- If no slot is usable, the run exits **3** and writes a JSON skip reason for every slot to stderr. Nothing is claimed.
- Preview prints the slot it would pick, or the stale owner it would reclaim.
- `review slots --repos <csv> --db <db> --workspace <id>` lists each slot from the database alone: `free`, `queued` (a live batch entry), `owned` (with the owner PR and lease) or `expired` (an automatic run reclaims it). It writes nothing and does not check local leases or the native slot.

**Preview.** Without `--execute` the run claims nothing and sends nothing, but it is not offline:

- It needs the S2S host (`LmStreamingBaseUrl` plus its S2S secret) and a real `--workspace`. It asks the host for an existing conversation titled `{repo}-{pr}-`, whether one is titled for the current head (then it stops: "already reviewed by another run"), and whether a bound conversation is still running.
- When the host has a conversation for the PR and the database has no binding, preview saves that binding. It is a cache of what the host already holds, not a claim.
- `--session` is only echoed; preview never contacts the gateway.

1. Validate eligibility, host capabilities and session/workspace identity. Re-review reuses the immutable bound conversation, checking its workspace/provider/mode and global idle state.
2. Atomically claim the selected workspace/repository/slot with an immutable SQLite token. Keep PR/slot lifetime leases. Incoming setup requires an already idle native slot; it never resets a predecessor. Run installed setup through Gateway and require complete context plus an unchanged active/non-draft source/merge revision. Save `setup-context.json` inside the review directory, in pieces of at most 64 KiB beside it when large (the main file lists them under `split_into`). Line history and earlier PRs go to `setup-history.json` beside it, for the review's history specialist only, who writes what matters into `history-notes.md`; the commit step moves the history out, so the archive never keeps it. A report from an older setup kit, with no history part, stops setup: install the kit before the runner.
3. Save thread binding and message idempotency key before submission. Give the hosted agent the absolute **sandbox** context-file path, not instructions to run setup again. The same initial prompt covers remaining context gathering, Bluebird searches, verified review findings, latest discussions, inline feedback/replies and summary publication.
4. Wait for the input, global root run and recursive descendants to finish successfully in two stable observations. Send the publication-confirmation prompt in the **same conversation**, then wait again.
5. Read `publication_receipts.json`, and the receipts it continues in when the commit kept one over 1 MiB as several (a failed push resumes here); verify fresh thread/comment IDs and exact text against read-only ADO GETs. Require a posted summary, even for no findings; a re-review's reply in the reviewer's own earlier summary thread (general, not inline) counts as its summary. Recheck PR/checkout before committing.
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

## Judge one merged PR's review

```bash
# Preview only. Free: host and ADO GETs, /tmp writes in the sandbox (removed). No agent, no receipt, no database write.
reviewd judge run --repo O365Exchange/Weve_DA/Nova --pr <id> \
  --db .run/reviewd/pending-nondraft.db --session <gateway-session-id> --workspace <host-workspace-id>
# Add --execute to start a hosted judge. It spends model money, capped by --max-usd (default 15).
```

`JudgeRunner.RunAsync` judges one completed PR whose review receipt is `completed-verified`. `--workspace` must match the receipt's workspace. Defaults: `--provider gpt-6.1-sol`, `--reasoning-effort xhigh`, the configured mode, and the embedded `Prompts/merged-judge.md` (`--prompt-file` replaces it). `reviewd judge run --help` prints the judge-only help. Every error line starts with `<repo> #<pr>:`.

**Preflight** (preview and execute, before any marker, judgment dir or receipt): the provider must be listed and available in the host's `api/providers`, the mode must resolve on the host, the host must honour a root reasoning effort, and the prompt must be non-empty and fit the 100 KiB limit for the longest possible judgment path. A failure exits 1 with nothing written.

**Preview** opens the database read-only, builds the inputs in the sandbox's `/tmp/reviewd-judge-<nonce>/inputs`, removes that directory, and prints the packet summary, the judge identity, the packet `paths` (source stores, prior judgments) and the action `--execute` would take. It still needs the S2S host (workspace, providers, mode, usage reads) and the sandbox session.

**Packet paths:** `sourceStoreGitDir` is the PR's own source store; `sourceStores` lists every local source store with its repo name, for cross-repo contract checks. `priorJudgments` lists only finished (`completed` or `completed-draft`) receipt-backed judgments of other PRs: never this PR's own, never a judgment dir without a receipt.

**Execute** takes the host-wide judge lock `~/.local/share/lmdotnettools-review-daemon/judge.lock` (one judge at a time on this host, across every install), then:

1. Quiet gate: the previous judge thread for this PR, if any, must be settled: its root idle, every sub-agent settled, and, for a frozen root, every tool call of its last turn answered (a sub-agent spawn the host has not started yet shows only as an `Agent` call without a result). Busy reads retry like the review wait, then refuse.
2. Write the pending marker `reviewd/judge-pending/{repository-key}/{prId}` (overwritten, never deleted). Build `PRs/<review-dir>/judgment-<n>/inputs` in the sandbox. Besides `packet.json` and the raw ADO files it holds two short digests the judge reads first: `threads.md` (one line per non-system comment: ids, author class, status, file:line, iteration, date, author, text without HTML or hidden markers, cut at 600 characters) and `packet-summary.md` (at most 8 KB). When the local source store lacks the final head, `inputs/final/<path>` holds the final-head text of the files changed after review (GET only; at most 40 files, none over 200 KB or binary; skipped files are listed in `packet-summary.md`, and a failed GET is a limitation, not a failure). Save receipt `reviewd/judge-run/{repository-key}/{prId}/{n}` with the marker's nonce.
3. Create a new conversation titled `judge-{repo}-{prId}-{n}` with no working directory. Send the prompt once with a saved idempotency key. A send the host does not answer (the same rule as the wait) goes again with the same key, backing off like the wait, for up to 30 minutes; the nudge send does the same. Creating the conversation is not retried (the host takes no idempotency key for it, so a timed-out try may still leave an empty conversation): a host too slow to answer ends the judgment `failed-precondition` with "the host was too slow … No judge ran and nothing was spent; rerun later".
4. Wait for the judge and its sub-agents. Every 60 s, read the conversation's `/usage` and save it to the receipt's `judgeUsage`, so a crash keeps the latest cost reading. At `--max-usd`, or when cost cannot be measured, freeze the conversation (final; never unfrozen or resent). Then let the judge settle for up to 20 minutes, by the same test as the quiet gate, on two reads 10 s apart. Sub-agent spend after the freeze is not capped. The end of the post-check and the final receipt write re-read `/usage`; spend above the cap is recorded as `capOverrun` (dollars over). A host that does not answer (a read past the HTTP timeout, no response, a 5xx, 408 or 429, or a tree that stays busy) does not end the wait: the judge counts as running, one line goes to stderr, and the next try comes 30 s later, then 60 s, doubling up to 5 min. The `/usage` reads go on meanwhile, so the cap still holds. A `/usage` read the host does not answer is not counted against the judge (one line per backoff step); only 5 definitive failures in a row (a 4xx, or a body that is not usage JSON) mean the cost cannot be measured, which freezes. After 30 minutes with no host answer the wait exits 1 with the judgment still `judge-running` (or `nudge-running`); rerun to resume the wait (nothing is resent). Only a full poll counts as an answer (the input status, run state and sub-agent tree all read); a status read alone, or a busy tree read, does not, so run-state or tree reads that keep failing also end the wait after 30 minutes. A host restart ends a judge run without saying so: the host keeps the run `InProgress` until something wakes that conversation. A live host names a run as current from before it reads `InProgress` until after its final status, so when the judge's run has started (its input reads `InProgress` or `Interrupted`) but the host names no run on every read for 3 minutes, no process is left to finish it: the wait records `orphaned` in the receipt and goes to the post-check. Every read in those 3 minutes must be answered; a read the host does not answer starts them over. `NotStarted` never counts (a live host that is still loading the conversation names no run either), so the wait goes on. Quiet time alone never counts: a root waiting on a sub-agent still has its run.
5. Post-check: snapshot ADO threads again and compare with the "before" snapshot, then read `judgment.json`. A judgment without `"stage": "final"` gets one nudge in the same conversation. A run a host restart ended (recorded `orphaned`, or `Interrupted` on the host) is finished the same way from what the judge wrote: final means `completed`, else one nudge (which also wakes the conversation), else `completed-draft` or `failed-no-output`.

**Stages and exit codes:**

| Stage | Exit |
|---|---|
| `completed`, `completed-draft`, or skip (same identity already judged) | 0 |
| `failed`, `failed-precondition`, `failed-no-output`, `capped`, refused, or still running | 1 |
| usage error | 2 |
| `ado-changed` (ADO threads changed while the judge ran) | 3 |
| PR not completed yet: nothing to judge, no judgment written (like `review_setup` exit 4) | 4 |
| Ctrl+C (remote work may continue) | 130 |

**Rerun and resume:**

- The same command resumes an unfinished judgment from its saved stage. A marker whose nonce has no receipt resumes too: its inputs are rewritten into the judgment directory that holds that nonce.
- A capped judgment whose settle or post-check did not finish repeats only those steps, and so does a failed judgment whose sub-agents had not settled (they can outlive the failed input); it stays `failed`.
- Identity is the review artifact commit, reviewed heads, prompt-template SHA-256, provider, effort and mode. The final PR head is recorded but not compared. A changed identity is refused. `--rejudge` starts a new judgment `n`, except while the last judgment still needs the settle and post-check above: then those steps repeat instead (exit 1 until they pass).
- `failed-precondition`: provisioning the judge failed before any judge thread existed (for example the provider became unavailable after preflight). Nothing can run twice, so a plain rerun starts a new judgment `n+1`; `--rejudge` is not needed.
- Failed, capped and ADO-changed judgments are never retried automatically. `--rejudge` is refused while a judgment is unfinished.

**ADO stays read-only by rule, not by force.** `judge run`'s own ADO calls are GETs, and the prompt tells the judge to use GETs only. The sandbox's ADO helper and egress rules do not enforce that: a judge session can send other methods. The post-check sees only this PR's threads changing (new, edited or deleted comments, status changes): a vote, a PR field or a work item changed by the judge is not detected. Making it enforced needs a GET-only egress rule for judge sessions on the host.

## Judge merged PRs in batch

```bash
# targets.tsv: one install per line, name<TAB>db<TAB>session<TAB>workspace (# comments allowed).
# Preview only. Free: database reads (read-only) and host GETs. No lock, sandbox, ADO or writes.
reviewd judge batch --targets targets.tsv --max-usd-per-day 30 --avg-usd-target 4
# Add --execute to judge, one PR at a time. Spends model money within the guards below.
```

`JudgeBatch.RunAsync` re-reads every target database on each pass; receipts are the only state. `reviewd judge batch --help` prints the judge page.

**Selection**, per verified review (`completed-verified` in the target's workspace), by its newest judge receipt:

| Newest receipt | Batch does |
|---|---|
| unfinished, with a judge thread the host accepted (in flight, or past the send) | resume with the receipt's provider, effort, mode and cap; no guards. While the host runs it, it counts as running now and the plan says `running now; the batch re-attaches only if its process stops` |
| `thread-created`/`send-pending` not in flight, or `inputs-written` | resume as a new start: all guards apply |
| unfinished under another prompt, or with no stored cap | not run: while the host runs it, only under "Running now"; once it stops, Needs you |
| `capped` with unsettled sub-agents or no ADO compare, frozen < 24 h | post-check ($0, sends nothing); after 24 h, Needs you |
| `failed` whose sub-agents had not settled, started < 24 h | post-check, the same way; after 24 h, Needs you |
| `failed-precondition` | retry once it is 24 h old; 2 of them on one PR, Needs you |
| any other finished stage, any identity | judged; skipped (`judge run --rejudge` by hand) |
| none | new start if the install's PR snapshot says Merged within `--max-age-days` (7) and no `judge-skip` marker is under 24 h old |

**Order and guards.** Resumes first (oldest start), then post-checks. New starts only while no judge is in flight on any target, every target was read, and the stop file is absent. In flight includes a capped judge frozen under 24 h whose sub-agents (or a pending spawn) still run, and a failed judge started under 24 h whose sub-agents had not settled, until they are quiet. A skipped target (an unusable install) may hide a judge in flight, so while one is skipped, readable targets still resume and post-check but nothing new starts; the status line, Needs you and the JSON `unusable` list name each install with one plain cause (the error text goes to stderr). Then the target whose last judge start is oldest goes next (never judged first), newest merge first within it. A start needs `spent24h + cap <= --max-usd-per-day` (rolling 24 h, all targets) and at most `--limit` starts per run. The average of the last 10 finished judgments with a thread (on at 5) above `--avg-usd-target` starts nothing and exits 5. A receipt's spend that may still grow (capped, unsettled, still running, or not marked complete) is re-read live from `/usage` and the higher value counts. A cost that is not known (an unpriced model, no dollar figure, or `/usage` unreadable) counts at that run's cap, as the runner's freeze does; Needs you says `cost not known; until it is, the daily cap counts its cost cap ($X)`; the 24 h table still shows the spend read so far. A start whose send never landed (its receipt holds no usage reading and the host has none for its conversation, a 404) has spent $0. A stored start about to resume counts once against the daily cap: its own spend so far is replaced by its cap, never added to it. A host that refuses the batch's sign-in (HTTP 401 or 403) refuses every read: judges count as running and costs at their caps, the status says `the host refused the batch's sign-in (HTTP 401); see Needs you` (never `ok`), and one Needs-you line says to check the batch's credentials instead of one line per judge. `--max-usd` above `--max-usd-per-day` is a usage error. The average counts every finished judgment with a thread in the scanned stores, test judgments included: clear or wait out test receipts before trusting it.

**Execute** takes the host-wide judge lock once (held: prints `busy`, exits 0) and runs `judge run` for each pick in-process. Its only write of its own: when a PR is not completed (exit 4) it writes `reviewd/judge-skip/{repository-key}/{prId}` and skips that PR for 24 h. A pick that returns with its judge still in flight ends the run (exit 0; the next run resumes it). Two consecutive failures exit 5; `ado-changed` exits 3 at once. A broken target line or database is skipped and listed under Needs you. Output: markdown for a person, in plain words for a phone (status, Needs you, then the plan; stages and miss categories in words, e.g. `could not start` for `failed-precondition` and `found but dropped by our pipeline` for `pipeline-filter`; the 24 h table newest first, `$0.00` for a judgment that never reached the host; repeated waits and repeated Needs-you lines read as one line with a count; a table or plan cut to fit ends with `and N more`; no exit codes, thread or tool-call ids, or error text, which go to stderr and the JSON), then one JSON line with the full plan and, when the run paused judging, a short `cause` (e.g. `average cost too high`). The hourly tick leaves stderr in `journalctl --user -u reviewd-judge`, and lines that need the error text point there; a failed judgment's line also says it is not retried and that `judge run --rejudge` judges it again; while its re-check is due (its sub-agents had not settled, started under 24 h), the line instead reads `last judgment failed while its sub-agents still ran; re-check pending`, as the plan's re-check does; still unsettled after 24 h, it says to run `judge run` until its sub-agents settle, then `judge run --rejudge` (`judge run` repeats the settle and post-check before any `--rejudge`). A paused status says why and how to undo it, e.g. `paused (2026-10-22T22:17Z: 2 judgments in a row failed). Judges already running still finish. To start new judges again, run: rm <stop file>`; an unusable install alone reads `paused: 1 install unusable (see Needs you); judges already running still finish`. A preview counts each new judge at its cap, so its status says how much room the daily cap leaves, not what was spent. The status never says `ok` while the daily cap lets no new judge start (`spent24h` plus the next start's cap is over `--max-usd-per-day`), even when a judge in flight hides that wait: it reads `waiting: no room under the daily cap for another new judge`. PRs read as `<repo> !<id>`, e.g. `NovaClient !5764750`.

| Exit | Meaning |
|---|---|
| 0 | done, nothing to do, busy, in flight, cap or limit reached, resume-only |
| 1 | the targets file cannot be read |
| 2 | usage error |
| 3 | a judge ended `ado-changed` |
| 5 | guard stop: 2 consecutive failures, or the average is above target |

**Hourly tick** (`ops/judge-tick.sh`, run by the `reviewd-judge` user timer; unit text in its header). It sources Nova's `env.sh` for credentials, rebuilds `targets.tsv` from each install in `JUDGE_INSTALLS`, runs the batch with `--stop-file $JUDGE_HOME/STOPPED`, and saves `summary.md`. Settings live in `$JUDGE_HOME/judge.env` (default `~/.local/share/lmdotnettools-review-daemon/judge/`). Alerts go through doctor.py's `notify()`:

- An install it cannot use (missing folder, settings that differ from Nova's, a failing `env.sh`) becomes a `name<TAB>cause` line in plain words: the batch lists it under Needs you and starts nothing new until it is fixed or removed from `JUDGE_INSTALLS`.
- Unusable installs alert ("Judge paused: N installs unusable", one line per install and cause; `PAUSED-ALERTED` remembers the lines). The tick alerts again whenever the lines change: an install becomes unusable, stops being unusable (the alert starts `Fixed or removed since the last alert: <names>.`), or stays unusable for a new reason (the alert says `New reason for: <names>.`). When the last one is fixed or removed it alerts once, `Judge resumed` (`Fixed or removed since the last alert: <names>.`, then `No install is unusable now. New judges can start again.`); while `STOPPED` exists that alert is titled `Judge: no install unusable` and ends `Nothing new starts until you run: rm <path>`. `PAUSED-ALERTED` changes only after an alert is sent, so a failed send is tried again on the next tick. It never alerts while the same lines repeat, in any order. A busy batch (another judge holds the lock, so nothing was scanned) changes nothing.
- Alert bodies are cut at a line under 3500 bytes and say the full summary is in `summary.md` on the host.
- Any non-zero exit writes `STOPPED` as `<UTC time>: <why>` and alerts once. The title names the cause (`Judge paused: average cost too high`, `Judge paused: 2 judgments in a row failed`, `Judge paused: a PR changed on ADO while it was judged`); the body starts `Why:` with the reason in words, then `Nothing new starts until you run: rm <path>`. A PR that changed on ADO while judged alerts every time. While `STOPPED` exists the batch status shows its first line and the same `rm` command. A missing `JUDGE_CLI` pauses the same way (`Judge paused: Reviewd CLI not found`, with the path); its body says `Nothing new starts until you fix JUDGE_CLI in <judge.env>, then run: rm <path>` (rm first only latches again on the next tick). The daily summary goes out on the first tick after `JUDGE_SUMMARY_HOUR`.
- While `STOPPED` exists the batch runs resume-only: unfinished judges finish, nothing new starts. Stop gracefully with `touch STOPPED`; recover with `rm STOPPED`.
- `systemctl --user stop reviewd-judge.service` stops at once; a running host judge is then uncapped until the next tick resumes it.
- Install the judge CLI as its own copy (`JUDGE_CLI`), never with `deploy.sh`: that relinks `cli`, and doctor then restarts every review batch.
- Enable the timer only with a `JUDGE_CLI` copy built from this source, which takes the host-wide judge lock. A build that holds only a per-database judge lease must not run a judge on this host while the timer is on.
- Before a host deploy, check that no judge is running: `flock -n ~/.local/share/lmdotnettools-review-daemon/judge.lock true`.

## Close a judged PR

```bash
# Preview: fetches main and the review rounds into private refs (git objects only), builds the commit, prints it.
# Nothing is pushed; the private refs and scratch are removed. The database is opened read-only.
reviewd judge close --repo O365Exchange/Weve_DA/Nova --pr 5776213 --db reviews.db --store <store dir> --judgment 4
# Add --execute to push the commit to the store's remote main (never forced).
```

`JudgeClose.RunAsync` files one judgment and merges every review round of the PR into the store's remote `main` as one commit, parents `[main, round 1 tip, …, round k tip]` (rounds from the judgment's `packet.json`, in order). The commit message is `close: <Repo> !<pr> (judgment <n>)`; author and committer come from the store's own git `user.name`/`user.email`.

- **Which judgment.** `--judgment <n>`, else the only judgment with stage `completed` and output `final`. With several, close refuses and lists them (`judgments 4, 5, 7 are completed final judgments; pass --judgment <n> to pick one.`): a closed PR cannot be closed again, so a test judgment must never be picked by default. It also refuses while a judgment of the PR is still running, and says so when the database has no judgment for that repo and PR. The first output line names n, its date, the judge and review cost, and its lesson counts. `judgment.json`/`.md` must match the sha256 in the judge receipt, `inputs/packet.json` must match `judgment.json`'s `inputs.packetSha256` (the packet the judge read) and the receipt's `inputs.packetSha256` (the packet `judge run` wrote; receipts from before it have none), and the packet must say the PR is completed. The judge can write its own files, so the packet must also name this PR (`prId` is `--pr`), its review dir must hold this judgment (`<review dir>/judgment-<n>`), and every round branch must be that dir's own (`review/<dir name>` or `review/<dir name>-<suffix>`); otherwise close refuses before it fetches anything.
- **Where git runs.** On the host: the two phases of `JudgeSandbox.py` (`close-prepare`, `close-commit`) run as a local `python3` in `--store`. The host's git credential helper does the fetch and the push (for GitHub, `gh auth git-credential`); git never prompts. `--remote` (default `origin`) can point at a local bare repo to try close on a copy of the store.
- **What it never touches.** It fetches `main` and each round branch into `refs/reviewd/close/<nonce>/*` (`--refmap=`, `gc.auto=0`, `--recurse-submodules=no`, so a round that moves a pin never fetches inside a populated submodule) and builds the commit in a temporary index under `.scratch/reviewd-close/<nonce>/`: the store's HEAD, index, local `main`, working tree and slots never move, and both are removed at the end; a push to a named remote (`--execute` with the default `origin`) does update `refs/remotes/<remote>/main`, as any `git push` does; from Git 2.56, so does a push to a URL that matches a named remote. Main keeps its own gitlinks.
- **Leftovers.** A close that was killed leaves its private refs and scratch behind. Each close first runs `close-sweep`: it removes only `refs/reviewd/close/<nonce>/*` and `.scratch/reviewd-close/<nonce>` older than an hour (newest mtime of the scratch dir and loose ref files; a packed ref with no scratch dir counts as old) and prints each one it removed on stderr.
- **Checks before writing.** Each round's remote tip equals its packet tip. A round changes only `PRs/<dir>/` (and gitlinks, which are not taken). `PRs/<dir>/` on main is unchanged since the rounds branched; otherwise close refuses. Round dirs overlay in order; a later round wins on the same path and CLOSING.md lists the replaced paths. Main keeps what a review archive keeps: text files of at most 1 MiB. A larger file, a binary (a NUL in its first 8000 bytes) or a link stays on its round branch, and CLOSING.md lists it with its size and branch. Before the push, close refuses a commit that would delete any path on main outside `PRs/<dir>/`, and a commit step whose prepared index is gone (its scratch was removed after prepare): nothing is committed or pushed.
- **What it writes,** composed against the fetched main (not the local KnowledgeBase, which can lag), one write per file:
  - a `write-now` lesson is appended to its target (`KnowledgeBase/<scope>/<slug>.md`, lowercase `[a-z0-9-]`, not `developers/`) under `<!-- judge-lesson <id> -->`, `### <title>`, the body and `Seen in: <Repo> !<pr>`; `sourcePrs` and `updated` are updated. Body headings move below the title (the shallowest becomes `####`; fenced code is left alone). A missing target is created. The judge's `op` is a hint only;
  - only the listing lines of entries it wrote: that entry's `_index.jsonl` line (replaced in place, or added; only when main has an index: close never starts one, since an index listing only close's entries would hide the rest) and, for a new entry, a link at the end of its `## <scope>` section of `_toc.md` (blank-separated when that section's links are). A new scope goes before the first later `## <scope>` (sections are alphabetical). Main's listings are curated, so close does not regenerate them;
  - one line per miss (improvements, and `count-only` lessons) in `Improvements/<repo>/process.md`, or `Improvements/process-generic.md` for `process-generic`: `- <Repo> !<pr> · category · change · evidence: <first> · key <key> · <policy> · <date>`, on one line (newlines and `|` removed, change cut at 200 characters);
  - `PRs/<dir>/CLOSING.md`, short enough to read on a phone: the marker `<!-- reviewd-close judgment=<n> review=<last tip> -->`, dates without times, a link to `judgment-<n>/judgment.md`, the verdict, and a cost table with short labels (`Review (rounds 1-2)`, or `Review conversation <i>` when the review used several conversations; `Judge, judgment <n>`; `Other judge runs (<n>, …)`) and tokens with thousands separators. The total adds known costs only and says `at least` when any part is a lower bound or unknown. An unknown judge cost (this judgment's or another run's) is never added: it is listed under the table as `cost unknown, capped at $<cap>`. Then the rounds merged, the replaced files as a count and a short list, the files kept on the review branch, what was added (a ledger line links to the judgment), and what was not filed;
  - `judgment-<n>/judgment.json` and `judgment.md` (not `inputs/`).
- **Not filed** (listed in CLOSING.md and the JSON): no or unknown `writePolicy`, no lessonId or key, a bad target or tags, a title or body that starts with `---` or contains `<!-- judge-lesson` or `<!-- reviewd-close`, an entry already holding the lesson's marker, a ledger already holding the PR's key, a target without front matter.
- **Secrets.** The text close adds is checked against the credential pattern, as plain text and as JSON text (a quote escaped as `\"` counts) (bearer, JWT, an `Authorization` value: `Basic`, `Bearer` or `token` with a credential of 16+ characters holding a digit, `+`, `/` or `=`, `AccountKey=`, `SharedAccessSignature`, `sig=`, `ghp_`, private keys, an Azure AD client secret such as `abc8Q~…`, and `client_secret`/`clientSecret`/`password`, or an `Authorization` with no scheme (quoted or not, `:` or `=`), a connection string's `Pwd=` (not a shell's `PWD=/path`) or a `--client-secret` flag set to a real-looking value: 8+ characters with a letter and a digit, not `<...>`, `***`, `$VAR`, `${VAR}` or `changeme`; a password or scheme-less `Authorization` value with no digit is not caught). All-digit values are not caught either, nor are other `Authorization` schemes such as `SharedKeyLite`, `GoogleLogin` or `Digest` (the value rule reads the scheme name as the value). `judge run` uses the same pattern for its inputs. Close checks lesson sections, ledger lines, listing lines, CLOSING.md, the judgment outputs and the overlaid round files, each against main's version of the path. Text already on main is not re-checked. A match refuses the whole close and names only the path, never the match.
- **Idempotent.** If main's `PRs/<dir>/CLOSING.md` has this judgment's marker and every round tip is merged, close prints `already closed` and exits 0. A marker for another judgment is refused: re-closing is not supported.
- **Push.** `git push <remote> <commit>:refs/heads/main` with no force or lease, after checking the commit descends from the fetched main; then remote main must be the commit or contain it (another writer may already have built on it). If reading remote main back fails after git accepted the push, close exits 1 with `close: push accepted but not confirmed (exit 2): the push of <sha> was accepted but not confirmed: git <ls-remote|fetch> failed (<code>); run close again: it says already closed.` A rejected push (main moved) pushes nothing; close fetches the new main and composes again, 3 tries in all. If main moved every time, it prints one JSON line (`"status":"main-kept-moving"`, `"tries":3`) and exits 6: run close again later.
- **A failed step** is reported as `close step <phase> failed (exit <code>): <first error>`, for example the git error.

| Exit | Meaning |
|---|---|
| 0 | preview done, closed, or already closed |
| 1 | failed or refused: nothing pushed, except in three cases. `close: push accepted but not confirmed`: the push landed. `push finished but remote main is <sha>, which does not contain <sha>`: git accepted the push, but remote main does not hold it now. A `push failed` can also have landed (for example, the connection dropped after the remote took the push). In all three, run close again: it says already closed, or closes again. |
| 2 | usage error |
| 6 | main kept moving on all 3 tries (nothing pushed; run close again) |
| 130 | Ctrl+C before the commit starts (during prepare or compose; it says `Cancelled before the commit; nothing was committed or pushed.`), or a Ctrl+C that also stopped the commit's python (if the push had started it may have landed; run close again: it says already closed or finishes). A cancel that reaches only close (a `kill` of its process) during the commit waits for the commit, says `Ctrl+C came during the commit; close waited for it to finish.`, and exits with its real result. Every try ends by removing its private refs and scratch, whatever happened, including a Ctrl+C that killed the step's python before it could clean up; a cancel that reaches only close while prepare's python still runs can leave them for a later sweep. |

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
