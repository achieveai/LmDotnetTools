# Dual-layer eval — task suite

Question: does a dual-layer agent (an expensive planner that never touches the tools, plus a cheap
executor that does all the reading) score like the expensive model alone, at a fraction of its cost?
Arms are model ids: `claude-opus-5.5` (expensive alone), `gpt-6-luna` (cheap alone), `opuna`
(Opus plans, Luna executes). A result is interesting only where the cheap model alone is **below**
the expensive one, so every task is built to be read-heavy, not solvable from a skim, and **not
answerable from memory**.

The active tasks are synthetic worlds generated from a seed. Most of each corpus is realistic
boilerplate that a cheap reader can skip on the planner's behalf. The few facts that matter sit in
prose, in the middle of long documents, and are contradicted by distractors that a careless reader
takes instead (approximate tokens = bytes / 4):

| Task | World | Questions | Fixture files | Size | ~Tokens | Answer kind |
|---|---|---|---|---|---|---|
| `fw1` | fictional freight company wiki | 10 (3-5 hops) | 662 `.md` in 7 folders | 2.37 MB | 592k | full name / team name (`norm`) |
| `ag2` | warehouse ledgers + ops bulletins | 8 | 48 `ledgers/*.csv` + 150 `memos/*.md` | 2.89 MB | 723k | exact integer / site code |
| `cb1` | fictional service-mesh monorepo | 8 | 251 (YAML, JSONC, C#, docs) | 0.42 MB | 106k | exact integer / bare word |
| `fw2` | fictional carriers' co-operative staff wiki, own house vocabulary | 10 (3-6 hops) | 567 `.md` in 9 folders | 1.32 MB | 330k | full name / yard name (`norm`), integer |
| `tr1` | fictional hosting company's Q1 support tickets + service-credit policy | 8 (aggregates over judged tickets) | 157 tickets + policy + calendar | 0.56 MB | 140k | integer / account name / ticket-id set (`idset`) |

`mq1` (MuSiQue) is back in `split: "dev"` for Opus-planned arm comparisons only; see Retired tasks.

## What each task stresses

Each task's generator computes every answer with one resolver, once under the true rules and once
under each **trap** (a plausible misreading). A question that no trap changes aborts the build, and
so does a trap that changes no question. `hidden/build-report.json` holds the trap-only answers, and
the self-check proves the checker scores them as predicted. **Predicted trap-only score** is the
score of an answer set that is right about everything except that one trap:

* **fw1 — multi-hop over a changing org.** Chains run: incident -> service (the root-cause alias is
  in incident prose, with a ruled-out decoy alias) -> owning team on a date (runbook table, then
  reorg memos, some later postponed or withdrawn) -> on-call lead that ISO week (rotation table, then
  swaps agreed in meeting-note prose) -> person (by nickname or "First L."; near-duplicate twins in
  other teams). Or: incident commander -> team on a date (transfer memos) -> line manager on a date
  (manager-change memos). Each deciding sentence sits mid-document with no heading.

  | Trap | Misreading | Predicted |
  |---|---|---|
  | `ignore_memos` | runbook/profile tables only | 0.3 |
  | `first_announcement` | take a move as announced, miss the later postponement or withdrawal | 0.7 |
  | `ignore_swaps` | rotation table only, miss meeting-note swaps | 0.5 |
  | `near_duplicate` | pick the one-vowel-different twin | 0.6 |
  | `nickname_literal` | fail to map a nickname to the profile | 0.6 |
  | `wrong_date` | answer for the wrong week/date | 0.9 |

* **ag2 — aggregation governed by buried memo rules** (hardens ag1). The CSVs carry no notes. Six of
  150 long bulletins carry a rule, each in one paraphrased mid-memo paragraph: LIS switches to
  cartons of 12 from week 12; OSL is a training mirror and is excluded from network figures; a void
  migration window on 14-15 March; BCN's carton switch is announced for week 11, then corrected to
  week 13; a proposal to exclude DUB is rejected (decoy). Other bulletins mention cartons without
  changing anything. The build aborts unless a memo-blind script scores at most
  `max_script_correct` / 8 (currently 0 / 8). An independent solver written from the rendered memos
  reproduced all 8 answers.

  | Trap | Misreading | Predicted |
  |---|---|---|
  | `ignore_memos` | script the CSVs as they are | 0.0 |
  | `ignore_unit` | miss the LIS carton rule | 0.625 |
  | `ignore_exclusion` | keep OSL in network figures | 0.375 |
  | `ignore_window` | count the void window | 0.375 |
  | `superseded_version` | BCN cartons from week 11 (the first bulletin) | 0.5 |
  | `apply_decoy` | also exclude DUB | 0.375 |

* **cb1 — effective config across overrides.** "What is the effective `X` of service `Y` in
  environment `Z`?" Layers (documented in `docs/CONFIG.md`, implemented in `platform/ConfigLoader.cs`):
  defaults < shared fragments in `include:` order (a fragment's own includes first) < `service.yaml` <
  `env/<env>/global.yaml` < `env/<env>/services/<svc>.yaml` < `Startup.ConfigureMesh`. The start-up
  code runs last. Its conditions test `env.Name`, `env.IsProduction` (true for staging **and** prod;
  see `platform/MeshEnvironment.cs`), feature flags (catalog default < `flags/<env>.jsonc`) or another
  setting. Every question needs Startup.cs plus at least 2 config or flag files that change the
  answer when dropped. Every question has a commented-out override (YAML `#`, JSONC `//` or C# `//`)
  that would change it, and a code step that changes it. An independent parser of the rendered files
  reproduced all 8 answers, and every C# file passes `dotnet csharpier check` (CI formats the tree).

  | Trap | Misreading | Predicted |
  |---|---|---|
  | `honor_commented` | treat commented-out lines as live | 0.0 |
  | `ignore_code` | stop at the YAML, skip Startup.cs | 0.0 |
  | `ignore_env` | skip the env overlays | 0.5 |
  | `include_order_reversed` | first include wins | 0.75 |
  | `flag_catalog_default` | use flag defaults, ignore `flags/<env>.jsonc` | 0.125 |
  | `isprod_literal` | `IsProduction` means prod only | 0.75 |
  | `wrong_section` | take the `admin:` value | 0.875 |
  | `skip_nested_include` | ignore a fragment's own `include:` | 0.75 |

* **fw2 — indirect naming, so grep cannot shortcut** (built after pilot `20260924-094432-6e9c5c52`
  put fw1, ag2 and cb1 at ceiling: every arm grepped the question's words). Questions name every hop
  by description ("the depot that took over the night routes of the depot that flooded in the spring
  of 2032"). The corpus never uses those words. Its house style says yard, yardmaster, area, area
  steward, Council, Presiding Member / Clerk / Keeper of the Accounts, substantive / interim,
  amalgamation, overnight workings, inundation or high water, stood down or mothballed. Only
  `handbook/glossary.md` maps one vocabulary to the other, and the generator aborts if any other file
  uses a question word. Facts sit mid-document in Council minutes, traffic notices (some later
  corrected), incident reports, papers marked in prose as drafts or rejected proposals, forum rumours,
  and people and yard pages that are stale snapshots. Each true hop has 2-3 near-duplicates: an
  interim cover beside the substantive holder, a rescinded appointment, a corrected notice, a
  cancelled area move, a reopened yard, a draft naming another approver, a rumour naming another
  yard, a flood in another season, or a near-miss report where the water stopped short of the yard.
  Families: flood -> takeover -> yardmaster at a date (F1) or area steward at a date (F6); Council
  role succession -> that person's last substantive yard (F2); merger approver -> their area -> yards
  ever in it -> count still in use (F3); sum of workings handed into a flooded yard's area (F4);
  distinct substantive yardmasters of a merged yard (F5). 5 of the 10 answers are counts or sums.

  | Trap | Misreading | Predicted |
  |---|---|---|
  | `count_interim` | treat an interim cover as the confirmed holder | 0.4 |
  | `trust_draft` | act on a draft paper or rejected proposal | 0.2 |
  | `trust_rumour` | act on a forum rumour | 0.1 |
  | `first_notice` | take the first notice, and miss its correction, cancellation, rescission or reopening | 0.2 |
  | `near_miss` | adjacent slip: another flood the same year, the next holder in a succession, or the other end of the year | 0.0 |

  Adversarial grep probe (`python tasks/fw2/hidden/grep_probe.py [--translate]`) runs `grep -ril` for
  each distinctive question word (stop words removed), then asks whether the files matching all
  words, or the most words, contain a named gold answer. With the words as written it lands 0/10:
  no file matches every word, and the best-matching files hold no gold. After a glossary
  translation it lands 1/10: q6's yard appears in the 9 best-matching files, which are Council
  minutes. The 5 count and sum answers appear in no file. Two chains were checked by hand against
  the rendered files: q2 (the spring 2032 flood at Daidhope was handed to Tusholm, then corrected to
  Drorcote; an autumn Daidhope flood went to Faimbury; Drorcote's last substantive appointment is
  Rosalind Kerrton, April 2033) and q7 (Drorcote was in Lustane on 1 January 2032 and moved to Girary
  in May; the 2032 handovers were 8 to Drorcote, which is Lustane, 6 to Graidfield, which is Girary,
  and 15 to Faimbury, which is Selish, so the answer is 8).

* **tr1 — every document must be read, and the policy is subtle** (built after rounds 1-4 showed Opus
  greps and never reads the bulk text on fw1, fw2, ag2, cb1 and mq1). 157 tickets of 517-1187 tokens each
  (by bytes / 4): customer narrative, agent replies, pasted logs, side conversations. Each ticket's class shows only in what
  the narrative says happened. Every class shares the vocabulary a keyword search would use (outage,
  down, CDN, DNS, firewall, maintenance, minutes), often in a negation ("we checked our CDN partner
  first; it was healthy"). Titles are chosen by customers, and a quarter of them point at the wrong
  class. The policy (`policy/service-credit-policy.md`, about 3.3k tokens) has six interacting clauses:
  production only, with minutes measured by monitoring rather than the customer's estimate
  (2.2, 2.4); a threshold of 30 minutes, or 15 for Enterprise incidents that begin on or after
  1 February (3.2, date-bound); customer-side changes do not count unless made on a Written
  Instruction given in a ticket (4.2; a phone call does not count); third-party failures do not count
  (4.3), which Appendix B amends for the two providers the company contracts itself; minutes inside
  a maintenance window are deducted before the threshold, but only if the window was posted at
  least 72 hours ahead (5.1-5.3, precedence); and incidents must be reported within 7 days, one per
  account however many tickets (6.1-6.2). Questions: count, Enterprise ids under 30 minutes, the
  account with the most incidents, count of provider failures, one account's minute total, count
  for one month, ids of customer-change incidents, and the number of accounts. The build rejects a
  world where any question is changed by fewer than 2 traps.

  | Trap | Misreading | Predicted |
  |---|---|---|
  | `no_appendix` | policy without Appendix B: partner failures excluded | 0.375 |
  | `appendix_overreach` | Appendix B stretched to the customer's own CDN, DNS or SSO | 0.5 |
  | `miss_unless` | customer changes never count, ignoring the Written Instruction exception | 0.5 |
  | `verbal_advice` | advice given on a phone call counts as a Written Instruction | 0.5 |
  | `customer_estimate` | use the customer's duration, not monitoring's | 0.25 |
  | `ignore_maintenance` | no deduction for announced windows | 0.75 |
  | `late_notice_window` | also deduct windows posted less than 72 h ahead | 0.875 |
  | `no_effective_date` | Enterprise 15-minute threshold for January incidents too | 0.75 |
  | `count_duplicates` | count every ticket, not every incident | 0.5 |
  | `include_nonprod` | count staging or preview copies | 0.625 |
  | `ignore_deadline` | ignore the 7-day reporting rule | 0.625 |

  Keyword baseline (`python tasks/tr1/hidden/keyword_baseline.py <dir>`, then `check.ps1`): outage
  words minus obvious exclusion words, the first "N minutes" in the text, the month the ticket was
  opened. It scores **0/8**. At ticket level it picks 38 tickets: 19 true incidents and 19 false
  positives, and it misses 29 of the 48 true incidents. Two tickets were classified by hand:
  T-4392 (production; a DNS change made by the customer, but following steps written by support in
  T-4363, so it counts under 4.2; monitoring 06:30-07:58 = 88 minutes; reported within a day; no
  window that day; Standard threshold 30, so it qualifies with 88) and T-4331 (production platform
  fault, 01:43-03:46 = 123 minutes, of which 01:43-03:30 fall in MW-03, posted 6 days ahead, so 16
  Qualifying Minutes remain, under 30 for a Business account, so it does not qualify; the
  customer's "2 hours and 21 minutes" is not used).

## Memory probe (why task.md alone answers nothing)

The pilot (`results/20260924-090015-641225f7`) retired the public-set tasks: Opus answered them
without opening a file. The active tasks are memory-proof by construction. Argument per task:

* **fw1:** the company, its 150 people, 14 teams, 48 services and 70 incidents are invented. A web
  search found no real "Brannock Vale" or "Quillfeather". Every surname is syllable-synthesised.
  Each answer depends on seeded facts that exist only in the corpus: which alias maps to which
  service, which memo was withdrawn, who swapped which week. task.md names an incident id and a date,
  neither of which carries any information outside the corpus. The best memory-only strategy is to
  guess a name from the corpus, which the model has never seen.
* **ag2:** every quantity is drawn from a seeded RNG, so no number exists anywhere else. The site
  codes in task.md are the only candidates for q4, but which one wins depends on the BCN week
  correction. The rules are only in the bulletins. task.md says bulletins can change the counting
  but not which rule or where.
* **cb1:** the repo, its services, fragments, flags and every value are invented and seeded.
  task.md names a key, a service and an environment. The answer is a seeded number transformed by
  seeded code (or one of 4-5 policy words chosen by a seeded chain). A 1-in-5 guess on the two text
  questions is the ceiling for memory.
* **tr1:** the company, its 22 accounts, its people, its partners and every ticket are invented and
  seeded. The questions name one account and one month; the answers are counts, sums and sets of
  ticket ids that exist only in this corpus. The memory assertion caught q5 naming q3's answer in an
  early build, and the generator now forbids it.
* **fw2:** the co-operative and its yards, areas and 210 people use invented syllable names. Every
  appointment, flood, takeover and merger is drawn from a seeded RNG. The questions carry only
  years, seasons and ordinals. The gold yard (q6) is one of about 36 invented names, and the counts
  depend on seeded rescissions and reopenings.

The self-check enforces the mechanical part (`memory` assertion, non-retired tasks): no gold answer
(4+ characters, or 3+ digits) appears in the message the model receives or in any fixture path. A
planted leak was confirmed to fail it. `mq1` fails it by construction: its answers are article
titles. Its `meta.json` therefore sets `memoryExposed`, which lists the leaks without failing.

## Contract

Same as [compaction-eval](../../compaction-eval/tasks/README.md):

* `task.md`: the message after `---`, with `{SEED}` substituted.
* `meta.json`: `family`, `split`, `seeds`, `timeoutMinutes`, `requiredFixtures`.
* `fixtures/`: copied into a fresh workspace per run.
* `hidden/`: for the checker only; it also holds the generator and `build.json`.
* `check.ps1`: `pwsh check.ps1 -Workspace <dir> -Out <score.json>`. Exits 0 when it judged and 2 when it could not judge. No LLM.

The agent writes `answers.json` in the workspace root. Every `check.ps1` calls the shared
[`tools/Score-Answers.ps1`](../tools/Score-Answers.ps1) with its `hidden/key.json`. Answer kinds:

* `norm` (fw1, cb1 text settings): normalised exact match with no partial credit. Normalisation folds case, accents and punctuation; dashes become spaces.
* `int` (ag2, cb1): an exact whole number; `1,234` is accepted.
* `exact` (ag2 q4): a case-insensitive site code.
* `idset` (tr1 q2, q7): a JSON array or a string of ids; every `LETTERS-DIGITS` token counts, case-folded, and the set must equal the gold set exactly.
* `text` (retired mq1/hq1): normalised match or token F1 >= 0.8.
* `choice` (retired lb1): one letter A-D.

`score` = correct / N, one check per question. `pass` means all correct and `fail` means none are.
An answer may be `{"answer": ..., "evidence": [files]}`. Evidence is never scored; its recall against
the gold files is reported in each check's `detail`.

## Rebuilding and difficulty knobs

Generators are deterministic: rebuilding gives byte-identical fixtures, keys and reports (verified).

```
python evals/dual-layer-eval/tasks/fw1/hidden/gen_world.py
python evals/dual-layer-eval/tasks/ag2/hidden/gen_ledgers.py
python evals/dual-layer-eval/tasks/cb1/hidden/gen_repo.py
python evals/dual-layer-eval/tasks/fw2/hidden/gen_world.py      # prose from hidden/prose.py
python evals/dual-layer-eval/tasks/tr1/hidden/gen_tickets.py    # prose from hidden/render.py
pwsh evals/dual-layer-eval/tools/selfcheck.ps1 -Task fw1,ag2,cb1,fw2,tr1   # or tasks/<id>/hidden/selfcheck.ps1
```

fw2 draws its questions from a separate RNG before rendering, so prose knobs (`filler_docs`,
`filler_scale`, `fact_doc_scale`) leave `key.json` unchanged (checked with `cmp`). Its prose comes
from 16 document kinds (weather logs, match reports, letters, tributes, kit reviews and so on), and
no sentence repeats within a paragraph. Across the corpus, 35% of sentences are unique, against 21%
for fw1.

Shared boilerplate comes from [`tools/filler.py`](../tools/filler.py). Its word banks avoid the
vocabulary the planted facts use. Knobs live in `hidden/build.json`. Changing any knob changes the
task: give it a new id, or treat old results as a different task. For cb1, raising `padding` from 1 to 3
was checked to leave every answer unchanged.

| Task | Harder | Easier |
|---|---|---|
| fw1 | more `extra_swaps` / `decoy_swaps` / `decoy_memos` / `twins`; more `people`, `meetings`, `padding` (length) | fewer of each |
| ag2 | more `memos` / `padding` (the rules drown further); more `window_rows`; higher `duplicate_rate` | fewer memos |
| cb1 | more `services` / `general_fragments` / `padding`; per-question `must_flip` picks the extra trap each question must fail | fewer |
| fw2 | more `draft_*` / `rumour_*` / `near_misses_per_year`; higher `correction_rate` / `transfer_cancel_rate`; more `filler_docs` | fewer; drop the glossary indirection (would reopen grep) |
| tr1 | more edge `slots` (for example `config_verbal`, `maint_late_notice`, `own_provider`) or `duplicates`; raise `min_flips_per_question` | fewer edge slots; more `platform_ok` / `no_outage` |
| all | more questions (`a/b/c_variants`, `questions`) lowers per-seed variance | |

**Calibration target:** `gpt-6-luna` alone at 40-75%, `claude-opus-5.5` alone above it. Pilot
`20260924-094432-6e9c5c52` put fw1, ag2 and cb1 at ceiling for every arm, which is why fw2 exists.
No model has been run on fw2 or tr1. Run a `--seeds 1` pilot of both single-model arms first. If the arms
tie, use the trap table to see which misreading the cheap arm avoids and harden that knob.

## Retired tasks

Kept runnable (name them with `--tasks`) and seedable, but `split: "retired"` in `meta.json`, which
records the reason. They are no longer in `runner.jsonc`.

| Task | What it was | Why retired (pilot `20260924-090015-641225f7`, 1 seed) |
|---|---|---|
| `mq1` | MuSiQue 2-4 hop, 1,006 docs | public questions are memory-exposed; arms separated only a little (Opus 0.875, Luna 0.625). **Restored** to `dev` for Opus-planned arm comparisons (see its `meta.json` `note`) |
| `hq1` | HotpotQA distractor hard, 1,197 docs | Opus scored 0.9 in one tool call with no file reads |
| `lb1` | LongBench v2 multi-doc MC, 266 pages | Opus scored 0.625 (chance 0.25) with no file reads |
| `ag1` | synthetic ledgers, header-note traps | every arm scored 1.0 (Luna for $0.003); a script solves it |

They are rebuilt by [`tools/build_qa.py`](../tools/build_qa.py) (`mq1`, `hq1`, `lb1`, from raw
downloads kept out of git in `.logs/dual-layer-eval-build/raw/`, sha256-checked) and by
`ag1/hidden/gen_ledgers.py`.

## Data licences

| Task | Upstream | Licence | Record |
|---|---|---|---|
| fw1, ag2, cb1, fw2, tr1 | synthetic (generated here; all names invented) | repository licence | this file, `hidden/gen_*.py` |
| ag1 (retired) | synthetic | repository licence | `ag1/fixtures/SOURCE.md` |
| mq1 (retired) | MuSiQue v1.0 (StonyBrookNLP) | CC BY 4.0 | `mq1/licenses/`, `mq1/fixtures/SOURCE.md` |
| hq1 (retired) | HotpotQA (Yang et al. 2018) | CC BY-SA 4.0 — the derived subset stays CC BY-SA | `hq1/licenses/`, `hq1/fixtures/SOURCE.md` |
| lb1 (retired) | LongBench v2 (THUDM) | Apache-2.0 (dataset card) / MIT (repo); contexts are third-party documents — **open question, see `lb1/licenses/README.md`** | `lb1/licenses/`, `lb1/fixtures/SOURCE.md`. **Not committed** (see `../.gitignore`); rebuild locally with `tools/build_qa.py` |

Hand testing: `pwsh evals/dual-layer-eval/seed-workspace.ps1 -Workspace <dir> [-Task fw1,ag2,cb1]`.
It copies each task's fixtures and prompt into `<dir>/<id>/`, never the answer keys. With no
`-Task`, it seeds every non-retired task. It writes a README.md listing every task folder present.
