# Workspace review workflow

`workflow.yaml` declares all three routes. The host admits a run and supplies `Route`, `PrId`, `HeadSha` and a frozen `WindowId` (empty when no discussion window applies). It binds the existing `review-parent` conversation and provisions independent grading and extraction sessions.

Before running, configure the [host publication callback map](../../LmStreaming.Sample/README.md#hosted-review-publication-callbacks) and this daemon's matching `WorkflowPublication:SharedSecret`. The `achieveai` and `s2s` profiles share an AppId and are alternative configurations; `mcqdb` has a separate AppId and callback. Missing callback configuration fails capability preflight even in collect-only mode. Keep callback and provider credentials in host configuration, outside the reviewed workspace.

Scripts require Python 3 and `REVIEW_DAEMON_EXECUTABLE`, an absolute path to the configured installed daemon executable or DLL. They forward the Context/Input envelope on stdin to `--workflow-operation <name>` without shell interpolation. The configured daemon resolves run ownership, checkout and credentials from its own store. Scripts never select remote URLs or branches from agent output. Script stdout is exactly one JSON value; diagnostic text goes to stderr.

Scripts are trusted foreground operations on Windows or Linux. A Python bootstrap establishes a Windows Job Object or Linux process group before launching the script. Python is also required for PowerShell steps. Scripts must wait for their children; residual processes or unconfirmed containment durably park the owning run and preserve its workspace through restart. Operators must reconcile the processes and workspace before clearing that park. Deliberate process-group escape by operator-authored code is outside this contract.

The preparation commands supply an existing `ContextArtifact` containing the exact checkout/range, frozen discussion or complete history, and exact authorized knowledge entry paths. Missing preparation is an error. Public retention contains fixed metadata and independently reviewed knowledge edits; drafts, grading, discussion and invocation evidence stay in private state. Canonical review/findings/judge records remain available to historical readers, with unavailable model or numeric grade fields left unknown. Closure requires a durable retention receipt and a merged source PR.

New collect-only reviews use a Markdown-first path: independent review → independent finding grade → proposed comments → deferred PR discussion snapshot → performance self-assessment → deterministic no-op publication → artifact retention. The first three stages receive no PR threads and must not fetch them through tools. Generic shell access is not an isolation boundary; manual validation must inspect actual tool use. The `gb` Gateway marketplace supplies review/grading/retrospective guidance; its registered host directory is `~/Gateway/marketplaces/ClaudePlugins`.

These agents return raw Markdown, never a JSON response schema. The sample transport wraps that text for internal workflow persistence. Agent Input is fenced YAML. Newly admitted collect-only reviews use a small `ContextArtifact` manifest: exact identities, scoped knowledge roots, linked-work IDs/lookup status, and pointers to separate readable PR-details and per-file YAML diff documents. The daemon supplies navigation facts, not an interpreted context dossier. The existing `gb:code-reviewer:pr-review` → `pr-context` → `pr-context-gatherer` route owns investigation and Markdown context synthesis, with the independent phase's no-discussion restriction propagated explicitly. The parent saves the gathered context as `pr-<PrId>-context-report.md` and passes its path and a short summary onward. Relevant knowledge is searched on demand within system/current-repo roots, never eagerly inventoried. Machine JSON remains for reconciliation; document hashes verify the manifest and sidecars on recovery. Older frozen admissions and non-collect routes retain their original context/knowledge contracts. The performance report distinguishes valid misses, their causes, our false positives, incorrect existing PR findings and unknown evidence. It is self-assessment, not blind independent judgment. No aggregate score or perfect-recall claim follows from empty discussion.

Under an exact full-output private artifact grant, retention includes `setup.md`, `review.md`, `grade.md`, `comments.md`, `discussion.md`, `performance.md`, identity/publication/receipt metadata and captured notes. Markdown carries the same run-binding trailer as existing review prose; all exports retain redaction and credential checks. Setup on the branch contains diff identity/digest, not source bodies. No comments are posted by the collect-only flow.

Existing pinned workflows and other routes keep their structured JSON contracts. A format-only correction continues the same session without action tools. Scoped tools enforce publication receipts and contained knowledge writes. The host saves each completed result before routing and releases the workspace only after safe completion. It must not treat a structured Outcome as proof a provider action occurred.

Changing the additional-extraction question or a skill requires editing these workspace files, with no workflow-specific C# DTO. Definitions and skills are trusted operator configuration; never load a replacement workflow from the untrusted PR checkout. Each admitted run copies its definition, declared skills/scripts and sibling script helpers into a hashed package. Resume verifies those copied bytes. Existing in-flight work without a pinned package refuses dispatch and requires operator reconciliation.

The supported SQLite deployment has one coordinator per database on a local filesystem. Startup holds an OS file lease until shutdown; a second coordinator cannot start against that database. There is no lease expiry or distributed takeover. Upgrade migration parks legacy nonterminal stage runs without changing their existing artifacts or receipts. New snapshots use schema 2; drain active work before rolling back to a schema-1 binary.

## PR-scoped review files

The four collect-only agent stages load `skills/artifact-boundary.md` before their stage instructions. It establishes `ReviewArtifactRoot` from the current run's prepared `Evidence.HistoryDirectory`, verified as `PRs/<PrId>` beneath the assigned workspace. All agent-created files—including scratch notes, plans, context maps, tool request bodies and temporary diffs—stay beneath that root. Shared `Memory/`, source checkouts, other PR directories, `/tmp` and home directories are not review-output locations. Missing or conflicting roots mean no file writes; report any violation rather than silently cleaning it up. Existing evidence and frozen reports remain read-only. Host-owned bookkeeping and retention are separate.

Every delegation carries the exact root and the boundary rule, recursively. The host's `code-review-daemon` mode also supplies a conditional rule in its root prompt and `subAgentPrompt`, without editing individual specialist templates or embedding a PR number in shared configuration. Catalog-time prompt injection reaches ordinary templates and descendants using them; late-discovered templates can bypass that injection, so explicit forwarding remains necessary. These are prompt instructions, **not filesystem enforcement** or a guarantee of model compliance. Non-collect publication and merged-knowledge routes keep their existing contracts.

New admissions pin the updated workflow and shared skill. Existing frozen runs are unchanged. The host must separately load the updated `samples/LmStreaming.Sample/Prompts.yaml` mode configuration for its shared prompt additions to take effect; editing this workflow package does not update a running host or existing conversations. No host restart, deployment, cleanup or review rerun is implied by the prompt change. Validate actual file placement in a later authorized run before claiming runtime compliance.

## Inventory existing review branches

```sh
CodeReviewDaemon.Sample --review nova --list-review-branches
```

This read-only command starts from actual remote `review/*` refs and their exact tips. It queries only the corresponding configured source PRs; it never falls back to database history or lists historical PRs without branches. JSON goes to stdout, diagnostics to stderr. Failed remote enumeration returns exit 1. Unmapped, ambiguous, missing-source and unknown-lifecycle entries remain explicit; exit 0 does not mean every branch is actionable.

Entries include provider-reported source head/base, target ref, merge result and timestamps. Oldest source closure comes first, then creation time; unknown dates sort last. This is not the branch creation date. A target ref is evidence, not authority to choose the review store's submodule tracking policy. GitHub's reported base SHA is not proof of the historical pre-merge base.

Inventory does not start polling, require S2S, open/migrate the review database, change a checkout, admit a run, publish, merge or delete. Existing credential loading/refresh and configured diagnostic logging may write their usual local files. Discovery alone grants no retention or cleanup authority. Keep open PRs pending and reconcile unknown entries before acting.

Each entry exposes `blocksNewIntake`: only a successfully discovered open PR returns false. The normal poller skips the matching terminal or unresolved PR, not unrelated PRs. Open artifact branches do not prevent a later head or discussion from being processed. Maintenance remains held while terminal or unresolved entries exist. Enumeration failure holds the cycle; it never falls back to database history. A configured review store is required. These observations do not replace current provider checks or the PR ownership gate.

**Automatic backlog draining remains disabled.** The legacy lifecycle sweeper and database-only stranded-run recovery are not composed into polling. Existing active workspace leases are recovered before polling; terminal branches remain visible blockers until explicitly completed.

### Complete one merged PR

Keep polling, source comment posting and blanket Git push disabled. This explicit command authorizes the named review-artifact branch and content-only review-store integration; it never writes source PR branches.

```sh
CodeReviewDaemon.Sample --review nova --complete-merged-pr <repoKey> <prId> <artifactTipSha> <landedSha> <parentSha> --inspect --slot <0-5>
CodeReviewDaemon.Sample --review nova --complete-merged-pr <repoKey> <prId> <artifactTipSha> <landedSha> <parentSha> --slot <0-5>
```

For a finite parallel batch, use `--complete-merged-batch <jsonPath>`. The JSON array accepts 1–18 objects, each with exactly `repository` (canonical configured key), `prId` (positive decimal string), `artifactTipSha`, `landedSha`, `parentSha`, `slot` (0–5) and `resume` (boolean). Duplicate PRs or repository slots are rejected before admission. Set `resume: false` for serial preparation followed by parallel execution; `resume: true` requires an already-admitted exact round with frozen scope, pinned package and matching prepared evidence. It verifies remote context hashes and skips preparation; this also supports a successful inspect whose model workflow has not started. Failed or incomplete preparation cannot use this path. Inspect uncertain input and its deadline before choosing resume. Keep automatic slot discard disabled. One coordinator and callback host serve the batch; model work overlaps while existing Git locks serialize integration. A failure stops new dispatch and drains accepted siblings. Verify remote completion after the batch drains, not while another integration can advance main.

Inspect selects an existing warm, unowned slot and prepares hashed historical evidence without starting assessments or publishing. Execution reuses that preparation. An explicit slot expands only the command's process-local pool view; it does not clear an occupied slot. Omit the slot only when normal allocation is appropriate.

The command freezes `CompletionEvidenceVersion: 1`, `ContextManifestVersion: 1`, provider identities and the exact artifact tip. The historical reader requires the legacy root `review.md` under the validated branch's PR directory. Limits are 64 tree entries, 256 KiB per file and at most 1 MiB aggregate (also bounded by half the context limit). Original filenames and hashes remain visible. Landed evidence uses immutable one- or two-parent objects and their verified first parent, not a synthetic source checkout. Missing original source revisions remain unavailable, not reconstructed proof. Other historical layouts require reconciliation.

The merged route runs reconstructed self-assessment, independent raw-evidence-first judgment, proposed lessons, same-judge challenge and independent knowledge safety review. Bound Markdown exports distinguish proposed, challenged and safety-approved knowledge/process lessons and per-PR developer observations. They do not change live review methodology or create developer profiles.

Retention preserves original artifacts and pushes only this run's branch without rebase retries. Integration imports positive regular-file paths onto freshly verified main, refuses conflicting existing entries, regenerates knowledge listings from main, and leaves source gitlinks unchanged. Main may only fast-forward from its own verified remote tip. The artifact branch is deleted with an exact-SHA lease only after remote main confirms integration. Unknown write outcomes require reconciliation, not blind retry.

Repeat the same command and original SHAs to resume. Accepted agent inputs are rejoined, never resent. A single first-assessment recovery is supported only when the host proves that failed input was never accepted and the conversation has no messages or descendants. A first failed closure can recover once only when all previous stages validated and locked Git checks prove the clean checkout still matches the retained artifact tip, main has no files for this PR, and no integration receipt exists. Divergent local main or changed remote identities block recovery. A crash between saving the recovery marker and updating the workflow snapshot fails closed and requires operator reconciliation. Failed receipts, frozen packages and prepared evidence remain intact. After retention advances the branch, resume accepts only this workflow's durable retained SHA; branch absence requires its integration receipt.

## Reset an interrupted review slot

Drain the daemon first. Keep polling, comment posting and blanket Git push disabled.

```sh
CodeReviewDaemon.Sample --review nova --reset-review-run 26
CodeReviewDaemon.Sample --review nova --reset-review-run 26 --confirm
```

The first command inspects only. The second parks one incomplete collect-only run, resets its exact prepared slot, and clears its active assignment. Slot reset deletes older tracked, untracked and ignored review output; it retains an inventory, intent and receipt outside the slot, not a copy of the discarded files. Conversations, workflow snapshots and prior host artifacts remain intact. It does not admit another review or publish anything. Completed runs, corrected turns, unresolved script/input/descendant activity and foreign source registrations are refused. An inactive incomplete assignment can be reset when its exact prepared checkout still exists. A failed read-only `fetch-discussion` step is allowed only with a matching frozen task and failure receipt, three validated preceding reports, no other unsettled tasks and no publication outbox.

The reset-run command requests compact output so large inventories do not exceed command-output limits. Its response and database receipt carry `compactOutput: true`, `inventoryCount`, and `inventorySha256` instead of the inventory. The digest uses UTF-8 JSON with sorted keys, compact separators and ASCII escaping. The full remote intent and receipt retain the inventory; replay validates the full receipt and warm state before returning the compact projection. Round acceptance and standalone/archive callers keep full output by default.

For a failed initial preparation with no prepared-workspace record, the same command can instead reset the exact retained checkout and rearm that run on its previous slot. This requires a stopped owner, inactive assignments, one failed first preparation task, no hosted reviewer bindings, and no publication. `--confirm` explicitly accepts any unreconstructed old write outcome; the original evidence remains. After a verified remote reset, the task receives a new invocation identity and deadline rather than replaying its failed receipt. The run is not parked. Resume it through normal admission. A repeated reset before resume verifies the saved remote receipt and warm slot without discarding again.

The selected outer worktree uses local sparse exclusions for `PRs/`, `KnowledgeBase/` and `DeveloperLearnings/` so a subsequent hard reset does not restore historical output. Required operational files and the registered nested source checkout remain usable. Pooled checkpointing requires the separately installed `review_common.py` to use explicit-path `git add --sparse`; updating the local helper alone does not deploy it. Legacy archive/knowledge paths must be validated separately before use on sparse slots. The low-level helper also supports exact unprepared-baseline cleanup, but that filesystem receipt does not retire a daemon round or resolve the historical outcome of an unacknowledged write.

A reset intent without a terminal remote receipt remains blocked for operator reconciliation. Do not clear its assignment or blindly retry the underlying helper. After a confirmed reset, repeating the command reports `already_reset` without touching the slot, which may now belong to another review. A changed PR head can then enter through normal admission; same-head rerun policy is unchanged.

### Accept a verified reset for an unprepared merged round

```sh
CodeReviewDaemon.Sample --review nova --reset-review-round <roundId> --receipt <reset-receipt.json>
CodeReviewDaemon.Sample --review nova --reset-review-round <roundId> --receipt <reset-receipt.json> --confirm
```

Inspection is read-only and does not migrate SQLite. Confirmation is a separate operator database action: it holds the coordinator lease, re-verifies the exact receipt and warm slot through the recorded Gateway session (no new session), and atomically records `OperatorReset`, its receipt and acceptance. An incomplete owning run is parked as failed; a completed parent is not rewritten. Original unknown-write artifacts remain unchanged. The acceptance exempts only that exact preparation artifact from the admission hold; newer preparations do not inherit it. The retired round cannot replay. A legacy ownerless same-PR/head remains held; this permits unrelated admission, not a same-head rerun.

This path supports schema v13–v15 without migration, an exact merged-event/run identity, inactive matching assignment, no prepared-checkout artifact and no publication outbox. Other event formats and prepared resets remain outside this acceptance path. A repeated acceptance refuses because the round is already terminal; it does not write a second disposition. Polling and publication must be disabled. The temporary operator client permits receipt output up to 32 MiB; Gateway's lower operation limit still applies, so oversized/truncated receipts fail rather than authorizing acceptance.

Follow-ups: [narrow script credentials](https://github.com/achieveai/LmDotnetTools/issues/766) and [completed private evidence retention](https://github.com/achieveai/LmDotnetTools/issues/767).
