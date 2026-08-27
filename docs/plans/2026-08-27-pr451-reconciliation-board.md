> **Committed record, 2026-08-27.** This board was written in a working tree and deliberately left
> untracked while the branch it describes was paused. It is committed here because the effort has
> resumed: PR #451 is being reconciled as a **stack of small PRs off `main`**, not merged in place.
>
> Tracking epic: **#526**. That epic, not this file, is the live surface — this is the evidence
> behind it and is not maintained going forward.
>
> **Re-verified before committing, against `main` at `26538168`:** M4, M5, M7 and M8 all still read
> as this board describes. Every count below was taken at `828f410b` and `origin/main` has moved —
> re-derive M1–M3 rather than trusting them.
>
> **The issue numbers in this file belong to a different tracker.** `#49`, `#118`, `#128` and the
> rest do not resolve in `achieveai/LmDotnetTools` — several collide with unrelated issues here.
> They are preserved verbatim as historical references. Work items are tracked as fresh issues
> under #526.

# PR #451 Reconciliation — Board

Three tables. **Monitor** = signals we watch. **Do** = work items. **Discovered** = things we
learned that change how we work.

Last updated: 2026-08-27 UTC · Branch `daemon/review-reliability-and-pr-coverage` · HEAD `29f80328` — verified against `origin/main` at `828f410b`, merge-base `3a25297b`.

> **THIS IS A SISTER BOARD, NOT THE BOARD.** The parent `REVIEW-BOARD.md` is **absent from this
> branch and from `origin/main`** — its newest version is `f68bfdc5` (2026-08-23), reachable only
> from other refs and from `.claude/worktrees/*`. That absence is why a plain search of the checked-out
> tree failed to find it. This file was written against that version as a structural template.

> **ROW-ID PREFIX: `R`, NOT `D`.** The parent board's DISCOVERED rows are numbered `D1…D440+` and
> are still being appended to on other branches. This board uses `R1…` so that if the two files are
> ever merged, concatenated, or scraped by the same tooling, no row ID can collide and no row can be
> silently overwritten. Do not renumber these into the `D` series.

> **SCOPE.** This board covers one question only: what happens to PR #451, which is OPEN and
> `CONFLICTING` / `DIRTY` against `origin/main` across **49 files**. It does not track daemon
> operations; the parent board owns that.

> **CURRENT DECISION: STOPPED (2026-08-27).** The audit is complete and the owner elected to take no
> further branch or PR action. Nothing here is in flight. This file is the record so the work can be
> resumed without re-deriving it.

---

## 1\. MONITOR — live signals

Each row is a number we can re-measure on demand. "Healthy" is what it should read.

|#|Signal|Now|Healthy|State|How to check|
|-|-|-|-|-|-|
|M1|PR #451 mergeability|`CONFLICTING` / `mergeStateStatus: DIRTY`|`MERGEABLE`|**BLOCKED**|`gh pr view 451 --json mergeable,mergeStateStatus`. Do not trust GitHub's cached verdict alone — confirm with `git merge-tree --write-tree HEAD origin/main` and count `CONFLICT (content)` lines.|
|M2|Conflicting file count|**49** (all modify/modify; no add/add, no delete conflicts)|0|**BLOCKED**|`git merge-tree --write-tree HEAD origin/main \| grep -c 'CONFLICT'`. Re-derive rather than reusing this number — `origin/main` moves.|
|M3|Branch ↔ main divergence|branch **+29–32** commits from base; main **+68**|converged|**DIVERGED**|`git rev-list --count 3a25297b..HEAD` and `git rev-list --count 3a25297b..origin/main`|
|M4|#49 — `SystemPromptAppendix` reachable in production on `origin/main`|**NO** — appendix is written, never read outside tests|reader exists on a production path|**DEFECT LIVE ON MAIN**|`git grep -n 'AppendCallerInstructions' origin/main`. Every hit outside `tests/` should be a real caller. Today the only callers are `SystemPromptAugmenterTests.cs`. Consequence: every S2S review runs on the bare mode prompt; the daemon's methodology never reaches the model.|
|M5|#118 — `SubAgentModelId` has a reader on `origin/main`|**NO** — declared and set in two live profiles, read by nothing|reader exists|**DEFECT LIVE ON MAIN**|`git grep -n 'SubAgentModelId' origin/main`. Declaration is `CodeReviewDaemonOptions.cs:131`; `appsettings.achieveai.json` and `appsettings.mcqdb.json` both set `gpt-5.6-sol`. Consequence: every review sub-agent runs the orchestrator's model.|
|M6|#128 — collect-only is structural on `origin/main`|**NO** — advisory only; outbound HTTP is write-capable on a collect-only run|write ops denied at the egress boundary|**DEFECT LIVE ON MAIN**|`git grep -n 'BuildForRun' origin/main`. `PolicyEnforcedHttpClientFactory.cs:42` calls it without `allowWriteOperations`, which defaults `true` (`DaemonOperationPolicy.cs:71`).|
|M7|PR-polling coverage on `origin/main`|~**101 of 711** active PRs enumerated (Nova); `MaxPagesPerPoll` has **zero readers**|all active PRs enumerated|**DEGRADED ON MAIN**|`git grep -n 'MaxPagesPerPoll' origin/main` — declared at `CodeReviewDaemonOptions.cs:260`, no reader; both providers use a private `const int MaxPages = 10`.|
|M8|`prompt_template_hash` populated in production on `origin/main`|**always NULL** — column, reader and eval consumer exist; no producer|non-null on new runs|**DEGRADED ON MAIN**|`git grep -n 'PromptTemplateHash =' origin/main` — the only assignments are in two test files.|

---

## 2\. DO — work items

**Owner** is the agent holding it. **Blocking PR** = must finish before the PR to `main`.

### Blocking the PR

Ranked as in the audit. Nothing below is started — the owner stopped the effort on 2026-08-27.
Sizes are LOC from the audit, not hours.

|#|Item|Owner|Blocking|State|
|-|-|-|-|-|
|1|**#49/#53 — `SystemPromptAppendix` reader.** Commits `0ee6688b` + `57ff1fbe` + `4f112fec`, ~613 LOC. Touches `SystemPromptAugmenter.cs`, `LmStreaming.Sample/Program.cs`, `LmStreamingS2SClient.cs`. Propose the three as one PR.|—|yes|**NOT STARTED — highest severity.** Fixes M4.|
|2|**#118 — `SubAgentModelId` reader.** Commit `3ae949de`, ~507 LOC, 15 files. Carries a `SubAgentManager.cs` precedence change that must be rebased onto main's post-`a8c79af6` manager.|—|yes|**NOT STARTED.** Fixes M5.|
|3|**#128 — collect-only made structural.** Commit `0c6bad30`, ~600 prod LOC + 407 test. Adds migrations v6/v7 and a `policy_refusal` ledger; egress method-denial + spawn-time audit refusal.|—|yes|**NOT STARTED.** Fixes M6.|
|4|**#114 — finding-disposition reconciler.** Commit `101f76c9`, ~1240 prod LOC + 616 test. `ReviewNotesArtifactBuilder.cs` is **byte-identical on `origin/main` to the merge-base**, so this ports with **zero conflict** — best effort/conflict ratio in the set.|—|yes|**NOT STARTED.**|
|5|**#113 — `InfraNarrationFilter`.** Commit `3a0c5e38`, ~1047 LOC — one new file (382 lines) plus one call site. Main has no structural filter; it only asks the model not to narrate via prompt text.|—|yes|**NOT STARTED.**|
|6|**#120 — work-item context into the review brief.** Commit `407002aa`, ~870 prod LOC. `AdoWorkItemContextReader.cs` (579 lines) absent from main; largest new capability, cleanest to port.|—|yes|**NOT STARTED.**|
|7|**#82 — first-review sentinel guard + standing rate check.** Commits `51ac92a4` + `20b8c272`, ~702 LOC. Main has the detection primitive `IsNoNewFindingsSentinel` but no control.|—|yes|**NOT STARTED.**|
|8|**#115 — findings persistence.** Commits `b9a5bc75` + `f64530ab`, ~629 LOC.|—|yes|**BLOCKED on item 4** — depends on `ReviewFindingReconciler`, which is also not on main. Propose as a stack.|
|9|**#116 — prose-aware knowledge ranking.** Commits `d18cc96c` / `309319b6`, ~457 LOC. Removes a dead heavy scoring term (`ScopeBonus = 3`, `KnowledgeDigest.cs:29`) still live on main.|—|yes|**NOT STARTED.**|
|10|**PR-polling `$top`/`$skip` pagination.** Extractable from checkpoint `d7e64e3e`. Small, high impact.|—|yes|**NOT STARTED.** Fixes M7.|
|11|**#122 — prompt-template provenance producer.** Commit `b782237e`. Small; makes a column main already has, reads, and ships to its eval corpus stop being NULL.|—|yes|**NOT STARTED.** Fixes M8.|
|12|**#112 — unrelated-histories vs indeterminate.** Commit `db27fb30`, ~228 prod LOC + 381 test. `"unrelated histor"` has 0 hits on main. Stops the daemon reporting its own watchdog timeout to an author as "your branch descends from nothing".|—|yes|**NOT STARTED.** Lands in `ReviewSlotPreparer.cs` — see R6.|
|13|**#123/#119 — model forwarding + empty-tier guard.** Commit `763f028a`, ~90 prod LOC. Main's `S2SReviewAgentLoopFactory.cs:131-134` discards the requested model id, so the escalation ladder re-runs the same model. **Keep the spend-neutrality pin** (`KnowledgeModelId` held at luna).|—|yes|**NOT STARTED.**|
|14|**#47 — DeveloperLearnings phases 1+2.** Commits `ee1c8600` + `5662b06b`, ~3688 LOC. Entirely new subtree; no main collisions except the ADR numbers (see R11).|—|yes|**NOT STARTED.**|
|15|**#107 — lock-atomicity regression test.** From `49c74a17`. Test-only, ~120 lines, trivially portable. Main's `_replayLock` production code is already correct.|—|no|**NOT STARTED.**|
|16|**eol/blob-identity cleanliness gate (`4cb2421c`), #124 fd guard, #87 `-b` status probe.** All live in `ReviewSlotPreparer.cs`.|—|yes|**DEFERRED — port only after R6 is decided.**|
|17|**Hygiene from `bbf8eff1`: csharpier gate + root PNG removal.** Trivial.|—|no|**NOT STARTED — proposable immediately.** Main still runs `dotnet format whitespace` and still carries `workspace-agent-test-tool-schema.png` at repo root.|
|18|**#127/#121 salvage.** Commit `d9fd3c83`, ~679 LOC total. Rebase onto main's shared-budget design, do **not** port the branch's budget structure. Still net-new: the 120→400 cap raise (main is at 120 against an observed ceiling of 201 comments), the `MaxExistingCommentsListed` → `MaxExistingCommentsRendered` rename, NEW-section-claims-first ordering, and all of #121 (`CommentFetchOutcome`).|—|no|**NOT STARTED** — see R4 for what main already won.|
|19|**Per-sub-agent model recorded and published in the brief.** Commit `e73646b2`, ~576 LOC. Main's `SubAgentProvenance.cs` has no `ModelKey`/`ModelIntelligenceKey`/`ModelSelectionSourceKey`, so the value carried on `SubAgentSummary.EffectiveModelId` is dropped mid-chain; main renders one run-level `\| Model \|` row only.|—|no|**NOT STARTED.**|
|20|**ADRs 0012–0015 — the four review-daemon decisions.** Commit `fc9be7d0`, ~377 LOC, docs only. Ships with the #47 subtree (item 14).|—|no|**NOT STARTED — blocked on the numbering decision.** See R11 and the ADR row in *Owner decisions*.|

### Done and committed

Per the parent board's rule: nothing moves here on a claim alone.

|#|Item|Commit|
|-|-|-|
|P1|Safe rollout gates and pooled-review reliability hardening — 80 files, +14188/−5309|`bbf8eff1`|
|P2|Lifecycle teardown races closed + supervised port handover — 23 files, +6471/−1574|`29f80328`|
|P3|Pre-commit formatting gate realigned `dotnet format whitespace` → `csharpier check` on staged `.cs` files (`.husky/task-runner.json`, `.husky/pre-commit`)|in `bbf8eff1`|
|P4|Verification at commit time: full solution build 0 errors · CodeReviewDaemon.Sample.Tests 1804 passed / 1 skipped · LmStreaming.Sample.Tests 1438 passed / 1 skipped · LmMultiTurn.Tests 1110–1133 passed · LmAgentInfra.Tests 369 passed · `ops/.../test_release.py` 26 passed · csharpier clean|verified against `29f80328`|
|P5|PR #451 opened and updated — https://github.com/achieveai/LmDotnetTools/pull/451|head `29f80328`|
|P6|Three-part audit completed: commit-level (first half), commit-level (second half), file-level conflict map. Read-only; no refs or working tree touched.|no commit — analysis only|

### Owner decisions waiting on you

|#|Question|
|-|-|
|**NEW**|**Does PR #451 proceed at all, and in what shape?** The audit answered the factual question — 30 of 32 commits are genuinely net-new, and ~0 of 49 conflicting files can be resolved by taking main's version — but not the scope question. Options on the table were: a stack of small PRs off current `main`; extracting only the three live defects (M4/M5/M6); or resolving #451 in place. **Answer 2026-08-27: STOP.** No branch or PR action for now. This board is the resumption record.|
|**NEW**|**`bbf8eff1` must be split before any re-proposal — six independent features in 14k lines.** Separable, in rough value order: (a) draft-PR gating / `PrStatus` — a **breaking `IPrProvider` change**, needs its own PR; (b) `SchemaCompatibility` + `ReleaseIdentity` + `DaemonAdmissionCoordinator`; (c) `ops/codereview-release` systemd path; (d) `SynthesisModelId` + `EffectiveModelId` resolution; (e) per-turn model override in `UserInput`/`MultiTurnAgentLoop`; (f) hygiene. Your call on the split boundaries.|
|**NEW**|**`bbf8eff1` changes the `ReviewModelId` default from `claude-sonnet-5` to `gpt-5.6-terra`.** A deliberate behaviour and spend change, not a merge artifact. Needs explicit sign-off before it ships anywhere.|
|**NEW**|**Linux systemd vs Windows PowerShell for the release story.** The branch adds `ops/codereview-release/` (release.py, 5 systemd units, verification-policy.json) with capabilities main has none of: release-identity hashing, schema-compat gate, admission drain. Main solves the same problem with `scripts/publish-daemon.ps1`, `ensure-services.ps1`, `restart-review-host.ps1` (main PR #334). Main has no `ops/` directory. Pick one story before porting (c).|
|**NEW**|**ADR numbering collision.** The branch claims 0012–0015. Main already has `0012-wall-clock-discriminating-test-inventory`, `0013-background-transcript-flush-has-no-caller`, `0014-host-directory-wipe-accepted-residuals` under different slugs. Renumber to 0015–0018, or accept the slug-identity collision per main's own ADR 0010. Either way `docs/adrs/README.md` conflicts.|
|**NEW**|**Two uncommitted plan docs and one superseded tracking file.** `docs/plans/2026-08-26-review-slot-cap-and-hygiene-{design,implementation}.md` should not be merged as-is — the design doc lists phases 1, 2 and 4 as still pending, and the implementation doc describes a task sequence that was not followed. `docs/pr-451-reconciliation-tracking.md` is an earlier draft of *this* board written before `REVIEW-BOARD.md` was located; keep it as an appendix or delete it, but two trackers for one effort is the drift the board rules exist to prevent.|

---

## 3\. DISCOVERED — lessons that changed how we work

Each one cost us real time. Each is now a rule.

|#|What happened|Rule now|
|-|-|-|
|R1|**We assumed #451 was "likely largely superseded" and nearly closed it on that basis.** It reads that way from the outside: 68 upstream commits, heavy thematic overlap, both sides citing reliability issue numbers. The commit-level audit found the opposite — **30 of 32 commits are net-new**, and main's issue stream (#274–#498) never intersects the branch's (#47–#128).|"Overlaps in theme" is not "already merged". Verify by identifier absence in the target tree, never by commit message, PR number, or vibe.|
|R2|**Two audits disagreed in framing and both were right.** The commit-level pass said "nothing is upstream"; the file-level pass said "main independently rewrote the same files, better". The reconciliation: the **features** are absent from main, the **files** moved underneath them.|Separate "is this capability present?" from "has this file diverged?". A file-level diff cannot answer the first question and a feature grep cannot answer the second. Run both before a scope call.|
|R3|**`HostDirectoryCleaner.cs` was written against a pre-#411 shape of the tree and duplicates work already merged.** Main has `HostPathGuard.cs` + `HostDirectoryWipe.cs` (PR #411 `1176a9b9`, PR #274 `517eb6d3`). Worse, the working-tree version **deletes an upstream security mechanism** — `SlotHostPathRefusedException`, `GuardSlotPaths`, `Retire`/`RetireAsync` — so a poisoned slot address gets recycled instead of retired. **DO NOT COMMIT.** *Caveat: the per-file detail came from an agent with no git access using a worktree as an `origin/main` proxy; treat the file-by-file claims as strong-but-unconfirmed. The core duplication was independently confirmed with git.*|Before building a containment primitive, grep the target branch for one that already exists under another name. A rewrite that removes a typed refusal is a security regression even when the replacement is technically better.|
|R4|**Two fixes on this branch were superseded by better ones on main.** (a) The parked-question race: main fixed it via #262/#428 by latching off a deferred `ToolCallResultMessage` rather than the tool call — stronger evidence, main wins. (b) The comment-budget double-count (#127): main fixed it via #225 with one shared budget **plus** a `MaxExistingCommentsChars` cap the branch lacks — main wins.|When both sides solved it, compare the designs and take the better one. "Ours is already written" is not an argument. Salvage only the parts main genuinely lacks.|
|R5|**`RetainInputs`/`ReleaseRetainedInputs`/`HasUnassignedInput` is parallel invention.** Main solved the same goal *after the fork* with `IInputAcceptanceObserver`, `IAcceptanceReportingAgent`, `InputAcceptanceRefusedException`, and `MultiTurnAgentPool.AcceptedInputGrace` (30s) — landed by `2bd30f84` (#442/#445) on `b5b5bc13` (#434). That file does not exist at the merge-base or on the branch. The two will not merge cleanly.|A long-lived branch must re-check upstream before solving a known-shared problem. Check `git log origin/main --since=<fork date>` for the problem area first.|
|R6|**`ReviewSlotPreparer` / `SlotHygiene` forked structurally in opposite directions.** Main kept the preparer at ~380 lines and grew `SlotHygiene` 317→851. The branch did the reverse: preparer 379→1724, `SlotHygiene` left byte-identical to base. Items 12 and 16 both land there.|A rebase into a file whose responsibility boundary moved is a rewrite, not a merge. Decide the structural question before porting anything into that file.|
|R7|**`DaemonOperationPolicy.cs` is a false conflict.** It carries literal NUL-byte sentinel content, which trips git's binary sniffing during merge despite `.gitattributes` declaring `*.cs text eol=lf diff`. Re-merged as text on the extracted blobs it produces **zero** conflict hunks.|When git says "Cannot merge binary files" on a `.cs` file, extract the three blobs and re-run `git merge-file` before treating it as manual work. `ReviewSlot.cs` has the same sentinel property.|
|R8|**Nearly none of the 49 conflicts are "take theirs".** Even where a specific hunk is clearly superseded by main's better version, the *same file* carries branch-only additions main lacks entirely — CI-status reader, work-item context, refusal recorder, `Home` sandbox feature, first-review-sentinel metric. Estimated resolvable-by-taking-main: **~0 of 49**, and the one mechanically clean file is R7's false conflict.|Never estimate merge cost from the conflicting-file count. Sample the largest files for mixed superseded/net-new content first — that ratio is the real cost driver.|
|R9|**`ScopedToolFilter.cs` was deleted on main** (#490 / PR #497) as dead once the daemon went mandatory-S2S. The branch still ships it.|Check for upstream *deletions*, not just additions. A file the branch still maintains may have been retired for a reason that also applies here.|
|R10|**Main has 11 daemon files the branch has never seen**: `Eval/*` (8 files), `StrandedRunReconciler.cs`, `HostDirectoryWipe.cs`, `HostPathGuard.cs`, `KnowledgeIndexRegenerator.cs`.|Before designing anything new in the daemon, list what exists on main that does not exist here. R3 happened because this was not done.|
|R11|**ADR numbers 0012–0014 were claimed on both sides for different content.** The branch commit's own message asserts the numbers "were free on both this branch and origin/main" — that claim was already stale when written.|Re-check ADR number availability against `origin/main` at the moment of writing, not against a remembered state. Slug is identity per ADR 0010, but the README index still collides.|
|R12|**The board itself could not be found by searching the checked-out tree.** `REVIEW-BOARD.md` is committed and has a long history, but exists on **neither `HEAD` nor `origin/main`** — only on other branches and under `.claude/worktrees/*`. Three search passes and two wrong-shaped drafts were spent before `git log --all -- <path>` located it.|To find a tracked file that is not in the working tree, search history and all refs (`git log --all --`, `git cat-file -e <ref>:<path>`), not the filesystem. Absence from `ls` is not absence from the repository.|
|R13|**The audit did not verify everything, and the gaps are specific.** (a) Checkpoint `d7e64e3e`'s 124 files were not individually verified — the `src/Sandbox/*`, `src/LmAgentInfra/*`, `LmStreaming.Sample/Persistence/*` and `scripts/ci-test.ps1` portions need a separate pass before that checkpoint is salvaged wholesale. (b) `bbf8eff1` and `29f80328` were verified by distinctive-identifier sampling plus counterpart-file reads, **not** hunk-by-hunk over 20k lines. (c) `MultiTurnAgentLoop.cs`, `SubAgentState.cs` and several CodeReviewDaemon test files were classified by pattern/ratio rather than fully verified.|State coverage limits in the artifact, at the same prominence as the findings. An audit that does not say what it skipped will be read as exhaustive.|
|R14|**Four hard design reconciliations are the real cost of merging #451 in place**, each requiring a reviewer to understand two documented competing designs rather than pick a side: (1) the `IPrProvider` interface migration across 4+ files — `GetPrStateAsync` return type `PrLifecycle` ↔ `PrStatus`, plus main's new `GetCurrentHeadShaAsync`; (2) branch `CommentRenderBudget` vs main `ExistingCommentsBudget`; (3) two independent bounded-teardown designs in `SubAgentManager.cs` / `MultiTurnAgentBase.cs` — branch `_disposeGate` + Interlocked election + `ObserveBoundedAsync` vs main `_disposeTask` + `AwaitBoundedTaskAsync`; (4) branch `Home` sandbox feature vs main `PluginSelection` tri-state across 5 files, which is mechanical once recognised.|Count the *design* reconciliations, not the conflicted files. Three of these four need a decision from someone who owns the subsystem; only the fourth is merge work.|

---

## 4\. COMMIT INDEX — every branch commit, mapped

All **32** commits on `daemon/review-reliability-and-pr-coverage` since merge-base `3a25297b`, oldest
first. This table exists so a merging agent can go commit → board row without re-deriving the audit.
Every commit is pushed; none is local-only.

**Verdict** is against `origin/main` at `828f410b`: `NET-NEW` = behaviour genuinely absent from main ·
`PARTIAL` = main has an independent equivalent for part of it · `WIP` = intermediate commit whose tree
is re-landed by the merge commit that follows it (no unique content; `git diff` between the pair is
empty for the wip's own files).

|#|Commit|Subject|Verdict|Board row|
|-|-|-|-|-|
|1|`d7e64e3e`|checkpoint(daemon): review-daemon work, plus git-reliability and PR-coverage fixes|**PARTIAL**|Item 10 (`$top`/`$skip` paging) · R4a (parked-question fix — main wins) · **R13a: 124 files, not individually verified**|
|2|`4cb2421c`|fix(daemon): stop refusing a correct tree over the repo's own eol attributes|NET-NEW|Item 16 — lands in `ReviewSlotPreparer.cs`, see R6|
|3|`d26af1ae`|wip(agent-a21615bae8ccb997f)|WIP|→ re-landed by #6 `b782237e`|
|4|`b29b38bc`|wip(agent-a8a5e80b65a41819e)|WIP|→ re-landed by #5 `db27fb30`|
|5|`db27fb30`|merge(#112): distinguish 'git could not tell' from 'histories are unrelated'|NET-NEW|Item 12|
|6|`b782237e`|merge(#94,#122): refresh PR metadata each poll; record prompt-template provenance|NET-NEW|Item 11 · M8|
|7|`4ec510c8`|wip(workitem)|WIP|→ re-landed by #8 `407002aa`|
|8|`407002aa`|merge(#120): wire PR work-item context into the review brief|NET-NEW|Item 6|
|9|`505df312`|wip(routing): model forwarding + empty-tier guard + #123 tier mapping|WIP|→ re-landed by #10 `763f028a`|
|10|`763f028a`|merge(#123,#119): forward the caller's model to the S2S host; reject empty tier arrays|NET-NEW|Item 13 — keep the spend-neutrality pin|
|11|`b349aaa3`|wip(collect-only): structural enforcement + refusal ledger|WIP|→ re-landed by #12 `0c6bad30`|
|12|`0c6bad30`|merge(#128): make collect-only structural instead of advisory|NET-NEW|**Item 3** · M6|
|13|`49c74a17`|fix(#124,#87,#107): orphaned-pack sweep, self-reporting status probe, replay-lock atomicity|NET-NEW|Item 15 (#107, test-only) · Item 16 (#124, #87)|
|14|`d9fd3c83`|fix(#127,#121): give the comment budget one meaning; make fetch degradation visible|**PARTIAL**|Item 18 · R4b — #127 core already fixed on main via #225|
|15|`101f76c9`|feat(#114): record what each specialist finding actually became|NET-NEW|**Item 4 — zero-conflict port**|
|16|`d18cc96c`|fix(#116): rank knowledge on what the PR says, not just the paths it touched|NET-NEW|Item 9|
|17|`309319b6`|merge(#116): same as #16|NET-NEW|Item 9|
|18|`51ac92a4`|fix(#82): refuse the no-new-findings sentinel when no prior review exists|NET-NEW|Item 7|
|19|`e73646b2`|feat(review-notes): record the model per sub-agent and publish the brief|NET-NEW|Item 19|
|20|`20b8c272`|feat(#82): report the first-review sentinel rate at every daemon start|NET-NEW|Item 7|
|21|`fc9be7d0`|docs(adrs): record the four review-daemon decisions|NET-NEW|Item 20 · R11 — number collision|
|22|`b9a5bc75`|feat(#115): persist the findings the reconciler already computed|NET-NEW|Item 8 — blocked on Item 4|
|23|`3ae949de`|feat(#118): give SubAgentModelId a reader so review sub-agents leave Luna|NET-NEW|**Item 2** · M5|
|24|`f64530ab`|feat(#115): give the findings record its provenance; pin single-invocation|NET-NEW|Item 8 — blocked on Item 4|
|25|`3a0c5e38`|fix(#113): keep the daemon's own infrastructure failures out of the author's review|NET-NEW|Item 5|
|26|`0ee6688b`|fix(#49): give SystemPromptAppendix a reader|NET-NEW|**Item 1** · M4|
|27|`ee1c8600`|feat(#47): DeveloperLearnings phase 1 — ledger, lifecycle, model contract|NET-NEW|Item 14|
|28|`57ff1fbe`|fix(#49): log the composed prompt at composition; assert the appendix lands last|NET-NEW|**Item 1** · M4|
|29|`4f112fec`|fix(#53): #49 and #45 were inert in production; provisioned-property reader|NET-NEW|**Item 1** · M4|
|30|`5662b06b`|feat(#47): DeveloperLearnings phase 2 — projection and renderers|NET-NEW|Item 14|
|31|`bbf8eff1`|feat(daemon): safe rollout gates and pooled-review reliability hardening|NET-NEW|**P1** · Item 17 (hygiene) · **must be split six ways** — see *Owner decisions* · **R13b**|
|32|`29f80328`|fix(agents,release): close lifecycle teardown races and support supervised port handover|**PARTIAL**|**P2** · **R5 — drop `RetainInputs`/`HasUnassignedInput`** · **R13b**|

**Counts:** 32 commits · 5 WIP (no unique content) · 24 NET-NEW · 3 PARTIAL (`d7e64e3e`, `d9fd3c83`,
`29f80328`). Of the 27 content-bearing commits, **24 are entirely net-new** and the 3 partials each
retain net-new material after the superseded parts are dropped per R4 and R5.

**Not on any branch commit — uncommitted by design.** The following sit in the working tree and must
**not** be merged. They are recorded here so a merging agent does not go looking for them:
`HostDirectoryCleaner.cs` + 8 companion files (R3 — duplicate of main's already-merged
`HostPathGuard`/`HostDirectoryWipe`, and it deletes an upstream security mechanism);
`docs/plans/2026-08-26-review-slot-cap-and-hygiene-{design,implementation}.md` (stale — phases 1, 2, 4
listed as pending; task sequence not followed); `docs/pr-451-reconciliation-tracking.md` (superseded
draft of this board).

---

## How to use this

* **Monitor** rows are re-measurable. Ask "check M4" and it gets re-run. M1–M3 describe the PR;
  M4–M8 describe defects **on `origin/main` today** and stay live whether or not #451 ever merges.
* **Do** rows move to *Done and committed* with a commit hash. Nothing moves there on a claim alone.
* **Discovered** rows only get added when something actually cost us time. They use the `R` prefix —
  never renumber them into the parent board's `D` series.
* This board is **not committed**. It is untracked in the working tree by design, because the branch
  it describes is paused. If it is ever committed, commit it to the branch it describes, and re-verify
  M1–M3 first — `origin/main` moves, and every count on this page was taken at `828f410b`.
