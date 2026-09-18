# ADR 0020: Compaction clears only answered tool results, and the eval ranks by outcome, never by cost at small n

* Status: Accepted
* Date: 2026-09-17
* Related issues, PRs, or commits: PR #774 (production compaction, merged 2026-09-16); design
  `docs/superpowers/specs/2026-09-15-compaction-strategy-eval-design.md`; findings
  `evals/compaction-eval/FINDINGS.md`; rules `evals/compaction-eval/STANDING-RULES.md`; commits
  `45fdead9` (H6 tokenizer), `f29011ec` (H7/H8 options), `173c2b68` (steer release), `1fdeb1eb`
  (usage dedupe)

## Context

PR #774 shipped compaction with a fit ladder: a pre-emptive clear that replaces older tool results
with placeholders (`ClearToolResultsKeepTurns`, default 3), and a summary cut gated on the tokens it
would free (`MinCompactionGainRatio`). Nothing had measured whether those pieces help a real job.

A ten-round eval (`evals/compaction-eval`, budget $2000, spend $747.93) ran `gpt-5.6-terra` through
Copilot on eight tasks with hidden checkers, 3 to 13 seeds per arm, against a no-compaction control.
It found one defect and one non-result, and it produced a set of measurement rules the harness now
enforces.

**The defect (rounds 3, 5, 9).** The clear and the cut interact. Once the clear has replaced the
head of the conversation with placeholders, a summary over the same rows frees almost nothing, so the
gain gate refuses it. In round 3 nine of ten compacting arms produced no summary at all. On a task
whose first-wave pages are deleted after reading (`r3`), the summary is the only thing that can carry
them: with a summary 1.000, without one 0.879, control 0.818 (round 5, n=3). The mechanism was
confirmed at n=13: an arm that clears only when forced compacted in 9 of 13 runs, the default in 0 of
13 (Fisher p = 0.000458).

**The non-result (rounds 9 and 10).** Suppressing the summary did not measurably hurt accuracy. At a
96k clamp the arms tied (+0.44 SE on `s1`, +0.88 SE on `r3`, n=13). At 64k, where the control now
compacted 2.85 times per run, they tied again (0.8741 vs 0.8182, +0.68 SE, pooled sd 0.2108, n=13).
Two rounds, two clamps, two ties, with pre-registered void conditions that both passed.

**Two candidate fixes were built and compared (round 6, n=12).** H7 scopes the clear to results the
model has already answered on, so the exchange in progress stays whole (`ClearAnsweredToolResultsOnly`).
H8 measures the gain gate on the stored rows rather than the cleared view
(`MeasureCompactionGainOnStoredRows`). H7 was the cheapest arm under both accounting rules
($2.2050 per run published, $2.3242 after the cost fix). H8 was the dearest and no more accurate.

**What the cost column can and cannot do.** Cost correlates 0.87 with turn count, and turn count
ranges 18 to 93 within a single arm, so a cost ordering at three seeds is a turn-length ordering. The
one published cost ordering at n=12 reversed under a 6% accounting correction. No vendor priced any
run: across 627 archives and 23,248 usage records `ProviderReportedCostMicros` is null in every one,
so every dollar is tokens times the public price card.

**Alternatives considered.** A third round to chase the accuracy benefit: rejected in advance and
held to; the SE at n=13 already bounds the effect below anything a product decision would turn on.
Switching the summariser to `gpt-5.6-luna`: it is cheaper on the rate card only, and the one
fidelity comparison (round 5, n=3) favoured `sol` and was confounded by when the cut fell. Rejecting
compaction outright: round 5 shows a summary carrying pages nothing else can.

## Decision

1. **The pre-emptive clear only touches answered tool results.** Hosts that enable compaction set
   `Compaction:ClearAnsweredToolResultsOnly=true` with `Compaction:TextTokenizer=o200k`. The library
   defaults stay `false`/heuristic in this PR; flipping them is a separate production change so the
   behaviour change for every host gets its own review. `MeasureCompactionGainOnStoredRows` stays
   `false` and is not recommended.

2. **The summariser stays `gpt-5.6-sol` until a fidelity comparison with a guaranteed trigger exists.**
   Round 10's 64k `r3` archives are that data; the comparison has not been run.

3. **The eval reports outcome, then cost, and never ranks arms by cost.** What enforces this in the
   repository is `CellSummary.Of`, which groups runs into one row per (variant, task) and orders them
   by variant name then task name ordinally — never by a measured value — so the table cannot become a
   ranking by being sorted. `CellSummary.BuildTable` prints `valid/runs` and `judged` beside every
   mean, appends `(n priced)` to a cost whose denominator is smaller than the cell, and prints `n/a`
   rather than `0` for a cell where nothing was measured. `--extract-only <sweepDir>` regenerates that
   table from an archived sweep, which is the entry point a fresh checkout can run to see it.

4. **The rules preamble was programme practice, not a shipped control.** Each round's read-out was
   written before its data arrived and run against data already in hand, and the analysis scripts
   printed `STANDING-RULES.md` before any number. Those scripts were scratchpad tooling for this
   programme; they are not in this repository and nothing here enforces the preamble. What ships is
   the rules themselves, committed as `evals/compaction-eval/STANDING-RULES.md`, and the findings that
   cite them. A future eval that wants the discipline enforced rather than remembered has to build
   that; this ADR does not claim it exists.

5. **`UsageReader.Rollup` keeps the most-resolved copy of a relayed attempt, not the first read.** A
   sub-agent's bag holds the copy written before the pricing resolver ran; the root bag holds the
   priced one. First-wins made the programme's spend a lower bound ($708.43 for $747.93) that depended
   on directory enumeration order.

## Consequences

* A host on the recommended configuration keeps the exchange in progress whole, so the summary runs
  when the window demands one and the first-wave pages survive. It is also the cheapest configuration
  measured.
* No accuracy claim is made for compaction on the tasks measured. The claim is narrower: the
  mechanism that lost summaries is fixed, at no measured cost.
* Cost figures in this repository's eval output carry their coverage beside them, and the two kinds of
  incompleteness are kept apart because they license different readings. A cell's mean covers
  `RunsWithCost` of its `ValidRuns` and prints `(n priced)` when those differ: the rest are
  unmeasured, not zero, so the figure bounds the cell in neither direction. Separately, a run whose
  own `RecordsWithCost` is under its `Records` was priced only in part, so its figure is genuinely
  under the truth; a mean containing one is printed `>=`. Treating an excluded run as a floor would
  have produced a bound pointing the wrong way.
* Follow-ups, tracked outside this record: flip the two library defaults after a host-side review;
  run the summariser fidelity comparison on the round 10 archives; H10 (force the compaction count at
  a fixed clamp) is the one untested branch that can still test a dose effect.
* The standing rules are process rules learned by breaking them. They are committed so the next eval
  inherits them rather than re-learning them at the same price.
