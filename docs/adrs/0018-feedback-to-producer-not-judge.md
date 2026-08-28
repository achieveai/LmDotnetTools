# ADR 0018: Route improvement feedback to the producer, never to the judge

* Status: Accepted
* Date: 2026-08-10
* Related issues, PRs, or commits: epic #526 item 14 (issue #47); ported under issue #553 from
  PR #451

> **Status note (2026-08-28, on porting into `main` under issue #553).** The decision stands, but the
> removal it records has **not** landed on `main`. All four things the Decision says are gone still
> exist: `ReviewFeedbackAgent` (`samples/CodeReviewDaemon.Sample/Agents/ReviewFeedbackAgent.cs`),
> `DaemonReviewStageExecutor.PrependDeveloperFeedbackAsync`, `MaxDeveloperFeedbackChars`, and the
> `EnableReviewFeedbackAgent` option — still default-`false` and still set in zero appsettings
> profiles, so the loop still has never run. `AtCloseExtractionSeam.Combine` is present with its
> `Wrote > Failed > Declined` precedence unchanged. Read this as an accepted decision awaiting
> execution alongside epic #526 item 14 (issue #47), not as a description of the current tree.
>
> One citation was corrected on the way in: this record's sole "Related" reference was
> `scratchPad/developer-learnings-spec.md` §14 — a local working file that was never committed to any
> branch, `scratchPad/*` being git-ignored, so its §-references cannot be followed by anyone; it is
> replaced above by references that resolve from `main`.
>
> Originally numbered ADR 0015 on `daemon/review-reliability-and-pr-coverage`; renumbered to 0018
> because `main` had already allocated 0012–0014 and this record's three companions shifted with it.

## Context

`DaemonReviewStageExecutor.PrependDeveloperFeedbackAsync` injects a PR author's accumulated
developer record into **the reviewer's** input, so the reviewer knows what that author tends to get
wrong. It has never had a record to read — `EnableReviewFeedbackAgent` is default-false and set in
zero appsettings profiles — so the loop it creates has never actually run. That is luck, not
design.

An earlier note in this repository claimed nothing had ever consumed the developer record. That is
wrong, and the correction matters: this method is a consumer, and deleting `ReviewFeedbackAgent`
without deciding what happens to the consumer would have silently preserved the wrong architecture
behind a disabled flag. A flag that is off is not the same as code that is dead.

Reviewer-side injection creates a self-reinforcing loop:

1. The record says the author misses null guards.
2. The reviewer is told to check that first, on that author's PR.
3. It therefore finds more null-guard issues on that author's PRs than on anyone else's.
4. The count rises, which strengthens the instruction.

Both consequences are fatal to the measurement in
[ADR 0017](0017-resolution-from-absence-over-exposed-prs.md). Counts stop being comparable between
developers, because each developer's reviewer was primed differently — the cohort baseline the
guard depends on is no longer a baseline. And `Resolved` becomes nearly unreachable for any pattern
already in the file, because the review is hunting for exactly the thing whose absence it is
supposed to establish. The system would be measuring its own instructions.

The original intent was never reviewer-side anyway: the **coding agent** should improve. Making the
reviewer search harder is a different feature that happens to share a data source.

No ADR records the introduction of reviewer-side injection. This record therefore documents a
retirement whose adoption was never documented, which is itself the reason the loop survived
unexamined for as long as it did.

## Decision

**Improvement feedback is delivered to the producer of the code, not to the judge of it.**

`ReviewFeedbackAgent` and its reviewer-side injection are removed entirely rather than migrated.
No records have ever been produced, so there is nothing to migrate and no deprecation window is
needed. `PrependDeveloperFeedbackAsync` and `MaxDeveloperFeedbackChars` go with it.

**Reviewer-side injection is not to be re-added.** A future change that gives the reviewer a
developer's history is contradicting an accepted decision and needs a superseding ADR that explains
what it does to [ADR 0017](0017-resolution-from-absence-over-exposed-prs.md)'s comparability and
resolution properties.

Phase 2 injects `checklist.md` into the **coding agent** instead. `checklist.md` is deliberately
built in phase 1, under 40 lines, even though consumption is out of scope this round: a 400-line
profile handed to an agent will not be read, while a 40-line checklist can be injected wholesale.

`AtCloseExtractionSeam.Combine` is kept and re-pointed rather than re-derived. Its
`Wrote > Failed > Declined` precedence, and the rule that one pass's write still commits when the
other fails, apply unchanged to the new pass.

Three operational facts learned by the deleted consumer are carried into phase 2 rather than
rediscovered:

* **Read root and render root differ in pooled S2S mode.** The path read *through* is the leased
  slot's store root; the path *named in the text* must be the one the agent's own tools resolve. A
  host path is one the agent can never open.
* **The path must reach every sub-agent brief.** A sub-agent sees only what it is handed, so
  without this it works blind to the record's contents. Given that prompt-delivered capability on
  this daemon is used at approximately zero rate, this must be wired in code rather than requested
  of the parent.
* **A size refusal is not an absent record.** Both leave the caller holding no text. Log which one
  happened, or a record that silently stopped being injected reads exactly like an author who never
  had one.

## Consequences

The measurement in [ADR 0017](0017-resolution-from-absence-over-exposed-prs.md) stays sound. Counts
remain comparable across developers because every
developer's reviewer saw the same instructions, and `Resolved` remains reachable because nothing is
searching for the absence it is meant to establish. This is the decision that makes the numbers mean
anything.

The reviewer loses a capability it never exercised. There is a real argument for a reviewer that
knows a codebase's recurring weaknesses — that argument is not refuted here, it is separated. If it
is wanted, it should be built from repository-level or team-level patterns, which do not create a
per-author feedback loop, rather than from an individual's file.

Value is deferred. Phase 1 produces records that nothing consumes, and the feature delivers nothing
observable until phase 2 lands. That is accepted deliberately: enabling consumption against an
unvalidated record under a named person's file is the more expensive mistake, and retracting a wrong
record from a shared repository costs far more than delaying the rollout.

The three salvaged lessons are recorded in a tracked follow-up ticket rather than only in this ADR,
because the code that encodes them is being deleted and a lesson that lives only in deleted code is
a lesson about to be relearned.
