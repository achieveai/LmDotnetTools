# CodeReviewDaemon Review Engagement — Design

**Date:** 2026-08-30  
**Revised:** 2026-09-02  
**Status:** Approved target contract; written-spec review complete  
**Scope:** Target behavior. Sections marked current-state describe the implementation as observed on 2026-09-02.

## 0. Decision summary

One durable PR engagement coordinator sits above the existing code-review stage machine. It consumes provider activity and opens exactly one typed round:

1. `CodeReview` — the latest head has not been reviewed.
2. `DiscussionFollowUp` — the head is unchanged and relevant external discussion arrived.
3. `MergedClose` — the PR merged and needs final evidence reconciliation, learning promotion, and an outcome report.

The existing code-review stages remain:

`Discovered → ContextReady → Reviewed → Judged → Posted`

The target adds these guarantees:

- The `code-reviewer:pr-context-gatherer` **agent** explores PR intent and related context iteratively before code specialists run.
- Clarification questions publish without holding the code review open. Conclusions that depend on unanswered assumptions are withheld.
- Same-head discussion follow-ups do not re-review unchanged code or dispatch the full review fleet.
- No new engagement round—whether triggered by comments or a pushed head update—opens until at least one hour after the prior engagement completes. Activity is observed and coalesced immediately; merge alone bypasses the gate.
- The parent agent authors actions and invokes the `code-reviewer:post-pr-review` **skill** through typed operations.
- A backend retains credentials and enforces scope, staleness, provider mechanics, idempotency, and receipts.
- Complete rendered prompts, complete returned model messages, judge exchanges, and typed tool events form an immutable audit source. Bounded Markdown is only a projection.
- Every merged PR gets evidence-backed outcome and promotion reports. `Closed` and `Abandoned` PRs do not.
- Raw observations are retained indefinitely under this design. A future cleanup policy is separate work.

## 1. Why the current design changes

### 1.1 Current-state findings

The daemon currently:

- polls providers and creates or resumes commit-identified runs;
- assembles context once before review;
- does not invoke `pr-context-gatherer` in the production review path;
- has a registered but unwired GitHub linked-issue reader;
- has no durable clarification-question lifecycle;
- fetches existing discussion once before a code review;
- forces agent-inline posting off on the deployed S2S path;
- publishes one host-authored PR-level summary through an outbox;
- persists a final review and a bounded judge artifact in SQLite;
- commits capped transcript projections to the PR notes branch;
- extracts Knowledge Base and legacy developer feedback only when a PR merges.

Source anchors:

- `samples/CodeReviewDaemon.Sample/Orchestration/StageMachine.cs`
- `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/ReviewPoster.cs`
- `samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/ReviewNotesArtifactBuilder.cs`
- `samples/CodeReviewDaemon.Sample/Orchestration/PrLifecycleSweeper.cs`

### 1.2 Specific gaps

1. A static concatenated brief is not an iterative context investigation.
2. Same-head activity is described in provider/run comments, but `ReviewStore.CreateOrGetReviewRun` ignores `TriggerWatermark`; it therefore cannot open a distinct discussion round.
3. No review-specific one-hour re-engagement gate exists in code or configuration.
4. Questions cannot be identified, posted, correlated, and revisited as durable entities.
5. The current S2S publisher loses the agent's action-level judgment and wording structure.
6. The unwired fenced `review-actions` YAML parser would require the host to reconstruct model intent from prose.
7. Judge output exists in SQLite but is not visible in the per-PR audit notes.
8. Exact prompts and specialist replies are not durably complete:
   - prompt provenance stores a template hash, not rendered bytes;
   - specialist Markdown caps one message at 6,000 characters and one file at 12,000;
   - structured finding rows omit full specialist bodies;
   - the exact assembled review input is never persisted.
9. The shipped legacy developer-feedback writer and the accepted append-only `DeveloperLearnings/` design are competing authorities.
10. Close-time extraction can exhaust retries and then proceed without durable promoted learning or an outcome report.

## 2. Goals and non-goals

### 2.1 Goals

- Give one durable component authority over activity, cooldown, round intent, and close processing.
- Preserve the existing code-review stage machine and its resume/retry guarantees.
- Gather provider-aware PR intent dynamically and with cited evidence.
- Participate in review discussions like a careful developer, not a broadcast-only bot.
- Ask decision-relevant questions without delaying independent review conclusions.
- Preserve agent judgment and wording through typed publication tools.
- Prevent stale, duplicate, cross-PR, or mechanically invalid posts.
- Retain complete model-interaction evidence without forcing complete history into later model prompts.
- Capture per-round observations without promoting provisional claims to global truth.
- Promote only merged, verified, generalizable knowledge and developer patterns.
- Measure merged-review outcomes from evidence rather than model self-assessment.
- Preserve provider-native discussion fidelity.

### 2.2 Non-goals

- Webhooks as lifecycle authority. They may become wake-up hints later; polling remains authoritative.
- Blocking a review indefinitely for developer input.
- Replying to every PR comment.
- Re-running code review when only discussion changed.
- Giving raw credentials or unrestricted REST access to a model.
- Parsing free-form Markdown/YAML to discover actions the agent intended to publish.
- Treating temporal sequence as proof that a reviewer caused a fix.
- Producing close reports or promoted learning for `Closed` or `Abandoned` PRs.
- Designing the future cleanup policy for indefinitely retained observations.
- Automatically changing review policy based on judge scores.

## 3. Architecture

### 3.1 Durable PR engagement coordinator

Create one coordinator record with a surrogate `PrEngagementId` and a unique stable `(provider, repository, PR)` identity. Rounds reference `PrEngagementId`; the surrogate never changes PR identity or permits two coordinators for one tuple. It owns:

- latest observed head and base;
- last reviewed head;
- PR lifecycle;
- last completed engagement time;
- `next_eligible_at`;
- latest observed, pending, and consumed external-activity watermarks;
- active and latest round IDs/intents;
- open clarification-question IDs;
- the root-summary provider receipt;
- close-processing progress and durable parking state.

A per-PR lease permits one active round. New events received while a round runs or while cooldown applies are durably coalesced.

The coordinator does not replace the current `ReviewRun` stage machine. A `CodeReview` round owns or references a normal review run. Discussion and close rounds use their own typed progress without pretending to be code-review stages.

### 3.2 Round identity

Every engagement round has:

- stable round ID;
- `PrEngagementId` foreign key;
- intent: `CodeReview`, `DiscussionFollowUp`, or `MergedClose`;
- frozen head/base and PR lifecycle;
- frozen provider-activity lower/upper bounds;
- prior-observation boundary;
- started/completed/superseded timestamps;
- status: `Pending`, `Running`, `RetryPending`, `Completed`, `Superseded`, or `Parked`;
- governed-failure/parking data;
- source-record and typed-action references.

No round exists merely because activity arrived during cooldown. That pending demand lives as head/activity state on `pr_engagement`. At eligibility, the coordinator creates one `Pending` round for the latest coalesced state; lease admission moves it to `Running`. Retries reuse the same round identity. A materially different admitted input creates a new round.

### 3.3 Event normalization and self-trigger exclusion

Provider polling normalizes:

- current lifecycle;
- head/base identity;
- latest external discussion activity;
- stable comment/thread IDs and timestamps;
- provider-native ancestry and status where available.

Daemon-authored activity is excluded through exact typed publication receipts. Author name, body comparison, and hidden marker alone are insufficient as the primary self-trigger discriminator.

Watermarks are monotonic provider-qualified records, not only timestamps. Ties are broken with stable provider IDs so comments sharing one timestamp are neither skipped nor replayed.

### 3.4 Eligibility and precedence

1. The first code review is immediately eligible.
2. After a successfully finalized `CodeReview` or `DiscussionFollowUp`, set `next_eligible_at = completed_at + 1 hour`.
3. A failed or superseded round does not start the cooldown.
4. External activity during cooldown is coalesced.
5. At eligibility:
   - if the head changed, choose `CodeReview` and include accumulated discussion;
   - otherwise, if relevant external discussion exists, choose `DiscussionFollowUp`;
   - otherwise, do nothing.
6. A new head replaces pending same-head discussion demand before a round is admitted. If a discussion round is already `Pending` or `Running`, the new head supersedes it and the later code-review round consumes its discussion window.
7. The one-hour gate deliberately applies to both comment-triggered follow-up and new-head code review. A push is observed immediately but can wait up to one hour after the prior successful engagement; further activity coalesces into the latest eligible code review.
8. Merge preempts ordinary work and chooses `MergedClose`. It is not subject to cooldown.
9. Any terminal lifecycle other than `Merged`—currently `Closed` or `Abandoned`—uses no-report/no-promotion cleanup.

Relevance is an agent judgment inside a cheap discussion triage turn, not a code-side keyword list. Mechanical triage first removes self-authored, already-consumed, deleted, and provider-system activity.

## 4. `CodeReview` round

### 4.1 Frozen bootstrap envelope

The coordinator freezes:

- repo/PR/provider identity;
- base, head, and merge base;
- complete changed-file inventory;
- linked issue/work-item and related-PR refs;
- relevant discussion refs and activity bounds;
- open clarification-question refs;
- workspace and `KnowledgeBase/` paths;
- prior observation/source-record boundary;
- configured policy/model identities.

Provider text is untrusted data. The envelope contains refs and compact navigation, not an uncontrolled copy of every body or diff.

### 4.2 Dynamic context

Dispatch the installed `code-reviewer:pr-context-gatherer` agent first. This is an agent definition, not the posting skill.

The agent receives read-only, PR-scoped tools and may iterate over:

- PR metadata and full changed-file inventory;
- linked GitHub issues or ADO work items;
- related PRs, dependency hierarchy, milestones/projects, and non-goals;
- existing review discussion;
- repository guidance and relevant Knowledge Base material;
- focused checkout files and history.

It writes a sourced context manifest. Every material claim carries a provider, commit, file, or discussion reference. It records unknown, failed, unavailable, none-linked, and truncated provider results distinctly.

The prior “pre-supplied context mode” remains useful as bootstrap behavior, but does not reduce the gatherer to static synthesis. The agent may follow supplied refs through its scoped tools.

A failure to establish required repo/head/workspace scope fails the round before publication. Missing optional context becomes explicit uncertainty. Dependent conclusions are omitted or qualified.

### 4.3 Parent review and specialists

The parent agent receives the sourced context manifest plus frozen code-review inputs. It dispatches code specialists as evidence requires.

The current provisional/barrier/synthesis invariant remains:

1. Collect a provisional parent response with posting disabled.
2. Observe the full descendant roster.
3. Wait until descendants are terminal and the roster is stable.
4. Recheck lifecycle/head.
5. Synthesize on the same parent thread with further spawning disabled.
6. Treat only this synthesis as authoritative.

Failed or unreadable dimensions are disclosed. Their absence never reads as a clean review.

### 4.4 Questions and qualified review

When a material conclusion depends on unknown developer intent, the agent creates a typed clarification question. It can continue reviewing independent dimensions.

The authoritative output separates:

- supported findings and conclusions;
- questions already published or ready to publish;
- conclusions withheld because an assumption remains unanswered.

A no-finding code review may finalize with no PR post when it would add no material information. It still retains complete evidence and starts cooldown after successful finalization.

### 4.5 Optional judging

Judging remains after authoritative review generation and before ordinary round finalization/publication tracking. A judge never silently rewrites or routes the review.

The judge audit record contains:

- exact rendered judge system prompt;
- exact judging input;
- exact raw response;
- parsed score and rationale;
- ballot count/status and abstention/exclusion reason;
- judge and generator effective model identities;
- self-grade status;
- rubric/version identity;
- source-record IDs and content hashes.

A concise judge projection is written under the PR audit documents and links to the complete records. Unknown provenance stays unknown.

## 5. Clarification and discussion lifecycle

### 5.1 Typed clarification question

A question records:

- stable question and originating round IDs;
- exact wording;
- provider target ref, file, and line when applicable;
- evidence that exposed the ambiguity;
- the conclusion withheld pending an answer;
- typed action ID, provider receipt, and asked time;
- state: `Open`, `Answered`, `Contested`, `Superseded`, or `UnansweredAtMerge`;
- candidate-answer refs and interpretation evidence.

A question counts as asked only after a provider receipt exists.

### 5.2 Non-blocking rule

Questions publish immediately with the rest of the useful review actions. They do not keep the current model turn or round open indefinitely.

The review may publish conclusions that do not depend on the answer. It must not guess or publish the dependent conclusion as fact.

### 5.3 Candidate-answer correlation

Mechanical candidate collection uses:

- provider-native thread ancestry;
- exact question/action IDs;
- direct replies where supported;
- references, mentions, and permalinks;
- external comments inside the unconsumed activity window.

The discussion agent interprets whether a candidate answers the question. The daemon does not infer agreement from timing, thread closure, or a generic acknowledgement. Conflicting answers become `Contested`.

### 5.4 `DiscussionFollowUp` round

This round is eligible only when:

- the head is unchanged;
- relevant unconsumed external discussion exists;
- no newer head requires code review;
- the one-hour gate has elapsed.

Its prompt states prominently:

> The code head has not changed. This is a discussion follow-up. Answer or add value to the new comments. Do not repeat the code review.

Its inputs are limited to:

- new comments inside the frozen activity window;
- ancestor/thread context needed to interpret them;
- open questions and candidate answers;
- prior context manifest and observations;
- focused checkout access when a response needs code evidence.

The full specialist fleet is unavailable in this intent. The parent discussion agent may perform focused code reads, answer a question, ask a follow-up, correct a prior conclusion, or add directly relevant evidence.

A new finding is permitted only when causally tied to the new discussion and supported by focused code evidence. If broader code review becomes necessary, the discussion round records that need; it does not silently transform itself into `CodeReview` on an unchanged head.

The agent participates only when it can answer, correct, add evidence, or ask a decision-relevant question. A genuine no-op records local finalization and produces no PR noise.

## 6. Agent-owned typed publication

### 6.1 Responsibility split

| Parent agent decides | Typed backend enforces |
|---|---|
| Whether an action adds value | Repository, PR, and provider scope |
| Wording and grouping | Credential isolation |
| Finding vs. question vs. reply vs. summary delta | Open lifecycle and expected head |
| Thread to continue from supplied refs | Ref existence, kind, and replyability |
| Whether to say nothing | Diff path/side/line validity |
| Root-summary/delta content | Size and provider capability |
| — | Idempotency, backstop lookup, receipts, secret-safe logs |

The host never substitutes its own summary when agent publication partly fails.

### 6.2 Posting skill and typed operations

The parent invokes the `code-reviewer:post-pr-review` skill. This is the posting skill; `pr-context-gatherer` is the context agent.

The skill is extended to call provider-neutral typed operations:

- `CreateRootSummary`
- `AppendSummaryDelta`
- `SubmitInlineFindings`
- `PostClarificationQuestion`
- `ReplyToDiscussion`
- `FinalizeRound`

Inputs are typed values. The host does not parse Markdown/YAML to recover actions. The skill does not use raw `curl` with exposed credentials.

Each action has a stable `(round ID, action ID)`. The backend returns either:

- a typed receipt with provider/thread/comment/review IDs and accepted time; or
- a typed rejection with mechanical reason and current scope evidence.

### 6.3 Root summary

The first material review creates one PR-level root summary. Later code and discussion rounds append only their delta:

- resolved findings;
- new findings;
- answered or still-open questions;
- corrected conclusions;
- remaining uncertainty.

History is never replaced. No disconnected summary roots are created.

### 6.4 Provider-native fidelity

#### Azure DevOps

Preserve:

- thread ID;
- comment ID and `parentCommentId`;
- thread status;
- file and left/right span context;
- PR iteration/change-tracking context.

Replies post to `/threads/{threadId}/comments`. Rich ADO thread structure is not flattened to a generic comment list.

#### GitHub

Preserve inline review thread identity and line/range or file-level anchors. Replies target the top-level review comment through the review-comment replies endpoint; GitHub does not recursively nest replies beneath replies.

PR-level conversation comments use issue-comment endpoints. The official issue-comment contract exposes no nested reply relation. Root-summary deltas therefore include the root permalink and stable action ID while remaining flat provider comments. `ReplyToDiscussion` uses the same flat-comment treatment for any external GitHub PR-level issue comment: quote or link the supplied target permalink and retain the stable action/question ID. The receipt records this explicit degradation.

### 6.5 Exactly-once and stale safety

Before each send, the backend revalidates:

- PR lifecycle;
- expected head/version;
- target ref and permissions;
- diff anchor;
- action idempotency.

A crash after provider acceptance but before local commit recovers from durable receipts and a provider-side backstop. Partial publication resumes only missing actions.

A stale action is rejected, never moved to a different line or flattened into a summary. A closed-lifecycle rejection caused by a merge that arrived mid-round is benign supersession, not a governed review failure.

## 7. Complete audit source and bounded projections

### 7.1 Source-of-truth records

Every model turn persists exact rendered content:

- complete system prompt;
- complete task/input prompt;
- complete follow-up prompt;
- every complete returned model message;
- provisional and authoritative review messages;
- context-gatherer, specialist, synthesis, discussion, judge, close-analyst, and verifier turns;
- typed tool calls/results required to reconstruct context and publication;
- typed posting receipts/rejections.

“Complete” means every byte the daemon actually sent or received after boundary-level secret exclusion and any upstream transport limit. It does not claim to recover bytes an external provider or sandbox never delivered.

Records are immutable, sequenced, chunked/content-addressed when necessary, and carry:

- source-record ID;
- round and parent-turn IDs;
- role/message/tool type;
- model/provider identity;
- sequence;
- content hash and byte count;
- capture outcome;
- timestamps;
- redaction metadata.

No source record applies a per-message tail clip or whole-artifact character cap. Missing, severed, or unreadable content is a typed gap, never a clean empty response.

Structured review, finding, question, judge, and close-report items reference exact source-record IDs and hashes. They do not rely on an undocumented same-run join.

### 7.2 Security and redaction

Lossless audit does not mean secret retention.

- Credentials and known secret fields are excluded before persistence.
- Logs never contain credentials or full sensitive bodies by default.
- Required later redaction writes a versioned redaction record with original content hash and affected ranges.
- A redacted projection never masquerades as byte-identical source.
- Archive access follows repository/PR confidentiality boundaries.

### 7.3 Projections

Human Markdown and downstream model inputs remain bounded. They may summarize, rank, or omit content to protect usability and context windows.

Every omission:

- states item/byte counts;
- links to complete source records;
- distinguishes unavailable source from deliberate projection;
- preserves tail-relevant evidence through source links.

Current `PR_Context_*`, `PR_Findings_*`, reconciliation, `review.md`, and judge views become projections. They are never the only copy of an agent's words.

### 7.4 Retention

Immutable source records and per-round observation bundles are retained indefinitely under this target.

A future cleanup process may define legal, privacy, storage, and evidentiary rules later. Until separately designed and approved, it does not delete or shorten these records.

## 8. Per-round observations and active PR memory

Each finalized round appends an immutable observation bundle containing:

- frozen input/activity boundaries;
- context claims and gaps with source refs;
- provisional and final findings;
- questions, answers, corrections, and contested interpretations;
- discussion contributions and deliberate no-actions;
- typed publication outcomes;
- judge/verifier outcomes;
- supersession/failure/parking state;
- links to complete model/tool source records.

Observations are evidence, not Knowledge Base truth.

A replaceable active-PR projection summarizes current conclusions, open questions, and consumed activity. It is regenerated from immutable records and may be rebuilt after a crash.

## 9. `MergedClose` round

### 9.1 Trigger and frozen evidence

`MergedClose` starts when the authoritative provider poll confirms merge. It freezes:

- merged commit and final diff;
- complete PR discussion and provider-native thread state;
- all engagement rounds and source records;
- questions and candidate answers;
- typed actions and receipts;
- final code state;
- current Knowledge Base and developer-learning destinations.

It does not post new review comments to the closed PR.

### 9.2 Deterministic candidate inventory

Code first constructs the denominator from typed records:

- questions;
- actionable suggestions/findings;
- blockers and high-severity findings;
- substantive comments/replies;
- provider thread dispositions;
- related commits/final-diff changes;
- promotion candidates and outcomes.

A model does not decide which source items existed.

### 9.3 Classification and independent verification

A close analyst proposes semantic labels. An independent verifier checks every label and citation against source records, provider discussion, and final code.

Only confirmed items enter definitive counts. Unresolved evidence becomes `indeterminate`. A verifier that rereads only the rendered report is invalid; it must read the underlying sources.

### 9.4 Required measures

The report includes linked item sets and aggregate counts for:

- questions asked, answered, contested, superseded, and unanswered at merge;
- actionable suggestions addressed, not addressed, or indeterminate in final code;
- blockers/high-severity issues novel when this reviewer posted them;
- equivalent blockers/high-severity issues independently found by others, with timing;
- substantive novel comments or replies not already present when posted;
- reviewer posts followed by a matching fix or thread resolution;
- Knowledge Base and developer-learning promotions, with source-observation and destination-record links;
- explicitly declined or failed promotions.

“Followed by” is temporal evidence, not causation. Causal credit is claimed only when discussion or commit evidence states it.

“Value added” is the linked set of confirmed outcomes above. The reviewer does not assign itself a score or write an unsupported testimonial.

### 9.5 Promotion order

Close processing runs in this order:

1. freeze final evidence;
2. build candidates;
3. classify;
4. independently verify;
5. run idempotent promotion passes;
6. render final reports including promotion outcomes;
7. archive and finalize.

Promotion passes:

1. confirmed generalizable repository/domain facts → `KnowledgeBase/`;
2. confirmed developer-specific patterns → append-only `DeveloperLearnings/`, followed by regenerated views.

The accepted append-only `DeveloperLearnings/` architecture becomes canonical. The legacy whole-file `KnowledgeBase/developers/*.reviewfeedbacks.md` records are migration input, not a continuing competing writer.

Promotion is idempotent by source observation ID. Raw observations remain after promotion.

### 9.6 Outputs

Each merged PR archive receives:

- `outcome.json` — machine-readable candidates, labels, verification, counts, evidence links, and promotion results;
- `OUTCOME.md` — concise human-readable results with linked examples;
- complete close-analyst and verifier source records;
- promotion receipts or explicit declined/failed results.

Finalization requires every required output to be written, explicitly declined, or durably parked as visible `Blocked` work.

## 10. Terminal non-merge behavior

`Closed` and `Abandoned` PRs:

- produce no outcome/value report;
- promote no Knowledge Base or developer learning;
- retain immutable observations and source records;
- clean working branches/workspaces according to lifecycle policy.

The no-promotion rule does not authorize deletion of the immutable audit source.

## 11. Recovery and failure semantics

### 11.1 Ordinary rounds

- A head change during code review supersedes code-dependent publication and queues the latest head.
- A head change during discussion supersedes that round; the next code review consumes its comments.
- Supersession is not a governed failure.
- The current code-review lease remains released in terminal cleanup.
- Retry resumes after the last durable boundary and reuses round/action IDs.

### 11.2 Merge during an active round

When merge arrives while an ordinary round holds the PR lease:

1. mark the ordinary round superseded, not failed;
2. cancel further model and unsent publication work;
3. durably release the lease;
4. retain any provider-accepted actions as evidence;
5. make `MergedClose` next.

A provider rejection caused solely by the now-closed lifecycle does not consume governed-failure budget.

### 11.3 Publication crash

A crash after provider acceptance recovers through typed action receipts and provider backstop lookup. The daemon never posts a host-written replacement for an agent action.

### 11.4 Close processing

Close retries survive restart. They reuse the existing governed-failure and durable-parking contract.

`Blocked` is the visible disposition of a durably parked close engagement, not a second retry subsystem. Exhausted attempts do not silently skip extraction or claim completion.

Reports and projections are regenerable from immutable source. Regeneration does not alter source records.

## 12. Data contract sketch

Names are conceptual. The implementation plan may fit them to existing SQLite/review-store conventions without changing their invariants.

### `pr_engagement`

- surrogate `PrEngagementId` plus unique provider/repository/PR identity
- PR lifecycle and latest/last-reviewed head
- external activity bounds
- last completion and next eligibility
- active/latest round
- root-summary receipt
- close progress and parking

### `engagement_round`

- round ID, `PrEngagementId`, intent
- frozen code/activity bounds
- `Pending` / `Running` / `RetryPending` / `Completed` / `Superseded` / `Parked` status
- timestamps
- failure/parking data

### `audit_source_record`

- ID, round, parent turn
- sequence, role/type
- complete bytes or chunk refs
- hash, length, capture outcome
- model/provider identity
- redaction metadata

### `clarification_question`

- ID, originating round
- wording/evidence/withheld conclusion
- target/action/receipt
- state and candidate-answer refs

### `review_action`

- stable action ID and typed payload
- expected provider scope/head
- status, rejection, receipt
- provider-native thread/comment/review identity

### `round_observation`

- immutable structured claim/event
- source-record refs
- verification/supersession state

### `close_outcome_item`

- deterministic source item
- proposed and verified label
- evidence links
- promotion links

## 13. Verification contract

Each behavior requires a distinguishing test. Absence-based effects require non-vacuity proof.

### 13.1 Coordinator and cooldown

- Same-head external activity opens exactly one discussion round no earlier than one hour after completion.
- Activity received during cooldown is not lost.
- Daemon-authored receipts do not self-trigger.
- Equal-timestamp comments are consumed exactly once by stable provider ID.
- A new head during cooldown produces one code review that includes pending discussion, not two rounds.
- Merge bypasses cooldown and supersedes an active ordinary round without charging failure budget.

### 13.2 Intent isolation

- The discussion prompt states unchanged code and discussion-only purpose.
- The full specialist fleet is unavailable to `DiscussionFollowUp`.
- A comment-only round cannot execute code-review stage machinery by default.
- Focused code reads remain available for evidence-backed answers.

### 13.3 Context and questions

- The real installed `pr-context-gatherer` agent is resolved and dispatched.
- It performs iterative scoped reads and emits cited context.
- Required-scope failure prevents publication.
- A posted question receives a stable receipt and withholds its dependent conclusion.
- A later provider reply is correlated and interpreted with source evidence.
- Conflicting replies produce `Contested`, not an invented answer.

### 13.4 Typed publication

- The agent's typed body reaches the provider unchanged after mechanical validation.
- Invalid/stale refs reject without retargeting.
- Killing the daemon after provider acceptance does not duplicate an action.
- ADO retains thread, parent-comment, status, line, and iteration identity.
- GitHub inline replies preserve their top-level review thread.
- GitHub PR-level deltas record flat-comment degradation and root permalink.
- A partial failure resumes only missing actions; the host writes no substitute summary.

### 13.5 Complete audit

- Rendered prompts and returned messages larger than current 6,000/12,000-character caps round-trip exactly.
- A sentinel appended at the tail survives storage and retrieval; deleting or changing it fails the test.
- Whole multi-message transcripts remain ordered and complete across chunk boundaries.
- Judge system prompt, judging input, raw response, parsed result, rubric, and model provenance are linked by explicit IDs/hashes.
- Missing/severed capture is a typed gap, not an empty success.
- Bounded Markdown discloses omissions and links to the complete source.
- Secret fixtures are excluded/redacted with visible metadata.

### 13.6 Merged close

- `Closed` and `Abandoned` retain observations but produce no report or promotion.
- Every merged aggregate recomputes from linked source items.
- Mutating/removing a cited source makes verification fail or changes the item to `indeterminate`.
- Novelty compares against discussion as it existed before posting.
- “Addressed” is checked against final code, not thread status alone.
- Promotion appears in the final report with source and destination links.
- A promotion failure leaves close visibly pending/blocked and preserves all evidence.

## 14. Rollout

1. Release/install the companion plugin version containing the required context-agent and typed posting-skill contracts.
2. Add complete audit capture before relying on new effectiveness statistics.
3. Add the coordinator and typed round persistence behind a feature flag.
4. Seed one coordinator for every open PR already known to the store before enabling eligibility:
   - derive last reviewed head and last successful completion from existing runs;
   - set `next_eligible_at` from that completion plus one hour;
   - adopt the latest external activity boundary without marking unseen activity consumed;
   - adopt the existing root summary only from provider/outbox evidence;
   - record missing historical prompt/transcript/judge material as explicit legacy gaps, never fabricated source records.
5. Keep eligibility disabled until seeding is idempotently complete; then run discussion detection and close classification in shadow mode. Newly discovered PRs use normal coordinator creation.
6. Keep provider writes disabled while inspecting typed action/rejection/receipt plans.
7. Enable agent-owned posting per provider only after exactly-once and fidelity tests pass.
8. Enable merged reports before learning promotion.
9. Enable Knowledge Base and `DeveloperLearnings/` promotion after report traceability passes.
10. Remove fenced-YAML and legacy competing developer-feedback paths only after migration evidence is complete.

Deployment must preserve the current collect-only safety default until live posting is separately authorized.

## 15. Material risks

### Indefinite complete retention

Complete prompts and responses can contain sensitive repository content. Exclude secrets before persistence, record later redaction, enforce repository access boundaries, and leave future deletion to a separately approved policy.

### Agent-owned external writes

Agent judgment improves wording and thread participation, but writing is externally consequential. Typed, least-privilege backend tools retain credentials and mechanical policy; the model never receives raw secrets.

### Semantic outcome statistics

Novelty, addressed status, and causal attribution can be overstated. Deterministic candidate inventories, independent source-based verification, evidence links, and `indeterminate` prevent invented precision.

### Provider skew

ADO carries richer thread/status/iteration structure than GitHub PR-level conversation comments. Preserve each provider's native model and record explicit degradation. Do not claim artificial parity.

### Audit volume and context pressure

Complete source records increase storage. Chunk/content-address them and keep model/human projections bounded. Never solve prompt-size pressure by truncating the sole audit copy.

## 16. Rejected alternatives

### Extend the linear five-stage enum for every event

Rejected. Repeatable same-head discussion and merged-close work are not linear continuations of one code-review run. Stage expansion would weaken existing resume semantics.

### Independent comment, learning, and close sidecars

Rejected. They create competing cooldown, posting, evidence, and lifecycle authorities.

### Fenced Markdown/YAML action handoff

Rejected. It requires the host to infer agent intent from free-form output and reintroduces parser/correction failure modes.

### Raw agent REST calls

Rejected. They expose credentials and bypass typed scope, stale-head, idempotency, and receipt enforcement.

### Host-authored fallback summary

Rejected. It loses agent taste and can contradict or duplicate partially accepted agent actions.

### Block review while waiting for answers

Rejected. It delays independent value and can strand reviews indefinitely.

### Promote learning after every round

Rejected. Round observations are provisional evidence. Merged final code and discussion provide the promotion boundary.

### Lowest-common-denominator provider model

Rejected. ADO and GitHub inline thread fidelity would be discarded merely because GitHub PR-level comments are flat.

## 17. Official provider references

- GitHub pull-request review comments and replies: https://docs.github.com/en/rest/pulls/comments
- GitHub PR/issue conversation comments: https://docs.github.com/en/rest/issues/comments
- GitHub pull-request reviews: https://docs.github.com/en/rest/pulls/reviews
- Azure DevOps PR threads: https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-request-threads/create?view=azure-devops-rest-7.1
- Azure DevOps comments in a PR thread: https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-request-thread-comments/create?view=azure-devops-rest-7.1

## 18. Approval record

The target proposal was approved through line-anchored review on 2026-09-02 at snapshot:

`sha256:a18a1a1a3208abed1dfb168e28aa97e97fb3a788bea0770a52e14ea58cac562b`

Approval included five refinements, incorporated here:

1. use the context **agent** when both agent/skill terminology is possible;
2. expose judge judgment in PR audit documents;
3. retain complete prompts and agent replies without tail truncation;
4. keep indefinite retention while allowing a separately designed future cleanup process;
5. preserve full provider-native discussion fidelity where supported.
