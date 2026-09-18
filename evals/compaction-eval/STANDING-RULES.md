# Standing rules of the compaction eval

Twenty-nine rules, each learned by breaking it, each naming the round or board version that produced
it. They were written during the ten-round programme recorded in [FINDINGS.md](FINDINGS.md) and
decided in [ADR 0020](../../docs/adrs/0020-compaction-eval-answered-only-clear-and-summary-on-demand.md).

Why they are committed as a file and not folded into prose: four of them were written down in the
programme's ledger and still failed to bind. The control that worked was making every analysis
script print this list before it printed a number. The next eval should do the same.

The rules were numbered `R1`..`R29` until 2026-09-17 and are now `SR1`..`SR29`, same numbers, same
order. The old prefix collided with the eval's own task ids (`r1`, `r2`, `r3` are tasks). Artifacts
written before that date use the old prefix.

The prose is first person and names the two agents that ran the programme (the lead and the board
keeper). It is kept as written: each rule records a specific failure, and rewriting the failure into
the passive voice would lose who could have caught it and why they did not.

## SR1 - Do not rank arms by the cost column at n=3. (round 5)
Cost correlates 0.87 with turn count, and turn count ranges 18–93 *within a single arm*. A cost
ordering at three seeds is a turn-length ordering wearing a price tag.

## SR2 - A metric that cannot vary in the control arm is not a metric. (round 9)
Before pre-registering one, compute what it reports on data already in hand. `compactions == 0` fired
on 13 of 13 control runs, including runs scoring 1.000.

## SR3 - Never filter on a post-treatment variable. (round 10)
J0 marks a run invalid for producing too few checkpoints, and trigger suppression is the effect under
test — so in the control arm "invalid" IS the treatment working. Report it, never condition on it.

## SR4 - Name the set beside any number that is a sum over one. (rounds 6 and "phase 0/1/1b")
Both known scope errors are on spend accounting. "39 runs, ≈$122" paired phase 1b's run count with
phase 0+1+1b's cost, and survived because phase 1b is *exactly* 39 runs.

## SR5 - An arm's score and that arm's margin over a control are different measurements. (round 9)
Only one of them replicated. Say which you mean, every time.

## SR6 - The SE comes from this round's own pooled sample sd (n-1), never an inherited one. (round 8)
Two identical arms measured the eval's own noise at pooled sd 0.1235 on s1.

## SR7 - A pooled correlation across arms is not a within-arm effect. (round 9)
The -0.305 over 79 mixed runs is an artifact; within fixed configurations it is -0.11 / +0.04 / -0.06.

## SR8 - Rows either side of the steer fix (173c2b68, 2026-09-17T03:01:36Z) are not comparable on
r2, r3 or s1. Before it the correction fired on a clock, landing 1–60 Read calls in, preferentially
mis-firing for the arms that run slowly.

## SR9 - A passing probe is not a distinguishing probe. (round 10)
Ask what the *broken* version would have printed. If the answer is "the same thing", the probe proved
only that the path does not crash.

## SR10 - A reading rule must be able to express every outcome it could meet. (round 10)
Including the direction you are not arguing for. A branch you cannot reach is a finding you cannot
report.

## SR11 - n=3 on s1 gives SE ≈ 0.10, so a 0.14 gap is 1.4 SE. (rounds 4, 6, 7, 8)
Every s1 ranking in rounds 4, 6 and 7 was underpowered by roughly 4×, and never for a budget reason.

## SR12 - Workspace names carry no sweep id and the runner refuses a non-empty directory.
Repeating an arm across sweeps silently aborts those cells. New sweep, new arm names.

## SR13 - A rule against an inference must be enforced where the DATA IS DISPLAYED, not only where the
rule is written. (board v56)
The board had been publishing the eleven r1 arms *sorted by cost per run at n=3* since before its
current keeper took over - two sections above the learning that states SR1. A sorted list is an
argument. My own forbidden cost table was not an independent lapse; it reconstructed an ordering the
artifact was already inviting. Wherever data is shown in an order a rule forbids, the caveat goes on
that display, not in a learnings section elsewhere.

## SR14 - A withdrawal must name WHERE the claim was published, not only what was wrong with it.
(board v56)
The holder of each artifact can then check whether it propagated. My recomputation found the claim;
only their grep found where the same defect had already landed on the board. Neither half finds it
alone. Corollary, learned the hard way: state where you VERIFIED the wrong text, because a claim about
a location is as checkable as a claim about a quantity and gets checked less.

## SR15 - Send the correction and its evidence in one message. (board v57)
Three crossed messages in a row each cost a round trip. A flag followed by the reasoning invites the
other side to act on the flag before the evidence arrives.

## SR16 - Do not DERIVE by subtraction what you can MEASURE by addition. (board v60)
The probe cost was published as $3.88 from `708.43 - 600.42 - 104.13`, three already-rounded totals
whose errors stacked over the $3.875 line. Measured directly: $1.698155 + $2.170935 = $3.869090, so
$3.87. Subtraction inherits every rounding error in its operands, hides which rows contributed, and
breaks silently when a row is missing from one total but not the other. A reconciliation that proves
no row is unaccounted for is a CROSS-CHECK; it is not the source of the number. Two different jobs.

## SR17 - A "rounding" caveat must be VERIFIED as rounding. A truncated input has the same symptom.
(board v62)
"$704.55 + $3.87 = $708.42 against a $708.43 total" was published as an unavoidable rounding artifact.
It was not. The measured rounds figure is $704.556778, which rounds to $704.5**6**, and $704.56 +
$3.87 = $708.43 exactly. The penny came from my own stale tally figure being a digit short, carried in
my messages for hours. Before labelling a gap "rounding", recompute each part from source at full
precision: a real rounding gap survives that, a truncation disappears.
Contrast the $122.46 case, where the gap IS real - $13.77 + $42.63 + $66.05 cannot reach $122.46 once
each part is rounded. Two pennies, one real and one an artifact, must not share one label.

## SR18 - When corrected on one term, measure EVERY term, not just the one named. (board v62)
After SR16 landed, the probe row was measured and the rounds row was left "obtained by difference" -
though it is equally measurable, being the sum of the 19 non-probe sweeps. A correction applied only
where it was pointed leaves the same defect one column over.

## SR19 - Review the RENDERED artifact, not only the patch. (board v63)
A duplicated block survived the diff and was caught on the render: an edit added a superseded note
while the previous one stood, so the page explained the same thing twice in consecutive paragraphs.
A diff shows what changed; only the render shows what the reader meets.

## SR20 - Do not instruct a change to an artifact you cannot see. Report the fact and let its holder
apply it. (board v63)
Told board-keeper2 to "drop that penny line" having proved only that ONE row needed no penny. Their
strip also lists fifteen rounded per-round items which sum to $704.55 against a measured $704.56 - a
genuine rounding gap my instruction would have deleted. The finding ($704.556778 measured) was right;
the instruction reached past it into a layout I had never seen. Third location overreach tonight, after
"on the board" and "drop the penny": send the measurement, not the edit.
Corollary, learned from the other direction: **a report of a page is not the page.** I then flagged a line as false having read board-keeper2's own quoted description of the PRE-FIX state as the current strip. The line had been corrected a version earlier. Whoever wrote the description, it is a snapshot with a timestamp, not the artifact.

**STRENGTHENED to a precondition, after the corollary failed a FIFTH time in one session (round 10
night).** A reminder does not bind here, because unlike every other rule on this list there is no tool
between me and a message that could print it. The precondition is: **do not send a correction about an
artifact you cannot read - send the discrepancy and let its holder check it.** That is exactly the
discipline board-keeper2 has used on me repeatedly, always phrased as a candidate with the check named.
The asymmetry is the evidence: their candidates found a code defect, a mis-generalisation and a scope
error; my corrections found four things that were already right. Reporting a discrepancy costs one
message and can only add information; asserting a correction can subtract it.

**board-keeper2's objection, which sharpens it: their discipline was FORCED, not chosen** - they could
not read my ledger, so a candidate was the only move available. Calling it a method was generous of me.
Their test is whether either of us uses it on an artifact we CAN read, where the correction is right
there and cheap. Tonight answers that in the worst direction: I could read their description, the
correction looked cheap, and I made it wrongly five times - and once used the same cheap certainty to
talk them OUT of a correct finding they had already verified. **So the precondition applies ESPECIALLY
where the correction is cheap, because cheapness is the hazard and not the licence.**

## SR21 - A "superseded" label preserves claims that were BELIEVED AND FALSE. (board-keeper2, v65)
An incomplete check that later turned out right is not that, and keeping it under the label dilutes
what the label means: a record that marks everything superseded tells a reader nothing about which
entries were ever wrong. Drop the incomplete-but-correct working; keep only what a reader would
otherwise still believe.
**Boundary, and carry it with the rule (board-keeper2, v67):** this holds where a record marks
ERRORS, because there the label asserts that someone believed something false. It does NOT hold
where a record marks REVISIONS - there the label never claimed belief, and citing SR21 to prune a
revision history would delete a record that was doing its job.

## SR22 - One label, two quantities has a TWO-PARTY form, and it is the worse one. (eighth instance)
The first seven were one author labelling two things they had both computed - reachable by
recomputation. In the eighth, "that penny line" named the row penny to me and the itemisation penny to
board-keeper2. Neither of us held both meanings, so neither could catch it alone, and recomputation
does not reach it. Note the sentence was not false: it was CORRECT about the row it named to me and
under-specified about which row that was. **An under-specified true statement is still a defect**, and
the worst kind, because nothing in it can be checked. What reaches it is quoting the phrase back with
its referent named.

## SR23 - Right by luck and right by check read identically from outside. (board-keeper2, v66)
Their rounding label turned out correct, so the recomputation "rescued nothing - it converted a guess
into a fact." Record that plainly rather than letting a reader think the check saved a wrong answer.
Only one of the two survives the next case, and the record's job is to say which one this was.

## SR24 - A recomputation from the same source field is not an independent measurement.
Every spend figure in this programme - mine and the board's - reads `cost.costMicros` from the same
`runs.jsonl` rows. Agreement between two routes catches grouping drift, double-counting, arithmetic
and stale tallies, which is most of what has gone wrong tonight. It cannot catch a systematic error in
`costMicros` itself. Say "reconciled against source", never "independently measured".

## SR25 - Never spend an ordinal on a hypothetical, and re-check a positional count whenever it grows.
(board-keeper2, v67)
A near-miss entry read "the only reason it is a near-miss and not an eighth instance". When a real
eighth arrived, the ordinal named two things, one of which never happened. "Not a counted instance"
says the same without spending a number. Any count that names positions - instances, rounds, rules -
must be swept for stale ordinals and stale totals each time it grows.

## SR26 - When a field travels with a coverage count, read the coverage count. (round 10 night)
`RunCost.CostMicros` ships beside `RecordsWithCost` and `Records`, and its own doc comment says a
figure covering only some records "is a lower bound, which is why the two counts travel with it".
I summed `costMicros` for hours, reconciled it four ways, published it, and never once read the two
counts sitting next to it. 81 of 378 runs were short-priced; the programme spend was understated by
$39.51. A caveat the source already states is not a caveat you have honoured. If a value arrives with
a denominator, the denominator is part of the value - print it or do not print the value.

## SR27 - A convenient claim needs its check BEFORE you build on it, not before you publish it. (round 10 night, with board-keeper2)
I sent board-keeper2 "the dropped price is always a summariser call" as a fact. It was wrong, and it
happened to spare their cost rows. They asked for the check - but only after building a blast-radius
argument on it. Scepticism gets spent where a claim threatens what you hold; a claim that RELIEVES
you of work passes cheaply. "I flagged it as an inference" is what I said when sending it, and it
stopped neither of us. The check is cheap. The argument built on it is what costs to unwind.

## SR28 - A denominator equal to its numerator hides what was dropped. (round 10 night, board-keeper2)
"48 of 48 scored, $114.22" appeared on both artifacts. I diagnosed it as a scope error - a run count
from one set beside dollars from another - and I was wrong. Checked: 51 rows, 48 Completed, 48 scored,
3 HarnessError at $0.0000, and $114.2180 covers exactly the 48 delivered. **Numerator and dollars
always matched. The defect is the DENOMINATOR:** "of 48" makes a complete-looking fraction out of a
set that attempted 51 and lost 3, and the repair sweep that replaced them is invisible in it. An
N-of-N reads as "nothing was lost" precisely when something was. Write attempted, delivered and
scored, or write none of them.
Corollary (board-keeper2): **a correction can install a plausible wrong account of a real defect** -
a second error entering through the door the first one opened. Re-derive before you re-diagnose.

## SR29 - A false caveat on a true number is quieter than a retraction, so guard it harder. (board-keeper2, v75)
My message that reversed a correct finding also asked whether the 0.972 should be marked. The
reversal was loud and was undone within the hour. Had they marked the score on the same authority, a
TRUE number would carry a FALSE caveat, nothing would be left to catch it, and **a marked figure looks
handled** - it invites no further checking. Ranked by how long the damage survives, the near miss was
worse than the error that actually landed.
