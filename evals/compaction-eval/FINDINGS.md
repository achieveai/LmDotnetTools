# Compaction strategy eval — findings

Ten rounds, 2026-09-15 to 2026-09-17. Model `gpt-5.6-terra` via Copilot. Spend $747.93 over 20
completed sweeps. Decision recorded in [ADR 0020](../../docs/adrs/0020-compaction-eval-answered-only-clear-and-summary-on-demand.md).
Rules the numbers were read under: [STANDING-RULES.md](STANDING-RULES.md).

## Outcome

**Ship the mechanism fix: clear only answered tool results, so the summary runs when the window
demands one.** No accuracy benefit was demonstrated for compaction in two rounds at two clamps. No
third round.

Open decisions: the summariser model (luna cheaper on the rate card only; sol better on fidelity at
n=3, confounded); flipping the library defaults (separate production change).

## Hypothesis tree

Verdicts: **HELD** / **NO DIFFERENCE** / **REJECTED** / **OPEN**. Every number carries its `n`.

| H | Claim | Verdict | Deciding number | Opened / closed |
|---|---|---|---|---|
| H1 | Compaction beats no compaction on job outcome | HELD, narrowly | Round 5, r3: summary 1.000, compacting-no-trigger 0.879, control 0.818 (n=3 each) | Only holds when a summary actually runs → H7, H8 |
| H2 | RC1–RC3 checks (dedupe, shell retention, open exchanges) change outcome | NO DIFFERENCE | Round 3b: 27/27 valid, J1 = 1.0 across arms (n=3) | Closed |
| H3 | Summary prompt variants v1–v4 rank | NO DIFFERENCE | Phase 1b: 31 of 33 runs at 1.0000, at ceiling | Closed; the task cannot rank prompts |
| H4 | Summariser model (luna vs sol) | OPEN | Round 5: sol better on checkpoint fidelity, n=3, confounded by cut timing | Needs a guaranteed-trigger comparison (round 10's 64k r3 archives) |
| H5 | Cached-prefix summariser (replay the agent's own prefix) | NO DIFFERENCE | Phase 2 r1: equal score, cost not rankable (n=3) | Closed |
| H6 | Real o200k tokenizer beats the length/4 heuristic | HELD | Cheapest on r1, c2, m1, s1 at equal score (n=3); committed 45fdead9 | Adopted in the recommended config |
| H7 | Clear only answered results (spare the exchange in progress) | HELD | Round 6: cheapest arm, $2.2050/run published, $2.3242 corrected (n=12); committed f29011ec | The shipping default |
| H8 | Measure the gain gate on stored rows, not the cleared view | REJECTED | Round 6: dearest arm, no better than default (n=12) | Closed |
| H9 | Tighter clamp helps r3 and hurts s1 | REJECTED | Round 7: both halves wrong; the manipulation worked, so this is a falsification | Closed |
| H10 | Force the compaction count at a fixed clamp (dose without duration) | OPEN, untested | — | The only remaining way to test a dose effect |
| H11 | Proactive clearing suppresses the summary and that costs accuracy (96k, 13 seeds) | MECHANISM HELD, accuracy NO DIFFERENCE | 9/13 vs 0/13 compacted, Fisher p = 0.000458; s1 +0.44 SE, r3 +0.88 SE (n=13) | → H12 |
| H12 | Same at 64k, where the control must compact | NO DIFFERENCE | keep99 0.8741 sd 0.1640 vs default 0.8182 sd 0.2490, pooled sd 0.2108, SE 0.0827, gap +0.68 SE (n=13); control compacted 2.85/run; both void checks passed | Closed. Two rounds, two clamps, two ties |

Shape of the tree: all the depth hangs off H7. The six hypotheses about *what goes into a summary*
(H2–H5, the prompt variants) all stopped at depth one — the tasks cannot distinguish them.

## Round table

| Round | Question | Runs | Result |
|---|---|---|---|
| 0/1/1b | Baseline, prompts v1–v4, checks | 95 | $122.46. At ceiling; cannot rank prompts |
| 3 | Do compacting arms summarise? | 33 | 9 of 10 arms produced no summary (defect found) |
| 3b | sum-* arms with clearing off | 27 | 1 summary each, J1 = 1.0 |
| 4 | 128k arms | — | Dropped: no pressure |
| 5 | Summary vs no summary on r3 | 30 | 1.000 / 0.879 / 0.818 (n=3) |
| 6 | Four clearing arms | 48 delivered of 51 attempted | H7 cheapest, H8 dearest (n=12). Cost order not rankable |
| 7 | Clamp as dose | — | H9 falsified |
| 8 | Two identical arms | 10 | Eval noise floor: pooled sd 0.1235 on s1 |
| 9 | H11 at 13 seeds | 52 | Mechanism p = 0.000458; accuracy tie |
| 10 | H12 at 64k | 26 | Tie, +0.68 SE; both void checks pass |

## What was fixed in code along the way

* `45fdead9` host-supplied o200k tokenizer for the request estimate (H6).
* `f29011ec` `ClearAnsweredToolResultsOnly` and `MeasureCompactionGainOnStoredRows` options, both
  default false, with a paired non-vacuity test.
* `173c2b68` the eval steer releases on a conversation event, not a clock. Before it the correction
  landed 1–60 Read calls into the work, mis-firing preferentially for slow arms. Rows either side of
  it are not comparable on r2, r3, s1 (SR8).
* `1fdeb1eb` `UsageReader.Rollup` keeps the most-resolved copy of a relayed attempt. First-wins made
  the spend a lower bound ($708.43 for $747.93) and depended on directory enumeration order. Tokens
  and scores were untouched: both copies of an attempt carry identical `TotalTokens` in all 4,348 cases.

## Spend

$747.93 over 20 completed sweeps, every dollar tokens × public price card. No vendor priced any run
(`ProviderReportedCostMicros` null in 23,248 of 23,248 records). The correction factor from the
dedupe fix is not constant (1.00–1.19 across arms), which is why no cost ordering survived it.

## Methodology learnings

The full list is [STANDING-RULES.md](STANDING-RULES.md), 29 rules, each named for the round that
produced it. The ones that decided a result:

* **A metric that cannot vary in the control arm is not a metric** (SR2). Round 9's pre-registered
  `compactions == 0` fired on 13 of 13 control runs. Compute the metric on data in hand before
  pre-registering it.
* **Never filter on a post-treatment variable** (SR3). J0 marks a run invalid for too few
  checkpoints; trigger suppression is the effect under test, so in the control arm "invalid" is the
  treatment working.
* **Cost cannot rank at n ≤ 12** (SR1, SR11). Cost is turn count wearing a price tag. Every s1 ranking
  in rounds 4, 6 and 7 was underpowered by about 4×.
* **The SE comes from this round's own pooled sample sd** (SR6). Round 8's two identical arms are the
  noise floor everything is read against.
* **A reading rule must be able to express every outcome it could meet** (SR10). Round 10's first
  version sent a strong result in the opposite direction into "inconclusive".
* **A written rule does not bind; a rule the tool prints at the moment of computing might.** Four
  rules were written down and still failed. Every analysis script now prints the rules before any number.
* **When a field travels with a coverage count, read the coverage count** (SR26). `CostMicros` shipped
  beside `RecordsWithCost` with a doc comment saying so; nobody read it for hours.
* **A pre-registration is code; unrun code is a claim** (SR9, SR10). Run the read-out on data in hand,
  and ask what the broken version would have printed.

## What this eval cannot say

* Anything about prompt quality: the tasks sit at ceiling.
* Which summariser is better: n=3, confounded.
* Whether compaction helps accuracy on any task here: bounded near zero at n=13, not shown either way.
* Anything from the cost column beyond "H7 was cheapest in round 6 under both accounting rules".
