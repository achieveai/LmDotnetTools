# ADR 0017: Infer resolution from absence over exposed PRs, and guard it against cohort drift

* Status: Accepted
* Date: 2026-08-10
* Related issues, PRs, or commits: epic #526 item 14 (issue #47); commit `448dfaa0` (#539, #560);
  ported under issue #553 from PR #451

> **Status note (2026-08-28, on porting into `main` under issue #553).** Accepted but not yet
> implemented — though the split between what exists and what does not runs through the middle of
> this record, so it is worth stating precisely.
>
> **The exposure seam the Decision names is live on `main` and already exercised.**
> `ReviewSubAgentNode` carries both fields the Decision keys on — `Status` and `Template` —
> at `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs:39` (`:44`, `:46`), and the
> `ReviewSubAgentStatus` enum (`:22-29`) includes the `Completed` value the Decision reads exposure
> from. The settled roster is reachable at review time: `ReviewSubAgentTreeSnapshot` (`:128`), its
> `AllSettled`/`IsSettled` predicates (`:371`, `:379`), and
> `DaemonReviewStageExecutor.AwaitSubAgentSettlementAsync`, which by contract returns the settled
> roster itself rather than only its rendered inventory. An implementation should build on that seam,
> not re-derive one.
>
> **What is absent is everything that would consume it as exposure — the arithmetic.**
> `ResolutionConfidence`, `ExposureStalenessDays` and `CohortDropThreshold` do not exist on `main`,
> and no smoothing, resolution-probability or cohort-guard code does either: `laplace`, `smooth`,
> `cohort` and `unjudgeable` have zero `.cs` hits repository-wide. Nor does the `DeveloperLearnings`
> subtree that would hold the per-pattern history these rates are computed over (epic #526 item 14,
> issue #47).
>
> **Two citations were corrected on the way in, because as authored neither resolved from `main`.**
> The reconciler commit in the Decision was cited by its sha on
> `daemon/review-reliability-and-pr-coverage`, reachable only from that branch (PR #451); it now
> reads `448dfaa0`, the sha the same work carries on `main` (#539, #560). The record's sole "Related"
> reference was `scratchPad/developer-learnings-spec.md` §6, §8 — a local working file that was never
> committed to any branch, `scratchPad/*` being git-ignored, so its §-references cannot be followed
> by anyone; it is replaced above by references that resolve.
>
> Originally numbered ADR 0014 on `daemon/review-reliability-and-pr-coverage`; renumbered to 0017
> because `main` had already allocated 0014.

## Context

DeveloperLearnings claims to answer "is this developer improving". That claim is the whole value of
the feature and it is also the easiest thing in it to get wrong, because **repair is never
observed.** The only observable is absence: the pattern stopped appearing. Every mechanism below
exists because absence has several causes and only one of them is improvement.

Four ways a naive implementation produces a confident wrong answer.

**Calendar time.** A pattern that has not appeared in 60 days looks resolved. If the developer wrote
no code in that window, or wrote code that the relevant specialist never examined, nothing has been
observed at all. Time passing is not evidence.

**All PRs as the denominator.** A pattern can only recur where its dimension was exercised. Counting
PRs where the exception-handling specialist never ran dilutes the rate with observations that were
never made.

**Per-finding counting.** Five instances of the same mistake in one PR is one occurrence of a
habit. Counting five lets a per-PR probability exceed 1.0, at which point every downstream
calculation is meaningless rather than merely imprecise.

**Unsmoothed rates.** One occurrence in one exposed PR gives `p = 1.0`, and `(1 - p)^n` is then zero
for any `n ≥ 1` — so the very first clean PR declares the pattern resolved at total confidence. The
most confident verdict in the system would be the one backed by the least data.

There is a fifth failure that no per-developer calculation can see. Three things make a pattern stop
appearing: the developer improved, they stopped writing that kind of code, or **the reviewer stopped
catching it.** The third is systemic. Change the review model — which this repository is actively
doing — and findings drop across the whole population, and every developer appears to improve
overnight. Nothing in a single developer's history can distinguish that from real progress.

## Decision

**Every rate, window and streak in this system is measured in exposed PRs — never in calendar days
and never in all PRs.** Exposure is the set of specialist templates that ran and reached `Completed`
on a PR, taken from the settled roster (`ReviewSubAgentNode.Template` plus `Status`). It is
code-derived, consistent with [ADR 0015](0015-model-classifies-daemon-counts.md).

**Hits are deduplicated by `patternId` within a PR.** One occurrence per pattern per PR, mandatory.

**Only findings that survived into the shipped review count.** Using the reconciler from
`448dfaa0`: `kept`, `severity-changed`, `reframed` and `merged-into` count; `dropped` does not. A
finding the lead reviewer threw out is not evidence of a developer mistake.

**Rates are Laplace-smoothed**, over the window from a pattern's first hit to its last hit
inclusive:

```
occurrences   = PRs with a hit for this pattern
exposedInWin  = PRs in that window where this pattern's dimension ran
p             = (occurrences + 1) / (exposedInWin + 2)
```

**Resolution is a derived threshold, never a flat constant.** With `n` clean exposed PRs since the
last hit, `P(luck) = (1 - p)^n`, and the pattern resolves when `P(luck) < 1 - ResolutionConfidence`
(default 0.95, so 0.05). A rate of 0.50 resolves after 5 clean exposed PRs; 0.25 after 11–12; 0.10
after about 29. Rare patterns take longer to declare dead, which is correct and is the whole point
of deriving the threshold.

**States are Active, Watch, Resolved and Unjudgeable.** `Regressed` is a flag on an Active pattern,
not a state. `Unjudgeable` covers fewer than 3 exposed PRs since the last hit, or no exposure within
`ExposureStalenessDays` (default 90) — and it is rendered as its own bucket, never folded into
progress. A developer who stopped writing that kind of code has not improved. Keeping the
honest-unknown visible is what stops the progress view becoming fiction.

**A cohort guard covers the systemic case.** Compute, per dimension, the finding rate across all
developers per 10-PR window. If the current window is more than `CohortDropThreshold` (default 40%)
below the trailing median, resolutions in that window are marked `provisional`, the reason is
rendered verbatim, and they are excluded from the headline resolved count until a later
non-suppressed window confirms them. The guard **does not block** resolution — a block could stall
forever — it qualifies it.

**Trend is rendered only when it can be.** Rate over the last 10 exposed PRs against the prior 10,
printed only when both windows have at least 5 exposed PRs; otherwise `insufficient data`.

## Consequences

The system can say "I do not know", and says it in its own bucket. That is the main thing this
decision buys, and it is the reason the output can be trusted at all: a progress view that never
reports ignorance is reporting something other than progress.

Resolution is slow for rare patterns, and users will read that as the system being reluctant. The
worked table exists in the spec so that slowness is legible as arithmetic rather than as a bug.

The cohort guard requires a population-level computation that no single developer's record contains,
so the renderer needs access to cross-developer aggregates within one store. Profiles aggregate
across repositories within a store and are not aggregated across stores, so "the cohort" means the
store's population — which must be stated in the rendered provenance, because a rate whose corpus
is unnamed cannot be checked.

The regression rate is this system's own quality check. A high rate of patterns returning after
resolution means `ResolutionConfidence` is set too loose. That check only works because nothing is
ever moved or deleted ([ADR 0016](0016-developer-learnings-append-only-ledger.md)); it is the
concrete payoff of that decision.

Thresholds are configuration, not constants, and every rendered view states the thresholds in force.
Changing one silently would invalidate comparisons across the history it is applied to.
