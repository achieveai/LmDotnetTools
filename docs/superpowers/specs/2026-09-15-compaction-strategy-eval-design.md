# Compaction strategy eval — design

Status: draft for review. Owner decisions D1–D6 are in
`.claude/scratchpad/conversation_memories/compaction-tests/eval/decisions.md`.
Research: `research-runner.md`, `research-messages.md`, `research-pricing.md`, `research-htr.md`,
`research-copilot.md` in the same folder. Every claim about current code cites those files.

## 1. Objective

Choose, with evidence, how compaction should behave: which summary model, which prompt, which
runtime cleanup, which collaboration handling — per task class if the answer differs. "Best" is
**job outcome first, cache-aware cost second**, measured against a no-compaction baseline.

**Done when**
1. A strategy (or a router: task family → strategy) is admitted by the merge gate on held-out tasks.
2. Every admitted strategy beats `Off` on outcome where `Off` overflows, and costs no more than a
   declared multiple of `Off` where `Off` fits.
3. The tree, every prompt version and every insight are committed; a reader can trace why the
   winner won.

Out of scope: changing production defaults. Every runtime addition ships behind an option that
defaults to today's behaviour.

## 2. Vocabulary

| Term | Meaning |
|---|---|
| Strategy | A named, versioned config: summary model, prompt version, runtime-check set, prefix mode, knobs. File: `evals/compaction-eval/strategies/<name>.json`. |
| Cell | One isolated host process running one strategy. Compaction knobs are host-level (`--Compaction:Key=value`, research-runner §4.2), so a cell is the unit of variation. |
| Run | One task × one seed in one cell. |
| Baseline | The same task × seed in the `off` cell (`Compaction:Mode=Off`). |
| Tier | T0 replay, T1 corpus, T2 live (Approach A). |
| Hypothesis | One strategy delta, expressed as a child strategy file + a claim. HTR node. |

## 3. Message taxonomy — what compaction must know about each kind

Source: research-messages §1. The eval treats each kind by its **protection class**; the runtime
checks in §4 and the judge probes in §7 are derived from this table.

| # | Kind (history row unless noted) | Today | Protection class | Eval expectation after a cut |
|---|---|---|---|---|
| 1 | User text (`TextMessage`, Role.User) | human row; quoted verbatim into CurrentInstruction/Instructions (R2/R5) | **Pinned-verbatim** | Every standing instruction present verbatim in the active envelope. |
| 2 | Assistant text / citations | summarized | Summarized | Narrative names the direction of work. |
| 3 | Reasoning (`ReasoningMessage`) | summarized as text; excluded from recall | Droppable | Never needed after a cut. |
| 4 | Tool call (`ToolCallMessage`/`ToolsCallMessage`) | not quotable; artifacts from file tools | Summarized | Artifacts section carries every written path. |
| 5a | Tool result — **resource** (Read, Glob, Grep, Skill, WebFetch, WebSearch, list-tasks, get-task) | cleared by recency only | **Reproducible** | Only the latest copy per identity survives in the view; older copies cleared with a pointer to the latest seq. |
| 5b | Tool result — **mutation** (Write, Edit, MultiEdit, NotebookEdit, add-task, update-task, …) | cleared by recency | Summarized-as-artifact | Path/id lands in Artifacts or Tasks; content may clear. |
| 5c | Tool result — **shell** (Bash, PowerShell) | cleared by recency | **Non-reproducible** | Trim (head/tail), never fully clear inside the last K shell turns; failures kept longer than successes. |
| 5d | Tool result — **deferred** (AskUserQuestion, Wait block) | R6 obstacle; cut refused | Blocking | Unchanged. |
| 5e | Tool result — collaboration receipt (`SendMessage` accepted, `Agent` spawn, Check/Wait) | cleared by recency | Summarized | Agents section + OpenExchanges (§4 RC3). |
| 6 | `AgentMessage.Question` / `DelegateTask` (inbound) | **not** human row, **not** obstacle, raw text to summarizer | **Async-open** | If unanswered at the cut: pinned in `OpenExchanges` with `message_id`, `from`, seq, age; still answerable after the cut. |
| 7 | `AgentMessage.Steer` / `DelegateTask` (as directive) | human row (IsHumanRow) | Pinned-verbatim | Same as #1. |
| 8 | `AgentMessage.Response` | content row | Closes an exchange | Removes the matching open exchange. |
| 9 | `AgentMessage.TaskUpdate` / `DeliveryFailure` | content row | Summarized | Headline only. |
| 10 | `NotifyMessage` (subagent-completion, descendant-question, todo-nudge/digest, …) | input row; completion feeds Agents outcome | Summarized / Agents | `descendant-question` is an **open exchange** until answered. |
| 11 | `CompactionCheckpointMessage` | row, never dispatched; merged into next manifest | Carried | Previous manifest merged. |
| 12 | `UsageMessage`, `ImageMessage`, `CompositeMessage` | 12-token estimate; kept | Neutral | — |
| 13 | Todo board (**transient, not a row**) | manifest Tasks from live snapshot | Pinned-from-snapshot | Board matches snapshot at cut. |
| 14 | Context pressure / compaction status / usage banner (transient) | not rows | Not compacted | — |

Outbound questions the agent itself asked (`SendMessage msg_type=question` receipt at #5e whose
`message_id` has no `Response` yet) are open exchanges too; the agent must recognise the answer
when it arrives after the cut.

## 4. Runtime checks (deterministic, pre-summary, option-gated)

Each check is a pure function over `SequencedHistory` (+ the tool-knowledge registry), unit-tested
in isolation, switchable per strategy. None calls a model. They run in the fit ladder **before**
clearing-by-recency, so the model-free path benefits too.

**Tool-knowledge registry** (new, `CompactionOptions.ToolKnowledge`, bound from host config):

```json
{ "Read":   { "kind": "resource", "identity": ["file_path"] },
  "Glob":   { "kind": "resource", "identity": ["pattern","path"] },
  "Grep":   { "kind": "resource", "identity": ["pattern","path","glob"] },
  "Skill":  { "kind": "resource", "identity": ["skill"] },
  "WebFetch": { "kind": "resource", "identity": ["url"] },
  "Bash":   { "kind": "shell" }, "PowerShell": { "kind": "shell" },
  "Write":  { "kind": "mutation", "path": "file_path" }, "Edit": { "kind": "mutation", "path": "file_path" },
  "SendMessage": { "kind": "collab" }, "Agent": { "kind": "collab" },
  "list-tasks": { "kind": "resource", "identity": [] }, "get-task": { "kind": "resource", "identity": ["taskId"] } }
```

Unknown tools default to `shell` (conservative). MCP `readOnlyHint`/`idempotentHint`, when a
server sends them, seed `resource` automatically (research-messages §3c: not propagated today —
small change in `McpClientFunctionProvider`).

| Id | Check | Rule | Replaces / extends |
|---|---|---|---|
| RC1 | Resource dedupe | Group resource results by (tool, canonical identity args). All but the newest → cleared placeholder naming the newest seq. Applies at any age. | `ToolResultView` recency clearing (research-messages §1e) — runs first. |
| RC2 | Shell retention | Shell results outside the last `ShellKeepTurns` (default 2): head/tail trim to `ShellTrimChars`; a result that is an error (non-zero exit / `Error:` prefix) keeps 2× the chars. Never fully cleared until covered by a checkpoint. | Recency clearing. |
| RC3 | Open exchanges | At cut: build `OpenExchanges` = inbound Question/DelegateTask/descendant-question without a matching Response (by `message_id`/`in_response_to`), plus outbound question receipts without an answer. Pin as a new manifest section `{message_id, direction, from, to, seq, asked_at_run, summary≤200 chars}`. **Never blocks a cut.** Envelope budget rank = Directive (never shrunk). | New manifest section + validator V10 (every open exchange at the cut appears). |
| RC4 | Board snapshot | Unchanged (live snapshot wins). | — |
| RC5 | Structured agent rows for the summarizer | `Describe` gains an `AgentMessage` arm: `agent-message {type} from={id} id={message_id} in_response_to={id}: {body}`; `NotifyMessage descendant-question` likewise. | `ProviderCheckpointSummarizer.Describe` (research-messages §1g). |
| RC6 | Skill reload | RC1 special case: identity = skill name; the newest load wins; older loads cleared even inside the keep-turns window. | — |
| RC7 | Steer fidelity | **Verified already covered:** `ManifestAssembler.cs:84-93` carries every `IsHumanRow` of the covered span into `Instructions`, and `Steer`/`DelegateTask` are human rows (`SequencedMessage.cs:46-55`). No runtime change; the J3 probe `steer_respected_after_cut` measures whether the model *obeys* it. | R5 (no change). |

Each RC is exposed as a boolean in `CompactionOptions.Checks` (`Rc1ResourceDedupe`, …), default
**false** in production, set per strategy in the eval.

## 5. Strategy space

Axes (each strategy file sets all of them):

| Axis | Values (round one) |
|---|---|
| `summaryModel` | `same-as-agent`, `gpt-5.6-luna`, `gpt-5.6-terra`, `gpt-5.6-sol` |
| `promptVersion` | `v0` (today's `ProviderCheckpointSummarizer.SystemPrompt`), `v1-human-first`, `v2-trajectory`, `v3-collab-aware`, … (§5.1) |
| `checks` | `none`, `resource` (RC1+RC6), `resource+shell` (+RC2), `collab` (+RC3+RC5), `all` |
| `prefixMode` | `cold` (today), `cached-prefix` (§5.2) |
| `knobs` | `TargetRatio`, `ClearToolResultsKeepTurns`, `MinTailTokens` — only when a hypothesis names them |
| `windowTokens` | declared window for the cell (§6.3) |

A strategy = one point in that space + `parent` + `hypothesis` text. `off` is the fixed baseline.

### 5.1 Prompt versioning

`evals/compaction-eval/prompts/summary/<version>.md`, YAML front-matter:

```yaml
id: v2-trajectory
parent: v0
hypothesis: "Naming the direction of work and the last three tool outcomes reduces post-cut re-reads."
created: 2026-09-15
status: candidate | admitted | pruned
```

Rules: a file is **immutable once a sweep cites it**; any edit is a new id. The runtime gains
`CompactionOptions.SummaryPromptPath` (file read once at startup; null = built-in v0). The sweep
manifest records the SHA-256 of every prompt used. Learnings from each version go in the tree node
(§9), not in the prompt file.

Initial variants (each ≤ 40 lines, same JSON contract as v0 so validators V1–V9 apply unchanged):

- **v0** — current prompt, unchanged (control).
- **v1-human-first** — order: every human/steer row verbatim, then the model's last reply to each,
  then everything else compressed; narrative ≤ half the cap.
- **v2-trajectory** — adds "direction": what the agent was about to do next, the last three tool
  outcomes with their seqs, blockers; artifacts get one-line "why it matters".
- **v3-collab-aware** — v2 + explicit `open_exchanges` echo (must match RC3's list), per-agent
  "what I owe / what I await", board delta since previous checkpoint.
- **v4-focus-first** — when `Focus` is present, the narrative is organised by the focus; otherwise
  identical to v2. (Manual-compaction path only.)

### 5.2 Cached-prefix summary call

New `ICheckpointSummarizer` implementation `CachedPrefixCheckpointSummarizer`, selected by
`CompactionOptions.SummaryPrefixMode = CachedPrefix`. Request = the agent's own system prompt + the
same tool definitions + the rows in the agent's own serialization (so the provider serves them at
the cache-read rate) + one appended user turn holding the summary instruction and the v-prompt.
`Functions` are sent but tool use is disallowed via `ToolChoice=none` where the transport supports
it; a reply containing a tool call is a failed attempt. Goes through the raw provider agent like
today. Not valid when `summaryModel != same-as-agent` (a different model has no shared cache);
the strategy loader rejects that combination.

Transport facts that bound this hypothesis (research-copilot §1–2): `gpt-5.6-*` is served over
the OpenAI `/responses` SSE transport; our `MultiTurnAgentLoop` owns history, so compaction
applies. The Responses path sends **no** `cache_control` and no `prompt_cache_key`; caching is
whatever the backend does implicitly on a byte-identical prefix, and `PromptCachingMode.Auto` is a
no-op there. So the summarizer must reproduce the agent's request prefix **byte for byte**
(system prompt, tool schemas, row serialization, same order). The hit is observable only through
`usage.input_tokens_details.cached_tokens`; live logs show it populated and large on this path.
J4 reports `cached_tokens` on the summary call as the direct test of the hypothesis.

## 6. Cost model (cache-aware)

### 6.1 Per-run cost

Reuse `ModelPricing.Estimate(UsageRecord)` (research-pricing §1.4) per record, deduped by
`ProviderAttemptId`; split by `ExecutionKind` so `Compaction` records are the summary cost.

```
run_cost      = Σ agent records + Σ compaction records
delta_vs_off  = run_cost − baseline_cost      (same task, same seed)
cache_hit     = Σ cache_read / Σ (input + cache_read + cache_write)   [accounting-aware]
```

Report `CostCompleteness` verbatim; a `Partial` total is printed as `≥`.

On `gpt-5.6-*` the accounting is fixed by the transport (research-copilot §2): `cached_tokens` is
a subset of `input_tokens`, `cache_creation_input_tokens` is never emitted (`created=0` in every
live row), so `CacheAccounting = SubsetOfInput` and `cache_write = 0` by construction. The
formula reduces to `cache_hit = Σ cached_tokens / Σ input_tokens`.

### 6.2 Shadow prices for gpt-5.6-* (D5)

Copilot ids have no price in the repo by design (research-pricing §3.5). The eval supplies
`evals/compaction-eval/pricing.shadow.json` — one entry per id with `_source`, `EffectiveDate`,
rates and `CacheAccounting` — and injects it **only into eval cells** via `--Pricing:Version=shadow-<date>`
and `--Pricing:Models:…` args. The sweep manifest records the shadow file's hash and every cost
table is headed "shadow prices, not billed". Rates: the public list price of the closest
equivalent model, chosen once and cited in `_source`; the tree records them as an assumption.

Mechanics (research-copilot §5): `PricingCatalog` is id-agnostic — it validates value shapes
only, so the shadow entries need **no code change**. Three rules follow from the code:

- Price **all three** ids or none: a conversation total is a strict fold, one unpriced model
  nulls the whole conversation.
- Set `CacheWrite*PerMillion = 0` explicitly (the Copilot catalog itself lists
  `cache_write_price: 0` for this family) so estimates are `Complete`, not `Partial`.
- Provenance stays `CostProvenance.PublicEstimate` (no enum change); the shadow marker is the
  `Pricing:Version` string `shadow-<date>`, which every report row carries.

The Copilot `/models` response also carries `billing.token_prices` per id. Its units are
undocumented, so round one records that block in the sweep manifest as evidence but does not
price from it.

### 6.3 Windows for gpt-5.6-*

The policy answers `capacity_unknown` without a window. Each cell declares `MaxContextTokens` for
its model in the same injected `Pricing:Models` entry. Round one **clamps** windows (e.g. 64k on a
larger real window) so compaction triggers early and cheaply; the true window is recorded
alongside.

Facts (research-copilot §3): the Copilot `/models` response carries
`capabilities.limits.max_context_window_tokens / max_prompt_tokens / max_output_tokens`, but
`CopilotModelCatalogParser` drops them, and no `gpt-5.6-*` window is configured anywhere — today
these ids run with no window, no utilization gauge and no threshold trigger. The only window
signal is the after-the-fact `model_max_prompt_tokens_exceeded` overflow error. No recorded
fixture contains a `gpt-5.6-*` entry; the sibling `gpt-5.5` shows 1 050 000 context and a
**two-tier prompt budget**: `default` ≤ 272 000 prompt tokens, `long_context` ≤ 922 000 at ~2×
input price.

Decisions:

- Windows come from config (`Pricing:Models:<id>:MaxContextTokens`), not from a parser change.
  Projecting the real fields into `CopilotModelInfo` is out of scope for round one.
- The T2 harness fetches `GET /models` once per sweep and writes each id's raw `limits` and
  `billing` blocks into the sweep manifest; the "unclamped" cell (§13) uses that recorded
  `max_prompt_tokens` of the **default** tier, never the long-context tier, so no cell crosses
  the price cliff.
- Clamped cells use 64k. A clamp is a runtime belief, not a provider limit: the `off` baseline
  under the same clamp never fails at 64k, it only runs past it. So "Off fitted" in §7 means
  "off did not hit the provider's real `model_max_prompt_tokens_exceeded`", and the cost
  comparison is compaction-at-64k against an un-truncated `off` — the honest worst case for
  compaction.

### 6.4 Counterfactual columns

For every compaction record the reader also computes `summary_cost_if_cached_prefix` =
(rows tokens × cache_read_rate + instruction tokens × input_rate + output × output_rate) so the
prefix-reuse hypothesis has a predicted number before it has a measured one.

### 6.5 `PredictSavings` note

The runtime formula prices removed tokens at the full input rate on both sides (research-pricing
§4.5, gap 1). The eval does **not** patch it in round one; "fix PredictSavings" is a hypothesis
the tree can raise once T2 numbers exist.

## 7. Judge

Deterministic first (D1). Score object `compaction-eval/score@1` per run.

| Layer | Source | Output |
|---|---|---|
| J0 validity | store: ≥1 thread, run terminal, no harness error, tools present | `valid`, reasons |
| J1 outcome | the task's checker (`check.ps1` / hidden tests / expected numbers) run against the cell's workspace after the run | `outcome ∈ {pass, partial(0..1), fail}`, `checks[]` |
| J2 quality (LLM) | judge model `gpt-5.6-sol`, pairwise: final artifacts + final assistant message of run vs baseline, both orders, 2 votes; rubric: instruction fidelity, completeness, no fabrication, user-question answered | `quality ∈ {win, tie, loss}`, rationale (stored, never fed back to the agent) |
| J3 compaction probes (deterministic) | store + compaction state | `overflowed`, `checkpoints`, `open_exchange_answered_after_cut`, `steer_respected_after_cut` (no post-cut action contradicting the steer, via checker-specific predicate), `recall_calls`, `reread_after_cut` (resource identity read before the cut and again after), `fabricated_compliance` heuristic (todo-eval rule) |
| J4 cost | §6 | `cost_micros`, `delta_vs_off`, `cache_hit`, `summary_cost`, `completeness` |

**Objective for the merge gate** (per task family f, over held-out tasks × seeds):

```
O_f(strategy) = mean(J1 pass)                      primary
                subject to  mean(delta_vs_off) ≤ 0.25 × mean(off_cost)  where Off fitted
tie-break      = −mean(cost)
```

The 0.25 bound is the measured "Compact is +22–35 % on fitting conversations" (research-pricing
§4.6) turned into a target: an admitted strategy must do better than today. Both numbers are
printed in every report; the bound is a pin, revisable by the owner.

Refusals mirror todo-eval: different task corpus hash, different judge version, coverage < 50 %,
fault rate > 25 % → the comparison is refused, never reported as a pass.

## 8. Task suite

`evals/compaction-eval/tasks/<id>/` — `task.md` (user message, `{SEED}` placeholder), `meta.json`
(family, dev|test split, seeds, expected window pressure), `fixtures/`, `check.ps1` (exit code +
JSON score; no LLM). Workspace = a fresh copy of `fixtures/` per run. Sandbox: gateway on, egress
rules empty (default-deny ⇒ "no external dependencies"; research-runner §2.3), Python 3 and Node 22
from the host PATH (checked by J0).

| Id | Family | Task (one line) | Deterministic checker | Why it stresses compaction |
|---|---|---|---|---|
| c1 | coding-py | CSV aggregator CLI (stdlib only) with 6 hidden tests | pytest via `python -m unittest` | many Read/Write/Bash rounds |
| c2 | coding-node | Markdown→HTML converter, no deps, 8 hidden tests | `node --test` | repeated file reads |
| c3 | coding-refactor | Add a feature to a 12-file fixture repo; existing tests must stay green | test suite + diff scope check | large reads early, must recall API shape late |
| c4 | coding-long | Tiny job-queue lib + CLI + tests, 3 milestones | tests + milestone files | forces 2+ checkpoints |
| r1 | research-local | Answer 10 questions about a vendored doc set (≈400 KB) with file:line citations | answer key + citation exists | Read-heavy; RC1 target |
| r2 | research-memo | Compare two vendored library docs, write a decision memo with required sections | section presence + facts table; J2 for quality | long reads, then writing |
| d1 | data | Stats over vendored CSV (≈2 MB, pinned GitHub source) — 8 numbers | exact/tolerance match | shell output heavy |
| d2 | data | Anomaly report: list rows matching rules across 3 CSVs | set equality | re-reads + shell |
| m1 | collab | Lead + 3 workers on the board; worker asks the lead a Question at ~40 % that the lead must answer after its own compaction | answer present in worker thread, board complete | **open exchange across a cut** |
| m2 | collab | Two agents exchange DelegateTask/Response over long work; lead compacts twice | both responses present, order correct | owed responses |
| s1 | steer | User corrects the goal mid-run, then 30+ tool calls follow | final artifact matches the corrected goal, not the original | R4/R5/RC7 fidelity |
| s2 | manual-focus | Operator triggers `POST /compaction {focus}` mid-run | focus terms present in the active envelope; outcome unaffected | manual path |

Split: dev = {c1, c2, r1, d1, m1, s1}; held-out test = {c3, c4, r2, d2, m2, s2}. Seeds: 3 per
task (Avg@3, HTR). Round-one budget (D2, ~$200): 4 strategies + off × 6 dev tasks × 3 seeds
≈ 90 runs for exploration; held-out only for merge-gate candidates.

Data fixtures: small public CSVs vendored under `fixtures/` with `SOURCE.md` (repo URL + commit).

## 9. Coordinator — hypothesis-tree refinement

Adapted from Arbor/HTR (research-htr.md). Persisted state in `evals/compaction-eval/tree/`:
`tree.json` (nodes), `insights.md` (root-ward abstractions), one folder per node with its
strategy file, sweep ids, scores, distilled insight.

Node = `{id, parent, hypothesis, strategy, status: pending|running|scored|admitted|pruned,
dev_score, test_score?, evidence: {sweepIds, T0, T1, T2}, insight}`.

Cycle (automated agent, D3; each cycle logged to UpdateWork):

1. **Observe** — frontier nodes, last evidence, ancestor insights, current `M_best`.
2. **Ideate** — ≤ 3 children under one parent; each child changes **one axis** (so the insight is
   attributable). Pruned siblings are negative constraints.
3. **Select** — by expected gain vs evidence; never more than 3 pending.
4. **Dispatch** — T0 (replay-judge, all recorded histories) → T1 (corpus, must be all-green) → T2
   dev sweep. Stop at the first failing tier; that is evidence too.
5. **Backpropagate** — write `dev_score`, insight (what changed, what happened, why — one
   paragraph, causal), update ancestors' `insights.md`.
6. **Decide** — a node whose dev `O_f` beats `M_best` runs the **held-out** sweep; admitted only if
   `O_f(test)` improves. Prune subtrees falsified by an insight.

Budget: round one = 6 cycles or $200, whichever first; each T2 dev sweep ≈ $15–25. The
coordinator reports spend after every sweep and stops at 75 % with a HITL question.

Output: `M_best` strategy file, `strategy-router.json` (family → strategy, only where a family's
winner differs from `M_best` on held-out), and a short report.

## 10. Harness changes by tier

**T2 — `samples/TodoEval.Runner`** (extend, research-runner §4.2):
- `Variants[]` axis `{name, strategyFile}` → one `EvalHostProcess` per cell (host start/stop moves
  into the sweep loop); `ExtraArgs` rendered from the strategy file (compaction knobs, prompt path,
  checks, shadow pricing, window).
- `Tasks[]` axis replacing the single `task.md`; per-run workspace copy; `check.ps1` invoked after
  the terminal poll; J0–J4 in `MetricsExtractor` (new `CompactionScore`).
- Baseline pairing by (task, seed) to the `off` cell; `SweepComparison` refuses on variant-set,
  prompt-hash, shadow-pricing-hash or judge-version mismatch.
- `--Compaction:*` cells keep `SandboxGateway:AutoSpawn` **on** (todo-eval turns it off).

**T1 — `tests/LmMultiTurn.Tests/Compaction/Corpus`**: new scenarios `n`–`s`: open Question across
a cut (m1 shape), outbound question answered after a cut, skill reloaded ×3, same file read ×4,
shell error retained, cache-write emission; `ScriptedProvider` emits
`cache_creation_input_tokens` and the pricing resolver uses `Additive` where the rates are
Anthropic-shaped. Fingerprint manifest regenerated deliberately.

**T0 — new `samples/CompactionEval.Replay`** (console): input = an archived `conversations/`
directory + a strategy file; rebuilds `SequencedHistory.FromPersisted`, runs the runtime checks and
`CheckpointPipeline` at the cut the real run took (from `compaction.state`), validates V1–V10,
then a checkpoint judge (deterministic: every open exchange, every human instruction, every
artifact path present; plus a J2-style rubric on the narrative). Cost = one summary call per
history. Recorded histories come from T2 runs (redacted archives keep tool results, which is what
the summarizer sees).

## 11. Runtime changes (all option-gated, default = today)

| Change | Where | Gate |
|---|---|---|
| `SummaryPromptPath` | `CompactionOptions`, `ProviderCheckpointSummarizer` ctor takes the prompt text | null = built-in |
| Tool-knowledge registry + RC1/RC2/RC6 | `CompactionOptions.ToolKnowledge`, `ToolResultView` pre-pass | `Checks.*` false |
| RC3 `OpenExchanges` + V10 | `ManifestAssembler`, `CompactionCheckpointMessage.Manifest`, `CheckpointValidator`, envelope render | `Checks.Rc3OpenExchanges` |
| RC5 `Describe` arm | `ProviderCheckpointSummarizer` | always (pure rendering improvement; covered by T1) |
| `CachedPrefixCheckpointSummarizer` | new class; `CompactionSetup.Summarizer` selection | `SummaryPrefixMode = Cold` |
| MCP hints → registry seed | `McpClientFunctionProvider` | registry absent = no-op |
| `UsageRecord` model id for compaction telemetry | `CompactionPayload.summary_model` | additive field |

Schema note: the manifest gains a section ⇒ `CompactionCheckpointMessage` schema version bump
(follow-up #777 already tracks the bump owner).

## 12. Delivery order

1. **Spec approved** (this document).
2. Runtime: `SummaryPromptPath`, registry + RC1/RC2/RC6, RC3/RC5/RC7, prefix summarizer — each
   with unit tests; T1 scenarios `n`–`s` green.
3. T2 harness: variants/tasks axes, checkers, judge J0–J4, shadow pricing, baseline pairing.
4. Task suite c1–s2 with fixtures and checkers; dry run each task once on `off` (validity only).
5. Prompts v0–v4; T0 replay tool; record 6 dev histories from the dry runs.
6. Coordinator brief + tree bootstrap (root = `off`, first children = 4 strategies); run round one
   within budget; ReviewPlan on the resulting router before anything ships.

## 13. Risks

- **Copilot rate limits / concurrency** — no documented limit in the repo (research-copilot §4).
  Behind a 429 there is only `RetryOptions.Default` (3 attempts, 1-2-4 s) and the Responses
  factory has no time-to-first-byte bound (5 min HTTP default). Round one runs
  `MaxParallelRuns=2`; a run that ends in a retry-exhausted 429 or a transport timeout is a J0
  validity failure — re-run, never scored. `gpt-5.x` is plan-gated (`billing.restricted_to`);
  the enterprise seat used here qualifies, verified by the live Luna/Terra logs.
- **Noise** — Avg@3 per cell; small effects (< 10 pp pass rate) are reported as "not separable".
- **Shadow prices are assumptions** — every cost figure is labelled; the Luna-vs-Terra verdict is
  conditional on the ratio between their assumed rates, and the report prints the break-even ratio.
- **Fake windows** — clamping changes when compaction fires, not what it keeps; T2 also runs one
  unclamped cell per admitted strategy before merge.
- **Judge model = a model under test** — J2 is pairwise and position-swapped; J1 is the gate, J2
  never admits on its own.
- **Held-out contamination** — held-out tasks are never used for ideation; their scores are read
  only at the merge gate.
