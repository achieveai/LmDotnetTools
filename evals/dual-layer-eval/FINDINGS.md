# Dual-layer eval: findings

Goal: find the prompts and flow that let a dual-layer pair (Opus plans, Luna executes) score
close to Opus alone at a fraction of its cost, on tasks that load lots of useless context.

Arms: `claude-opus-5.5` alone, `gpt-6-luna` alone, `opuna` (Opus 5.5 planner + GPT-6 Luna executor).
Score: J1 answer score from `tools/Score-Answers.ps1`. Cost: the runner's cache-aware cost from
each run's persisted usage records, priced from `samples/LmStreaming.Sample/appsettings.json`.

**Result so far:** the best flow (reference context off, "executor reads, planner judges" records,
empty-reply nudge) is now the built-in default. On the must-read task it matches Opus quality at
about 0.7x Opus cost at both effort levels tested (default: 0.86 vs 0.84 at 0.74x; medium: 1.0 vs
1.0 at 0.70x). On tasks a shell can narrow it saves nothing. See [Conclusion](#conclusion).

## Round 0: pilot on public sets (1 seed)

Sweep: `results/20260924-090015-641225f7/`.

| Task | Opus alone | Luna alone | Opuna | Opuna / Opus cost |
|---|---|---|---|---|
| mq1 (MuSiQue) | 0.875, $0.208 | 0.625, $0.028 | 0.875, $0.144 | 0.69 |
| hq1 (HotpotQA) | 0.9, $0.058 | 0.9, $0.006 | 0.9, $0.040 | 0.69 |
| lb1 (LongBench v2) | 0.625, $0.024 | 0.5, $0.015 | 0.75, $0.026 | 1.07 |
| ag1 (synthetic ledgers) | 1.0, $0.112 | 1.0, $0.003 | 1.0, $0.122 | 1.09 |

Tool calls, planner + executor (the sweep's own counts missed the executor; see finding 3):

| Task | Opus | Luna | Opuna |
|---|---|---|---|
| mq1 | 15 | 86 | 7 + 7 |
| hq1 | 4 | 33 | 2 + 10 |
| lb1 | 1 | 30 | 1 + 1 |
| ag1 | 5 | 8 | 5 + 8 |

### Finding 1: the public sets are memory-contaminated

- Opus answered lb1 with ONE tool call (writing `answers.json`) and read no file. It scored
  0.625, where chance is 0.25.
- Opus answered hq1 after 4 calls and Opuna after 2 planner calls; both scored 0.9.
- So these tasks measure recall, not reading. A cheap executor cannot help a planner that never
  delegates.
- Decision: retire mq1, hq1 and lb1 from scoring. Build synthetic tasks whose answers exist only in
  the workspace (fictional org, fictional codebase, rules buried in memos).
- Later revised for mq1 only (see finding 6): it is back for the Opuna-vs-Opus comparison.

### Finding 2: ag1 is at ceiling

- All three arms scored 1.0, and Luna alone did it for $0.003.
- It cannot separate arms. Retired from scoring; ag2 replaces it with the counting rules buried in
  about 150 memos.

### Finding 3: the sweep dropped executor tool calls (fixed)

- The executor thread has no `sample.subAgentOf` link, so the runner listed it under
  "Unattributed threads" and left its tool calls out of each run.
- Cost was NOT affected: the planner's ledger already holds the executor's usage records under the
  same attempt ids, and the rollup dedupes by attempt id.
- Fix: `ConversationStoreReader.PairedExecutorParent` links `executor-{id}` to `{id}`.
  Test `GroupByRootThread_PutsADualLayerExecutor_InItsPlannersRun`; reverting the fix fails it.
- Re-extracted the pilot with `--extract-only`: Opuna tool calls became 14 / 12 / 2 / 13
  (mq1 / hq1 / lb1 / ag1), every cost stayed identical to the micro-dollar, and no thread is
  unattributed. The pre-fix files are kept as `*.before-executor-fix.*`.

### Finding 4: where Opuna saves money today

- Only on the task where Opus read a lot (mq1: 15 calls). Opuna cost 0.69x there at equal score.
- Where Opus reads little, Opuna costs slightly more: the planner pays its own prompt plus the
  delegation round trip.
- So the saving scales with how much reading moves to the executor. The prompt search targets
  exactly that: delegate whole sub-questions, get compact answers back.

### Finding 5: what the planner's dollars buy

Opus-side cost split (Opus 5.5: cache write $5/M, cache read $0.20/M, output $20/M):

| Run | Cache write | Output | Cache read |
|---|---|---|---|
| mq1 Opus alone | $0.091 (18.2k) | $0.077 (3.9k) | $0.040 (200k) |
| mq1 Opuna planner | $0.070 (14.0k) | $0.055 (2.8k) | $0.015 (74k) |
| lb1 either (one call) | $0.016 (3.3k) | $0.005 | $0.003 |

- Every token that enters the planner's context is paid once as a cache write. After that,
  re-reading it each turn is cheap.
- About 3.3k of the writes are fixed: system prompt plus tools.
- So the two levers are: fewer tokens reaching the planner (short executor reports), and less
  planner output (rationales and answers are output at $20/M).
- More planner turns cost little on their own (cache reads). Fewer, bigger delegations help mostly
  because each report replaces raw file text.

## Round 1: calibration on synthetic tasks (1 seed)

Sweep: `results/20260924-094432-6e9c5c52/`. Spend $1.06.

| Task | Opus alone | Luna alone | Opuna | Opuna / Opus cost |
|---|---|---|---|---|
| fw1 (fictional wiki, 592k tokens) | 1.0, $0.251 | 1.0, $0.025 | 1.0, $0.274 | 1.09 |
| ag2 (ledgers + 150 memos) | 1.0, $0.162 | 1.0, $0.018 | 1.0, $0.098 | 0.61 |
| cb1 (fictional codebase) | 1.0, $0.138 | 1.0, $0.007 | 0.75, $0.083 | 0.60 |

### Finding 6: with a shell, "lots of useless context" is not hard

- Every arm used Bash and grep. The corpus never entered anyone's context: the models filtered it
  first. Luna alone got a perfect score on all three tasks at 1/10 to 1/20 of Opus's cost.
- For tasks like these the honest answer is: use the cheap model alone. The dual layer can only
  earn its cost on tasks the cheap model gets wrong.
- So the benchmark needs tasks where grep can't find the answer and judgment decides it: indirect
  references, near-duplicates, superseded or non-authoritative sources. fw2 is being built to that
  brief.
- mq1 is restored for the Opuna-vs-Opus comparison: both arms plan with Opus, so Opus's memory helps
  both. It is still the only task where Luna trailed (0.625 vs 0.875).

### Finding 7: on fw1 the planner micromanaged the executor

- The Opuna planner took 15 turns (Opus alone took 18) and wrote 22.8k tokens to its cache (Opus
  alone 22.4k). The executor's reports carried as much text as Opus's own grep output.
- So delegating one keystroke at a time saves nothing and adds the executor's cost on top.
- Where the planner delegated in fewer, bigger steps (ag2, cb1), its cache writes fell from about
  17k to about 6k tokens, and cost fell to 0.6x.
- This is what the prompt variants in round 2 target.

## Round 2: Opuna prompt variants on fw1, ag2, cb1 (1 seed)

Sweep: `results/20260924-095305-60a1e608/`. Spend $2.23. Base Opuna is from round 1.

| Variant | fw1 | ag2 | cb1 | Mean score | Total cost |
|---|---|---|---|---|---|
| base (round 1) | 1.0, $0.274 | 1.0, $0.098 | 0.75, $0.083 | 0.92 | $0.455 |
| briefs | 0.6, $0.071 | 1.0, $0.169 | 1.0, $0.205 | 0.87 | $0.445 |
| shape | 0.9, $0.251 | 1.0, $0.214 | 0.875, $0.074 | 0.93 | $0.539 |
| briefs-shape | 0.2, $0.092 | 1.0, $0.090 | 0.25, $0.068 | 0.48 | $0.250 |
| lean-shape | 0.5, $0.308 | 1.0, $0.101 | 0.875, $0.061 | 0.79 | $0.470 |
| briefs-shape-noref | 1.0, $0.159 | 1.0, $0.121 | 1.0, $0.252 | 1.00 | $0.532 |
| Opus alone (round 1) | 1.0, $0.251 | 1.0, $0.162 | 1.0, $0.138 | 1.00 | $0.551 |

### Finding 8: handing whole sub-questions to Luna trades quality for cost

- The three worst scores all came from runs where the planner delegated in 2 to 4 turns: briefs
  on fw1 (0.6, $0.07), briefs-shape on fw1 and cb1 (0.2 and 0.25). Few turns is not enough to fail,
  though: ag2 briefs-shape also took 4 turns and scored 1.0 at $0.09.
- When the planner hands over a sub-question, the executor's judgment replaces Opus's. The
  planner then trusts a confident one-line ANSWER it cannot check.
- Luna ALONE scored 1.0 on fw1. As an executor it did worse. My guess here was that the
  sub-question stripped context the full task gave. Round 3's transcripts disproved that: the
  executor had too MUCH context. It saw the whole task and took it over (finding 11).
- One seed per cell and large swings (the same variant scores 0.2 on one task and 1.0 on another),
  so none of these arms is a winner yet. The pattern across arms is the finding, not any one cell.

### Finding 9: no answer misrouting

- Several wrong fw1 answers equal another question's gold answer. That looked like results matched
  to the wrong call.
- Ruled out: `AgentDelegatedToolExecutor` runs one delegation at a time behind a gate, and
  `AgentTextCollector` returns only that run's final text. fw1's traps are built to produce an
  entity from another hop of the chain, which is exactly this pattern.

## Round 3: separating tasks fw2 and mq1, plus an fw1 probe (1 seed)

Sweeps (raw transcripts, off-repo): `.logs/dual-eval-raw/20260924-102143-cc4f45dc/` (baselines) and
`.logs/dual-eval-raw/20260924-102443-90e590fb/` (Opuna arms). Spend $2.76.

| Arm | fw2 | mq1 | fw1 |
|---|---|---|---|
| Opus alone | 1.0, $0.313 | 0.875, $0.189 | (r1) 1.0, $0.251 |
| Luna alone | 0.2, $0.075 | 0.75, $0.037 | (r1) 1.0, $0.025 |
| Opuna base (reference on) | 0.2, $0.182 | 0.75, $0.107 | |
| Opuna shape (reference on) | 0.7, $0.356 | 0.875, $0.156 | 0.7, $0.158 |
| Opuna briefs-shape (reference on) | 0.8, $0.256 | 0.875, $0.132 | 0.3, $0.097 |
| Opuna briefs-shape-noref | 1.0, $0.485 | 0.875, $0.111 | 0.9, $0.106 |

### Finding 10: fw2 separates the models

- Opus 1.0, Luna alone 0.2. This is the first task where the cheap model clearly fails. The trap
  table in `tasks/README.md` says which misreadings cost which points.

### Finding 11: the reference context lets the executor take over the task (root cause)

- With the reference context on, the executor sees the user's whole task. It then answers the
  task instead of the planner's call.
- fw1 briefs-shape: the planner asked only for the page formats. The executor ran 60 tool calls,
  answered all 10 questions and wrote `/tmp/ans.json`. The planner copied it and told the user it
  had not checked the answers. Score 0.3.
- fw2 base: the planner asked for "a vocabulary search". The executor ran 74 calls and wrote
  `answers.json` itself. The planner submitted it unchecked. Score 0.2, the same as Luna alone.
- So in those runs the pair became Luna with extra steps. The executor prompt already says "never
  start the planner's next step". Luna ignores that once it can see the task.
- With the reference context off, the executor cannot know the task, so it cannot run away:
  fw1 0.9 and 1.0 (reference on: 0.2 and 0.3), fw2 1.0 (reference on: 0.2 to 0.8).
- The unattended mode tells the planner to finish without asking anyone, so it submits even what
  it says it has not checked.

### Finding 12: with the reference context off, fw2 quality costs more than Opus

- briefs-shape-noref matched Opus on fw2 (1.0) but cost 1.55x. The planner took 20 turns and wrote
  41k tokens to its cache; Opus alone took 12 turns and 35k.
- Delegation saves money only when the executor compresses what it reads, and compressing takes
  judgment. On fw2 that judgment is exactly what Luna gets wrong, so the planner re-reads.
- Next idea (round 4): the executor extracts every candidate with file, date, status and a quote,
  and the planner judges. Compact and judgment-free for the executor.

## Round 4: reference off, three prompt sets, 2 seeds

Sweeps (raw, off-repo): `.logs/dual-eval-raw/20260924-105042-745fcd08/` (Opus) and
`.logs/dual-eval-raw/20260924-105310-5199e6c2/` (Opuna). Config `runner-r4.jsonc`. 24 runs, all
Completed. Spend $5.90. Cells are the mean score and mean cost of the 2 seeds.

| Arm | fw2 | mq1 | cb1 | Mean score | Cost vs Opus |
|---|---|---|---|---|---|
| Opus alone | 1.0, $0.397 | 0.75, $0.150 | 1.0, $0.144 | 0.92 | 1.00x |
| Opuna lean-shape-noref | 0.95, $0.327 | 0.875, $0.137 | 0.94, $0.133 | 0.92 | 0.86x |
| Opuna briefs-shape-noref | 1.0, $0.395 | 0.81, $0.169 | 0.94, $0.145 | 0.92 | 1.03x |
| Opuna candidates-noref | 0.75, $0.592 | 0.75, $0.164 | 0.875, $0.203 | 0.79 | 1.39x |

### Finding 13: reference off holds quality; the saving is small and noisy

- All three noref arms fixed the takeover from finding 11. No executor answered the task itself.
- lean-shape-noref is the best arm: Opus-level mean score at 0.86x the cost.
- The saving is inside the noise. Seed-to-seed cost swings 2x within one cell (lean cb1 $0.076 vs
  $0.189; lean fw2 $0.221 vs $0.432). Two seeds cannot separate 0.86x from 1.0x.
- candidates-noref lost. The candidate lists were long, so the planner paid cache writes for them,
  and one fw2 run spent $0.89 re-reading. Judgment-free extraction is not compact enough.
- Why the ceiling is low: Opus alone is already frugal on these tasks. It greps, so the useless
  context never enters its window. A pair can only save what Opus would have read.
- Confound: `planner-briefs.md` and `planner-lean.md` still tell the planner the executor "has seen
  a copy" of the task. In noref runs that is false. The planner may over-trust short briefs.
- Confound removed after round 4: both files now say "Assume the executor does not know the
  user's task", and lean asks for the facts a question needs instead of "don't restate". Rounds
  0–4 ran the old wording.
- Action taken: the host now defaults `DualLayer:ShareReferenceContext` to false, and the built-in
  prompts no longer claim the executor sees the task
  (`DualLayerModelPreset.cs`, `DualLayerPrompts.cs`).
- Next: a must-read task (tr1), where grep cannot skip the reading. That is where a cheap reader
  can pay off.

## Round 5: the must-read task tr1 (2 seeds)

tr1: 157 support tickets (~140k tokens) judged against a six-clause credit policy, then 8 aggregate
questions. A keyword script scores 0/8, so grep cannot skip the reading. Sweeps (raw, off-repo):
`.logs/dual-eval-raw/20260924-114027-d5150d5e/` (baselines) and `.../20260924-114329-1b7bd13f/`
(Opuna). Config `runner-r5.jsonc`. 10 runs, $6.01.

| Arm | Seed 0 | Seed 1 |
|---|---|---|
| Opus alone | 1.0, $0.722 | 0.375, $0.957 |
| Luna alone | 0.5, $0.205 | 0.25, $0.083 |
| Opuna built-in prompts | 0.0, $0.640 (no answer) | 0.0, $0.963 (no answer) |
| Opuna lean-shape | 0.125, $0.554 | 0.625, $0.691 |
| Opuna briefs-shape | 0.0, $0.397 | 0.75, $0.797 |

### Finding 14: tr1 is the task that separates them, and it is hard for Opus too

- Luna alone 0.25–0.5. Opus alone 1.0 once, then 0.375: that run counted 39 of the 48 incidents,
  and every question aggregates over that count.
- Opus reads almost everything: 85–100k tokens written to its cache, $0.72–0.96 a run. This is the
  first task where there is real reading for a cheap model to take over.

### Finding 15: a thinking-only reply ended the run (fixed in the run loop)

- Both built-in-prompt runs ended with a planner reply that was one thinking block, no text and no
  tool call. The loop reads "no tool calls" as done, so neither run wrote `answers.json`.
- The reply was not cut off: 1,169 of 24,576 output tokens, then a normal message stop.
- Anthropic documents this: an empty `end_turn` most often follows a text block added after tool
  results, and the fix is a continuation prompt in a new user message
  (platform.claude.com/docs/en/api/handling-stop-reasons). The host's elapsed-time notice is such a
  text, sent about once a minute. Opus alone finishes tr1 in 3 minutes; the pair takes 8–14.
- Fix: `MultiTurnAgentLoop` now sends a hidden, persisted continuation prompt (`EmptyReplyNudge`)
  when a turn after tool results replies with nothing, at most twice per run. Every client view
  and compaction skip it, as they skip the notice. Regression tests in
  `tests/LmMultiTurn.Tests/EmptyReplyNudgeTests.cs`, mutation-verified.
- This affects any long Anthropic run with the notice on, not only the pair.

### Finding 16: the planner still read the tickets, second-hand

- The executor's reports added up to 400k–724k characters per arm (2 runs): 50–90k tokens per run
  landed in the planner's context. Opus alone read 85–100k. So the pair read the corpus twice.
- Cause: the planner asked for raw text. In built-in seed 0 it had the executor script a grep
  digest of every ticket, then asked for it with "Return the COMPLETE file content VERBATIM — do
  not summarise; I must analyse every line myself." The executor prompts return full output when
  asked, so the planner got the corpus back.
- Next (round 5b): a records arm. The planner names the facts it needs, the executor reads batches
  and returns one line per document with short quotes, and the planner judges
  (`variants/planner-records.md`, `variants/executor-records.md`).

## Round 5b: tr1 with the nudge, plus the records arm (2 seeds)

Sweeps (raw, off-repo): `.logs/dual-eval-raw/20260924-122906-7484f462/` (Opus) and
`.../20260924-123207-fd61fc99/` (Opuna). Config `runner-r5b.jsonc`. 8 runs, $5.15.

| Arm | Seed 0 | Seed 1 |
|---|---|---|
| Opus alone | 1.0, $0.898 | 1.0, $0.769 |
| Opuna built-in prompts | 0.0, $0.659 (no answer) | 0.0, $0.448 (no answer) |
| Opuna lean-shape | 0.625, $0.548 | 0.0, $0.428 (counted 12 of 48) |
| Opuna records | **1.0, $0.722** | **1.0, $0.673** |

Opus over its 4 tr1 seeds (rounds 5 and 5b): mean score 0.84, mean cost $0.837.

### Finding 17: records matches or beats Opus on tr1 at about 0.83x its cost

- Records scored 1.0 on both seeds at a mean of $0.698. Opus averages 0.84 at $0.837.
- The saving is modest because the planner still wrote 56–80k tokens to its cache: the records came
  back in about 31 reports a run, median 3.2k characters but about 200k characters in total per run
  (some reports were long). Shorter records are the next lever.
- Two seeds only. Round 6 checks it on the grep-friendly tasks and adds two tr1 seeds.

### Finding 18: the built-in planner prompt goes silent; the nudge budget was per run

- The nudge fired (2 per run) but both built-in runs still ended silent later: seed 0 went silent at
  turns 13 and 14, worked for four turns, then went silent at turn 19 with no nudges left.
- Built-in prompt runs on tr1: 4 of 4 ended silent. Lean, briefs and records runs: 0 of 10 needed a
  nudge. Something in the built-in planner prompt invites it; the text gives no obvious cause.
- Fix: the budget is now per silent streak (at most 2 in a row, reset by any turn with tool calls).
  Test `A_turn_with_tool_calls_resets_the_budget_so_a_later_silent_stretch_is_recovered_too`,
  mutation-verified. LmMultiTurn 2633/2633.
- Candidate: make the records prompts the built-in ones, if round 6 holds.

## Round 6: records on every task (2 seeds)

Sweep (raw, off-repo): `.logs/dual-eval-raw/20260924-131034-9c4bea49/`. Config `runner-r6.jsonc`.
8 runs, $2.35. No run needed a nudge.

| Task | Records (seed 0, seed 1) | Records mean | Opus alone mean |
|---|---|---|---|
| tr1 | 0.375 $0.496, 1.0 $0.527 | 0.69, $0.51 | 0.84, $0.84 (4 seeds) |
| fw2 | 0.9 $0.381, 0.9 $0.395 | 0.9, $0.39 | 1.0, $0.40 (round 4) |
| mq1 | 0.5 $0.109, 0.875 $0.154 | 0.69, $0.13 | 0.75, $0.15 (round 4) |
| cb1 | 1.0 $0.141, 0.5 $0.145 | 0.75, $0.14 | 1.0, $0.14 (round 4) |

### Finding 19: records reaches Opus quality on the must-read task at 0.72x; elsewhere it saves nothing

- tr1 over all 4 records seeds (rounds 5b and 6): mean 0.84 at $0.605. Opus: 0.84 at $0.837.
- On the grep-friendly tasks the pair costs the same as Opus and scores a little lower (0.78 against
  0.92 over fw2, mq1, cb1). Opus alone is already frugal there.
- tr1 seed 0 lost points to a flow mistake: the planner first asked for "tickets with monitoring
  data", a keyword pre-filter, and so recorded 35 of the 48 incidents. The prompt did not forbid it.
- cb1 seed 1 was judgment noise: the planner traced config layers from verbatim output and got four
  values wrong.
- Action: the records guidance, plus "do not narrow the set with a keyword search", is now in the
  built-in planner and executor prompts (`DualLayerPrompts.cs`). The built-in prompt went silent in
  4 of 4 tr1 runs, and records is the only arm that matched Opus on tr1. Round 7 tests that default
  with no prompt files.

## Round 7: the shipped default (2 seeds)

Sweep (raw, off-repo): `.logs/dual-eval-raw/20260924-133411-66709fa9/`. Config `runner-r7.jsonc`.
Built-in prompts with the records guidance, reference context off, per-streak nudge, no prompt
files. 8 runs, $2.73. No run needed a nudge.

| Task | Opuna default (seed 0, seed 1) | Opuna mean | Opus alone mean | Cost vs Opus |
|---|---|---|---|---|
| tr1 | 0.75 $0.764, 1.0 $0.549 | 0.875, $0.657 | 0.84, $0.837 (4 seeds) | 0.78x |
| fw2 | 1.0 $0.324, 1.0 $0.403 | 1.0, $0.364 | 1.0, $0.397 | 0.92x |
| mq1 | 0.875 $0.152, 0.625 $0.165 | 0.75, $0.159 | 0.75, $0.150 | 1.06x |
| cb1 | 0.75 $0.273, 0.875 $0.101 | 0.81, $0.187 | 1.0, $0.144 | 1.30x |
| All four | | 0.86, $1.37 | 0.90, $1.53 | 0.89x |

### Finding 20: the shipped default holds; the planner's own reading is the cost floor

- tr1 across all six records-guided runs (rounds 5b, 6, 7): mean 0.85 at $0.62, against Opus 0.84 at
  $0.84. That is Opus quality at about 0.74x its cost, on the task where the reading is the work.
- Planner cost is 84–97% of every run. The executor costs $0.005–0.13. So the saving cannot go much
  past what the planner avoids reading.
- What the planner still reads on tr1: the policy in full (it must apply it: about 14k characters),
  and the records. On seed 0 the records came back as prose, about 700 characters per ticket
  (107k characters in all), not one short line. Seed 1 skipped records and had the executor squeeze
  the tickets with a script to 59k characters, which it read verbatim. It scored 1.0 at $0.55.
- Seed 0 also read the policy twice: the executor summarised the first "verbatim" request instead
  of pasting it. Small cost (one round trip), but it is Luna not following the rationale.
- On fw2, mq1 and cb1 Opus alone already greps and reads little, so the pair has nothing to save and
  loses a little quality on the two short tasks. cb1 seed 0 cost 1.9x Opus: 48 executor calls
  tracing config layers.

## Round 8: compact records on tr1 (3 seeds)

Sweep (raw, off-repo): `.logs/dual-eval-raw/20260930-073946-48d894da/`. Config `runner-r8.jsonc`.
Prompt files `variants/planner-compact.md` and `variants/executor-compact.md`: the built-in prompts
plus a record cap (about 40 words, fields only) and "verbatim means in full" for the executor.
3 runs, $1.88.

| Seed | Score | Cost | Planner writes to cache | Report characters |
|---|---|---|---|---|
| 0 | 0.875 | $0.535 | 48.7k | 83k |
| 1 | 0.75 | $0.724 | 74.8k | 116k |
| 2 | 1.0 | $0.616 | 52.4k | 85k |
| Mean | 0.875 | $0.625 | | |

### Finding 21: a record cap shortens each line but not the total

- Each record became one line of about 450 characters, down from about 700 of prose in round 7.
- The planner then asked for about 10 fields per ticket, so the batch replies stayed near 10k
  characters each. Mean cost $0.625 against round 7's $0.657: within noise.
- The executor again summarised a multi-file `cat` the planner had asked for verbatim, so the
  policy was still read twice. The "verbatim means in full" line did not stop it.
- Not adopted. Records for 157 documents cost at least 15–25k planner tokens whatever the cap.

## Round 9: medium reasoning effort on tr1, with Opus as the control (3 seeds)

Sweep (raw, off-repo): `.logs/dual-eval-raw/20260930-080144-5323e286/`. Config `runner-r9.jsonc`.
Every earlier round sent no effort, so the provider's default applied. Round 9 provisions each
conversation with root effort `medium` (new runner field `reasoningEffort`). The host log confirms
"requested effort medium, shaped effort medium". Built-in prompts, no prompt files. 6 runs, $6.66.

| Arm | Scores | Mean cost | Planner output | Planner writes to cache |
|---|---|---|---|---|
| Opus alone, medium | 1.0, 1.0, 1.0 | $1.306 | 12.6–13.8k | 129–142k |
| Opuna, medium | 1.0, 1.0, 1.0 | $0.913 | 12.1–16.0k | 65–104k |
| Opus alone, default (rounds 5, 5b) | 1.0, 0.375, 1.0, 1.0 | $0.837 | 8.4–12.6k | 82–102k |
| Opuna records-guided, default (rounds 5b–8) | mean 0.86 over 9 runs | $0.623 | 7.5–10.0k | 35–84k |

### Finding 22: medium effort buys a perfect tr1 score, and the pair still costs 0.70x

- The default is below medium, not above it. At medium both arms think more and read more. Opus
  alone read the whole corpus (290–326k characters of tool output) and scored 1.0 on every seed.
- At medium the pair also scored 1.0 on every seed, at 0.70x Opus's cost at the same effort.
- At the default effort the pair scores 0.86 at 0.74x Opus's cost (Opus 0.84).
- Read across: the pair at medium ($0.91) costs about what Opus costs at the default ($0.84), and
  scores 1.0 against 0.84. At equal spend the pair buys more accuracy on must-read work.
- So the saving is a property of the flow, not of one effort setting: about 0.7x at each level.
- Three seeds per arm. Pair cost at medium ranged $0.72–1.04, so treat 0.70x as about 0.6–0.8x.

## Conclusion

Best flow found (now the built-in default, no prompt files needed):

1. **Reference context off.** With it on, the executor sees the task and answers it itself, and
   the planner submits unchecked answers (finding 11).
2. **Executor reads, planner judges.** The planner asks for one short record per document, in
   batches of 15 to 25, with named fields, facts not verdicts, a short quote where judgment is
   needed, and "not stated" for gaps. It never pre-filters by keyword (findings 17, 19).
3. **Empty-reply nudge in the run loop.** A turn after tool results with no text and no tool call
   gets a hidden "please continue", at most two in a row per silent streak. Without it the built-in
   prompt ended 4 of 4 tr1 runs without an answer (findings 15, 18).

What it buys:

- On must-read work (tr1: 157 tickets, about 140k tokens, keyword scripts score 0): Opus quality at
  about 0.7x Opus cost, at both effort levels tested. Default effort: 0.86 against Opus's 0.84, at
  0.74x (9 pair runs, 4 Opus runs). Medium effort: 1.0 against 1.0, at 0.70x (3 runs each).
- For must-read work, run the pair at medium effort: it scored 1.0 on every seed for about what
  Opus alone costs at the default effort.
- On work a shell can narrow (fw2, mq1, cb1): about the same cost as Opus, slightly lower quality.
  Opus alone is already frugal there, and the planner's overhead eats the rest.

What it does not buy: a small fraction of Opus's cost. The planner's thinking ($0.15–0.20 a run
on tr1) and the text it must read itself (the rules, the records) set a floor near 0.6x on these
tasks. Two seeds per arm cannot resolve differences under about 20%: cost swings up to 2x between
seeds of the same arm.

Levers tried after the conclusion was first written:

- A record-length cap (round 8): shorter lines, same total. Not adopted.
- Reasoning effort (round 9): medium raises cost for both arms and keeps the pair's ratio near 0.7x.
  `low` is untested.

Next levers, untested:

- Folding the elapsed-time notice into the tool result, which removes the cause of the empty
  replies instead of recovering from them.
- Effort `low` for the planner, which might cut its thinking below the default.

## Spend ledger

| Round | Runs | Spend | Notes |
|---|---|---|---|
| Round 0 pilot | 12 | $0.79 | 4 public tasks x 3 arms x 1 seed |
| Round 1 calibration | 9 | $1.06 | fw1, ag2, cb1 x 3 arms x 1 seed |
| Round 2 variants | 15 | $2.23 | 5 Opuna variants x fw1, ag2, cb1 |
| Round 3 separating tasks | 15 | $2.76 | fw2, mq1 baselines + 3 Opuna arms (+ fw1 probe) |
| Round 4 reference off | 24 | $5.90 | fw2, mq1, cb1 x Opus + 3 Opuna arms x 2 seeds |
| Round 5 must-read task | 10 | $6.01 | tr1 x Opus, Luna + 3 Opuna arms x 2 seeds |
| Round 5b nudge + records | 8 | $5.15 | tr1 x Opus + 3 Opuna arms x 2 seeds |
| Round 6 records everywhere | 8 | $2.35 | records x fw2, mq1, cb1, tr1 x 2 seeds |
| Round 7 shipped default | 8 | $2.73 | built-in prompts x fw2, mq1, cb1, tr1 x 2 seeds |
| Round 8 compact records | 3 | $1.88 | compact prompt files x tr1 x 3 seeds |
| Round 9 medium effort | 6 | $6.66 | Opuna + Opus alone at medium x tr1 x 3 seeds |
| **Total metered** | **118** | **$37.51** | |
| Manual re-tests (D1/D2) | - | not metered | hand runs on :5088, Opuna, short prompts |

Spend comes from each run's `usage.cost.costMicros` in `runs.jsonl`.
