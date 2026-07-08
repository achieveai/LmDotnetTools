# Layer-2 — Knowledge Base — Design

**Status:** approved (2026-07-06). Builds on Layer-1 (pooled/persistent/**writable** review workspace + merge-on-close sweep + the `AchieveAiReviews` store checkout mounted into the review agent — see `2026-07-05-daemon-pooled-review-workspace-design.md`).

**Goal:** At PR-close, distill *durable, generalizable* knowledge from a PR's accumulated review notes into a queryable, layered `KnowledgeBase/`; reviews consult it so the daemon improves over time.

**Scope boundary (agreed):** Layer-2 = **extraction (at close) + retrieval (reviews consume)**. **Layer-3** (separate, later) = the **judge** noting what a review *missed* + evolving the review *prompts*. Layer-2 does **not** touch the judge or prompt-evolution.

**What already exists (evolve, don't duplicate):** `Agents/KnowledgeAgent.cs` distills a review into `KnowledgeBase/{slug}.md` + regenerates `_toc.md` via `KnowledgeTableOfContents`; gated by `EnableKnowledgeAgent`; invoked per-review by `DaemonReviewStageExecutor.RunKnowledgeArmAsync`. Gaps Layer-2 closes: runs per-review not at PR-close; writes **flat** (store layout is layered `system/<repo>`); **markdown-only** (no queryable JSONL); **no durable-knowledge gate**; and reviews **never read** the KB.

---

## §1 Extraction — trigger + gate

Extraction runs at **PR-close**, on the merge-on-close path, host-side (write credential), on the store checkout — **before** the notes branch merges to `main`.

- Input: the PR's **accumulated notes** (`PRs/<provider>/<slug>/<pr>/…` on the notes branch) + the final review text.
- **Gate first:** the agent judges *"does this PR yield durable, generalizable knowledge worth remembering across future reviews?"* Most PRs → **no → write nothing** ("not every PR contributes"). Only reusable insights (a recurring pitfall, a cross-cutting contract, a non-obvious invariant) are written.
- If yes → write/update entries → the existing Layer-1 merge carries them into `main`.
- The current per-review `RunKnowledgeArmAsync` invocation is **removed** (extraction moves to close). `EnableKnowledgeAgent` keeps gating whether extraction runs at all.

## §2 Storage — layered + queryable

- `KnowledgeBase/system/<slug>.md` (cross-repo patterns) + `KnowledgeBase/<repo>/<slug>.md` (repo-specific). The extraction agent chooses `system` vs `<repo>` scope.
- Each entry: markdown with **YAML frontmatter** — `title`, `tags` (list), `scope` (`system`|`<repo>`), `sourcePrs` (list), `updated` (date, injected by the daemon, not the model — determinism) — then the body.
- **`KnowledgeBase/_index.jsonl`** — one JSON object per line `{file, title, tags, scope, sourcePrs, updated}` — the queryable index; regenerated from the entries actually present (like the ToC, so it never drifts).
- **`_toc.md`** — regenerated via existing `KnowledgeTableOfContents` (extended to walk the layered dirs).
- **Dedup/update:** the agent consults the index/ToC and **updates a related existing entry** (appending the new PR to `sourcePrs`, refining the body) rather than creating a near-duplicate. Slug is derived from the entry title (existing `Slugify`).

## §3 Retrieval — reviews consume the KB

- The **review profile's system prompt** gains a KB-consult directive: consult `KnowledgeBase/` in the checkout — `Read` `_toc.md`, then `Grep`/`Read` entries relevant to the changed files/topics — and factor prior knowledge into the review (and call out when the PR contradicts a known invariant).
- The compact **`_toc.md` is injected** into the review context (ToC only — scalable as the KB grows; full entries are pulled on demand by the agent).
- No new retrieval infra: Layer-1's slot mount makes `/workspace/store/KnowledgeBase/` directly `Read`/`Grep`-able; the **JSONL index tags** make the grep targeted.

## §4 Components (interfaces)

- **`KnowledgeIndex`** (new, pure/testable): parse an entry's frontmatter; read/write/regenerate `_index.jsonl` from the entries present. `Regenerate(IReadOnlyList<KnowledgeEntryMeta>) -> jsonl`; `Parse(entryMarkdown) -> KnowledgeEntryMeta`. All IO via `ISandboxFileSystem`.
- **`KnowledgeExtractionAgent`** (evolves `KnowledgeAgent`): `TryExtractAsync(repoRoot, notesInput, existingIndex, ct) -> KnowledgeWriteResult?` — runs the gate (returns `null` = nothing durable), else writes the layered entry (create-or-update), regenerates `_index.jsonl` + `_toc.md`. The gate + scope + update-vs-create live in the agent's prompt + a deterministic post-step.
- **`KnowledgeExtractionProfile`** in `DaemonAgentFactory` (new knowledge prompt with the gate + "emit `NO_KNOWLEDGE` when nothing durable" contract).
- **Sweep integration**: `PrLifecycleSweeper` (Merged path) invokes extraction over the notes checkout **before** `MergeToDefaultAsync`. Wired in `Program.cs` behind `EnableKnowledgeAgent`.
- **Review-prompt update**: `DaemonAgentFactory` review system prompt + the executor injecting `_toc.md` into the review input.

## §5 Data flow

reviewer writes notes during reviews (Layer-1, `PRs/<pr>/…`, writable slot) → PR merges → sweep runs `KnowledgeExtractionAgent` over the accumulated notes → **gate**: durable? → if yes, write/update `KnowledgeBase/{scope}/<slug>.md` + regenerate `_index.jsonl` + `_toc.md` → Layer-1 merges the notes branch (carries the KB into `main`) → future reviews consult `KnowledgeBase/`.

## §6 Error handling

Extraction failure (agent error, IO) **logs + skips** — it must **never block the merge/delete** (mirror Layer-1's degrade tiers: a capability gap degrades, never fails the lifecycle). "Gate emits `NO_KNOWLEDGE`" is the common, non-error path. Malformed frontmatter on an existing entry → skip that entry in index regen + log (don't abort).

## §7 Testing

Unit: the gate (durable input → entry written; `NO_KNOWLEDGE` → no file, no index change); layered scope selection (`system` vs `<repo>`); create-vs-update dedup (related entry updated + `sourcePrs` appended, not duplicated); `_index.jsonl` regenerate + `Parse` round-trip; `_toc.md` regen across layered dirs; frontmatter `updated` injected by the daemon; sweep runs extraction before merge on the Merged path only (not Open/Abandoned); review profile carries the KB-consult directive + the executor injects `_toc`. Live: a real PR-close writes/updates an entry under `KnowledgeBase/<repo>/`; a subsequent review's transcript shows it read the KB.

## §8 Global constraints

- Common code in `samples/CodeReviewDaemon.Sample` only (no `src/…`); types `internal`. Do not add "enhanced/v2" files — evolve `KnowledgeAgent` in place.
- `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`: zero warnings; collection-expressions `[...]`; `Lock` not `object`. No AI/Claude signature in commits/PRs. Never `--no-verify`.
- The daemon owns all writes/pushes; extraction runs host-side with the write credential (like Layer-1's commit-notes), never the sandbox runner.
