# ADR 0015: The model classifies, the daemon counts

* Status: Accepted
* Date: 2026-08-10
* Related issues, PRs, or commits: epic #526 item 14 (issue #547); commit `448dfaa0` (#539, #560);
  ported under issue #553 from PR #451

> **Status note (2026-08-28, on porting into `main` under issue #553).** The repository-wide rule in
> the Decision section is in force on `main` today: `ReviewFindingReconciler` computes every review
> outcome (`Kept`, `SeverityChanged`, `Reframed`, `MergedInto`, `Dropped`) in code from text the
> daemon already holds, and the commit cited below for that corrective — `448dfaa0` — is on `main`.
> The DeveloperLearnings specifics named below are forward-looking — that subtree is not implemented
> on `main` (epic #526 item 14, issue #547).
>
> **Two citations were corrected on the way in, because as authored neither resolved from `main`.**
> The reconciler commit was cited by its sha on `daemon/review-reliability-and-pr-coverage`, which is
> reachable only from that branch (PR #451) and would resolve to nothing once it is deleted; every
> occurrence now reads `448dfaa0`, the sha the same work carries on `main` (#539, #560). The record's
> sole "Related" reference was `scratchPad/developer-learnings-spec.md` §2 — a local working file
> that was never committed to any branch, `scratchPad/*` being git-ignored, so its §-references
> cannot be followed by anyone; the reference is replaced above by ones that resolve, and this record
> is meant to be read standalone.
>
> Originally numbered ADR 0012 on `daemon/review-reliability-and-pr-coverage`; renumbered to 0015
> because `main` had already allocated 0012.

## Context

`samples/CodeReviewDaemon.Sample` is the first component in this repository whose output is a
*record that accumulates* rather than a document produced once and read once. The
DeveloperLearnings feature needs to answer how often a mistake has recurred, when it was first and
last seen, and whether it is being resolved. Every one of those is a derived value that must
survive across runs.

The design it replaces, `ReviewFeedbackAgent`, handed the whole record to the model and asked it to
rewrite the file each time. Counts and dates cannot survive that. A model asked to increment a
counter will drift, reset, and silently drop entries when it summarises — and the failure is
invisible, because a rewritten file is always internally consistent. There is no diff that looks
wrong. The record simply becomes fiction at a rate nobody can measure.

This is not a hypothesis about models in general. It is the same shape this repository has now
measured three times on this daemon: capability delivered to the model by prompt is used at
approximately zero rate (0/422, 0/158, 0/26 across three separate capabilities), while every
equivalent capability wired in code works. Commit `448dfaa0` already applied the corrective to
review reconciliation — every outcome there is computed by the daemon from text it already holds,
and nothing asks the model to self-report.

The obvious wrong answer is to keep the model in the loop and validate its arithmetic afterwards.
That fails because validation needs an independent source of truth for the count, and once the
daemon holds that source of truth it has already done the counting; the model's number is then pure
cost and pure risk. A weaker variant — "ask the model, but only for the description" — is correct,
and is what this ADR formalises.

## Decision

**No number, date, state, or file path in any daemon-rendered artifact may originate from the
model.** The model's entire contribution is classification and prose.

Concretely, for DeveloperLearnings:

* The model receives this PR's surviving findings plus the developer's existing pattern list as
  **id and one-line title only** — never the full bodies, so it cannot rewrite prose it was shown
  only for matching.
* The model returns strict JSON: for each finding, whether it is a recurring risk, and either an
  existing `patternId` **or** a `newPattern` with `slug`, `title`, and three description fields.
* The daemon validates and rejects rather than repairs. Exactly one of `patternId` / `newPattern`
  must be non-null. An unrecognised `patternId` is **rejected, never auto-created** — silent
  creation is precisely how duplicate patterns for one mistake appear and how a count stops
  accumulating. `slug` must match `^[a-z0-9][a-z0-9-]{2,63}$`, which closes path traversal by
  construction rather than by sanitisation, the same way `SlugifyAuthor` already does.
* If the reply contains any count, date, state, or path, the **whole reply is rejected**, not the
  offending field.

Pattern identity is two-tiered, and the tiers differ in who owns them. **Dimension** is a closed
set derived in code from which specialist raised the finding, so it is comparable across all
developers with no model involvement. **Pattern** is an open set scoped to one developer,
model-proposed and daemon-validated.

Out of scope: the model does not revise an existing pattern file. Pattern prose is written once at
creation. Revision is a separate mechanism and is deliberately not half-built here.

## Consequences

Counts, dates and states become auditable. Every number in a rendered file traces to an immutable
observation file the daemon wrote, so a wrong number is a bug with a reproduction rather than a
model mood.

Free-text drift is contained at the only point where it matters. Left open, *"missing null check"*,
*"null guard absent"* and *"no null validation"* become three ids and the count never accumulates.
Forcing the model to choose from a supplied set, and rejecting anything else, means drift shows up
as a rejected reply — a visible event — instead of as a quietly fragmented history.

The cost is that a rejected reply is a lost classification for that PR. The daemon records the
rejection rather than retrying into a different failure, and the observation file still records the
findings, so nothing is unrecoverable; but a run can legitimately produce fewer hits than findings.
That must be rendered, not hidden.

This rule is stated here as a repository-wide record, not a DeveloperLearnings detail. It already
governs review reconciliation (`448dfaa0`) and it should govern the next accumulating artifact
without being re-argued. A future feature that asks the model for a derived value is contradicting
an accepted decision and needs a superseding ADR.

Ongoing maintenance: the validator is now a security boundary as well as a correctness one, because
`slug` reaches the filesystem. It must be tested with a traversal payload, not only with malformed
input.
