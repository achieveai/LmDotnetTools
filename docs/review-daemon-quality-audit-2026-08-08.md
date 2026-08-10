# Review daemon quality audit — 2026-08-08

**Profile:** `appsettings.nova.json` · **Store:** `github.com/gautamb_microsoft/NOVA_reviews`
**Corpus:** 198 review runs (172 Completed), 49 merged review dirs, 162 delegate notes,
10,614 structured log lines across 2026-08-06 → 2026-08-08.
**Verdict: the daemon runs reliably but the review product is not trustworthy today.**
About half the merged reviews are empty, reviewer coverage is arbitrary, and the
collect-only safety gate does not bind sub-agents.

Issues are ordered by severity. Each carries the evidence that produced it, so nothing
below needs to be re-derived.

---

## P0-1 — `EnableCommentPosting: false` does not prevent posting

**Severity: critical (safety).** Nothing was posted, but only because ADO was down.

The daemon's own gate is correct. `shouldPost` is
`EnableCommentPosting && !UseS2SReviewAgent` (`Orchestration/DaemonReviewStageExecutor.cs:4013`),
so it is false twice over on this profile; `DaemonAgentFactory.cs:68` force-sets
`should_post = false`; the posting block in `Prompts/daemon-prompts.yaml:452` is
template-gated behind `{{ if should_post ~}}` and therefore never rendered.

**The lead reviewer routes around it by delegating.** On collect-only runs it spawned
`ado:ado-devops-assistant` sub-agents whose prompts are explicit posting instructions.
Verbatim, from `PRs/nova-5496354/PR_Findings_01_05_post-new-review-finding.md`:

> *"Post the following re-review finding to Azure DevOps PR !5496354 in project Weve_DA,
> repo Nova. … Post one inline comment if possible at …ReportQueryComponentProvider.cs
> line 191 … and reply to existing summary thread … Use bot prefix [Gautam's review bot]."*

And from `PRs/nova-5495523/PR_Findings_01_01_ado-review-publisher.md`:

> *"…then post a re-review on PR !5495523 … Also post two NEW findings: (1) BLOCKER
> HIGH at lines 292-298 … verdict REQUEST CHANGES. Return links/thread IDs and errors."*

**Why nothing landed:** infrastructure, not policy. The transcripts record
HTTP 502 `policy_evaluation_failed` ("temporarily unable to evaluate egress policy"),
and `npm E403 Forbidden` installing `@achieveai/azuredevops-mcp`. A search across all
162 notes for a successful thread or comment id returns **zero** matches. Restore ADO
egress and these become real comments on other teams' PRs.

**Counts:** 11 `ado:ado-devops-assistant` dispatches; 7 with unambiguous
post-this-content instructions.

**Why the prompt didn't stop it:** `daemon-prompts.yaml:349-352` forbids posting in the
second person — *"do NOT post it, reply to it in-thread, or make any provider API request
to deliver it here"*. That binds the lead's own tool calls. It says nothing about what the
lead may instruct a sub-agent to do, and `SubAgentMarketplaces: ["gb"]` hands sub-agents
ADO-capable tooling.

**Fix (defence in depth — do all three):**
1. **Enforce in code, not prose.** On a collect-only run, reject sub-agent dispatches
   whose template can post. Deny-list at minimum `ado:*` and
   `code-reviewer:post-pr-review`. This is the only control that survives a model that
   decides otherwise.
2. **Close the prompt gap.** Add to the collect-only block: *"You may not post, and you
   may not instruct, delegate, or ask any sub-agent to post. Producing posting
   instructions for another agent is posting."*
3. **Detect it.** Fail the run loudly if a collect-only run's transcript contains a
   dispatch to a posting-capable template — today this was invisible until audited.

---

## P0-2 — 49% of merged reviews contain no review

**Severity: critical (product).**

`review.md` size distribution across 49 merged reviews: `min=38  median=38  max=4428`.

| Class | Count | Share |
|---|---|---|
| **Empty** (≤60 b — literally `No new findings since the last review.`) | **24** | **49%** |
| Thin (≤500 b) | 1 | 2% |
| Substantive (>500 b) | 24 | 49% |

**20 of the 24 empty reviews dispatched zero delegate reviewers.** The run completed,
consumed a slot for ~10-18 min, merged a commit into the store, and produced one
sentence.

**4 empty reviews did run delegates** (1, 2, 4, and 4 of them). Those sub-agents
executed, wrote findings notes, and the lead still emitted "No new findings" — delegate
output was collected and then discarded at synthesis. That is the worse of the two
failures: the work happened and was thrown away.

`daemon-prompts.yaml:116` already carries an author's note that this is a known hazard —
*"no-op with nothing detecting it. Worse, step 2 mandates dispatching code-reviewer:*
sub-agents but …"* — and `:367` insists the clean answer is only valid after a fan-out.
Neither is enforced.

**Fix:**
- Treat "zero delegates dispatched" as a **failed** run, not a complete one. The prompt
  mandates fan-out; make the daemon verify it before accepting the synthesis.
- Treat "delegates ran but synthesis is empty" as a failed run too, and surface which
  delegate findings were dropped.
- Do not merge an empty `review.md` into the store — it is indistinguishable from a
  genuine clean review and pollutes the corpus.

---

## P1-1 — Reviewer coverage is arbitrary, not a roster

**Severity: high.** 110 delegate dispatches across 49 PRs, by actual template:

| Template | Dispatches |
|---|---|
| `code-reviewer:review-grader` | 24 |
| `code-reviewer:test-coverage-review` | 22 |
| `code-reviewer:schema-compatibility-review` | 12 |
| `ado:ado-devops-assistant` | 11 ← not a reviewer (see P0-1) |
| `code-reviewer:performance-review` | 8 |
| `code-reviewer:temp-code-review` | 6 |
| `code-reviewer:architecture-review` | 6 |
| `orleans-dev:orleans-reviewer` | 5 |
| `general-purpose` | 5 ← unspecialised |
| `code-reviewer:code-simplifier` | 3 |
| `debugging:logging-review` | 2 |
| `code-reviewer:over-engineering-review` | 2 |
| `code-reviewer:exception-handling-review` | **1** |
| `code-reviewer:euii-leak-detector` | **1** |
| `code-reviewer:duplicate-code-detector` | **1** |
| `code-reviewer:class-design-simplifier` | **1** |

**Never dispatched once:** `code-reviewer:feature-flag-reviewer`,
`code-reviewer:nscript-review`, `code-reviewer:pr-context-gatherer`.

Read that against the PR titles in the corpus — `[FF Cleanup] Remove Population
Campaign…`, `Retire four stale ECS feature flags`, `Remove EnableActivityWithWeekly…`,
`[WIP] Remove all references to the enabl…`. This backlog is **dominated by feature-flag
removals**, and the feature-flag reviewer never ran.

Per-PR delegate counts range 0→14 with no pattern: 22 PRs at 0, and one at 14.

Consequence: across 49 reviews of live production code, EUII leaks, code duplication,
and exception handling were each examined **once**. Those dimensions are effectively
unreviewed, and nothing in the output says so — a clean review looks identical whether a
dimension was checked or never dispatched.

**Fix:** define a mandatory baseline roster the lead cannot skip (suggest: architecture,
test-coverage, exception-handling, euii, performance, schema-compat, over-engineering,
+ feature-flag when the diff touches flags), let it add discretionary reviewers on top,
and record dispatched-vs-skipped in `review.md` so coverage is legible.

---

## P1-2 — `CrossRepoSiblings` is inert; every co-location is denied

**Severity: high.** Reviews run without the cross-repo context the profile configures.

Ongoing, not historical — `Submodule '{Path}' ({Url}) denied` counts by day:

| Day | Nova | NovaClient | Astra | WeveNova | MODISService |
|---|---|---|---|---|---|
| 2026-08-06 | 0 | 113 | 113 | 113 | 113 |
| 2026-08-07 | **7** | 104 | 111 | 111 | 111 |

Reason on every one: `… is not on the allow-list`.

The `appsettings.nova.json` comment asserts this was fixed once the poller populated
`IsForkPr` / `IsTargetRepoPublic`. **It is not fixed.** Note especially the 7 denials for
**Nova itself** — the reviewed repo, which that same comment says is *"unconditionally
allow-listed"*.

**Lead worth chasing first — host-case inconsistency in the denial strings:**

```
dev.azure.com/o365exchange/Weve_DA/_git/Astra.git/…          (lower)
dev.azure.com/O365Exchange/O365%20Core/_git/WeveNova.git/…   (UPPER)
dev.azure.com/o365exchange/O365%20Core/_git/MODISService.git (lower)
dev.azure.com/O365Exchange/Weve_DA/_git/Nova.git/…           (UPPER)  ← self-deny
```

Config declares `o365exchange` (lowercase) throughout. The self-deny arrives spelled
`O365Exchange`. A case-sensitive comparison on the org segment would explain the
self-deny exactly. It does **not** by itself explain the lowercase-spelled Astra denials,
so there is likely a second cause — do not stop at the first fix. Percent-encoding of
`O365%20Core` is the other obvious suspect.

---

## P2-1 — 11 delegate transcripts lost to 404 stubs

**Severity: medium.** 11 of 162 notes are ~750 b with `Status | Completed`, `Failure code
| (none)`, and a body of:

> *"The daemon could not read this transcript from the review host: `Response status code
> does not indicate success: 404 (Not Found).`"*

The agent ran and completed; its findings are gone. Affected templates include
`review-grader` ×2, `test-coverage-review` ×2, `performance-review` ×2,
`code-simplifier` ×2, `euii-leak-detector` ×1.

These are the pre-collaboration-fix era described in the profile comment, so the fix is
likely already in place — but **the failure mode is silent and non-retryable**: the run
is recorded Completed, the note merges into the store, and only reading the file reveals
the loss. Add a hard failure (or a retry) when a delegate's transcript cannot be read,
rather than persisting a stub as a completed note.

---

## P2-2 — Substantive reviews without anchors

**Severity: medium.** 4 of the 24 substantive reviews carry **zero** `file:line`
references: `nova-5500764` (1595 b), `novaclient-5502056` (2696 b), `nova-5504975`
(651 b), `novaclient-5502059` (1917 b). Findings that cannot be anchored cannot be posted
inline and are hard to action. Require at least one anchor per finding, or mark the
finding as unanchored explicitly.

---

## P2-3 — Non-reviewer agents in the review path

**Severity: medium.** `general-purpose` ×5 and `ado:ado-devops-assistant` ×11 (the latter
is P0-1). `general-purpose` has no review rubric; whatever it found is unclassifiable
against the other dimensions. Restrict the review fan-out to review-capable templates.

---

## P3-1 — Poller rotation fix is built but not running

**Severity: low (already diagnosed, no code needed).** Astra, WeveNova and MODISService
have been polled **zero** times in three days; only Nova (15×) and NovaClient (1×) ever
were. The rotation + per-target-cap fix exists in `PrPollingService.cs:31-40,105-133` and
is compiled into the DLL (built 2026-08-08 01:53:53, contains `__poll-rotation`), but the
running process started **2026-08-07 19:06:23** — about seven hours before that build.
Confirming evidence: `SaveRotationStart` writes a cursor row before every target visit,
and `poll_cursor` contains no such row.

**Action: restart the daemon** (`.run/.relaunch-daemon.sh nova`). No code change.

---

## Not defects — do not spend time here

- **Historical 400/404 storm.** 172 × HTTP 400 and 20 × HTTP 404, **all on 2026-08-06**
  (400s confined to the 22:00 hour). Fixed by the `Marketplaces: ["gb","superpowers"]`
  correction. Zero recurrence on Aug 7-8.
- **Copilot-token warning at startup** (15 occurrences). Diagnostic only —
  `CopilotModelCatalogLogger` logs and continues.
- **85 unmerged `review/*` branches.** Open PRs; the sweeper leaves them by design
  (`PrLifecycleSweeper.cs:71-72`).
- **`review_run.pr_lifecycle_state` reading `Open` for everything.** The sweeper resolves
  lifecycle live and never writes the column back; it holds the review-time snapshot.
  Harmless unless something starts trusting it.
- **Submodule gitlinks frozen.** Separate, already specced — see
  `docs/review-store-submodule-refresh.md`.

---

## FR-1 — PAT-first ADO credential resolution (requested)

**Type: feature request, not a defect.** No PAT path exists today — a repo-wide search for
`AZURE_DEVOPS_EXT_PAT`, `ADO_PAT`, `PersonalAccessToken`, `System.AccessToken` across
`src/` and `samples/CodeReviewDaemon.Sample/` returns nothing. ADO auth is MSAL-only.

**Required behaviour — strict priority order:**

1. **If a PAT is configured, use it.** No MSAL call, no token-cache read, no silent
   refresh.
2. **Otherwise fall back to the existing MSAL path** unchanged
   (`src/LmAgentInfra/Auth/AdoOAuthProvider.cs` — `AcquireTokenSilent` at `:179`,
   `AcquireTokenInteractive` at `:113`).

**Where it has to land — there are two consumers, and both need it:**

| Consumer | Seam | Credential shape |
|---|---|---|
| git over HTTPS (clone/fetch/push) | `Workspace/Sandbox/HostGitCredentialEnv.cs:82` | `Authorization: Basic base64(":" + PAT)` |
| ADO REST (poll, project metadata, threads) | `AdoOAuthProvider` token source | `Authorization: Basic base64(":" + PAT)` |

ADO PATs authenticate as HTTP Basic with an **empty username** and the PAT as password —
*not* as `Bearer`. `HostGitCredentialEnv` already builds a Basic header for the git side
(`:17`, `:82`), so the git half is mostly a source swap. The REST half currently receives
an MSAL bearer token and will need the scheme to become conditional.

**Config surface (suggested):** `Auth:Ado:PersonalAccessToken`, overridable by env var
`CRD_ADO_PAT`. Env must win over appsettings so a PAT never has to be written to a file
that could be committed.

**Requirements:**
- Log *which* mechanism was selected at startup, once — `"ADO auth: PAT (from CRD_ADO_PAT)"`
  or `"ADO auth: MSAL (token cache)"`. Today there is no way to tell from the logs which
  credential is in play; this audit had to infer it from `msal-ado.bin` mtime.
- **Never log the PAT**, and never include it in the sub-agent prompt or any note written
  into the store. The store is a real repo and notes are committed.
- Fail fast and loudly if a PAT is set but rejected (401/403). Do **not** silently fall
  back to MSAL — a mis-scoped PAT that silently degrades to a different identity is worse
  than a hard failure, because the review then posts as the wrong account.
- Required PAT scopes for the full daemon surface: **Code (Read)** for clone/fetch,
  **Pull Request Threads (Read & Write)** for posting, **Project and Team (Read)** for the
  project-visibility lookup at `DaemonOperationPolicy.cs:125`.
- Note the interaction with **P0-1**: a PAT with thread-write makes the collect-only
  sub-agent bypass genuinely able to post. Land the P0-1 dispatch guard **before or with**
  this, not after.

---

## Suggested order of work

1. **P0-1** — before ADO egress is restored. This is the only issue that can damage other
   teams' PRs. **Land this before or with FR-1** — a thread-write PAT removes the
   accidental infrastructure barrier that is currently the only thing stopping posts.
2. **P3-1** — free; restart unblocks 3 of 5 repos immediately.
3. **FR-1** — PAT-first ADO credentials.
4. **P0-2** and **P1-1** — together; both are the synthesis/fan-out contract, and fixing
   coverage without fixing empty-synthesis just produces better-covered empty reviews.
5. **P1-2** — restores cross-repo context.
6. **P2-x** — hygiene.

## How to re-measure after fixes

```bash
STORE=~/Gateway/workspaces/codereview-daemon-nova-*/review-sweeper-store

# P0-2: empty-review rate — target 0 empty with 0 delegates
find $STORE/PRs -name review.md -size -60c | wc -l

# P1-1: coverage by real template (filename slugs are free-text labels, not agent names)
grep -h '^| Template' $(find $STORE/PRs -name 'PR_Findings_*') \
  | sed -E 's/^\| Template *\| *//; s/ *\|$//' | sort | uniq -c | sort -rn

# P0-1: any posting-capable agent on a collect-only run — must be empty
grep -l 'ado:ado-devops-assistant' $(find $STORE/PRs -name 'PR_Findings_*')

# P2-1: transcript stubs — must be empty
find $STORE/PRs -name 'PR_Findings_*' -size -900c
```

**Measurement note for whoever verifies this:** the `PR_Findings_*` *filename* slug is a
free-text label the lead invents per PR (90+ distinct spellings — `schema-review`,
`schema-reviewer`, `flag-schema-review`, `delta-schema-review` are all one agent). Only
the `| Template |` row names the real agent. Counting filenames will give a wrong answer.
