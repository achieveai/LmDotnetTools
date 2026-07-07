# Layer-2 Knowledge Base — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** At PR-close, distill durable knowledge from a PR's accumulated review notes into a layered, queryable `KnowledgeBase/`; reviews consult it.

**Architecture:** Extend Layer-1's merge-on-close sweep with a gated `KnowledgeExtractionAgent` (host-side, over the notes checkout) that writes/updates layered markdown entries + a `_index.jsonl` + regenerated `_toc.md`; the review agent consults `KnowledgeBase/` in its mounted store checkout. Evolve the existing `KnowledgeAgent`/`KnowledgeTableOfContents`; do not duplicate.

**Tech Stack:** .NET 9, C#, xUnit + FluentAssertions, `ISandboxFileSystem`, `MultiTurnAgentLoop`.

## Global Constraints
- Common code in `samples/CodeReviewDaemon.Sample` only (no `src/…`); types `internal`. Evolve files in place — NO `*-v2`/`*-enhanced` files.
- `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`: zero warnings; collection-expressions `[...]`; `Lock` not `object`; IDE0300 is an error.
- No Co-Authored-By / no AI signature in commits/PRs. Never `--no-verify`; if husky missing: `dotnet tool restore && dotnet husky install`.
- Build `dotnet build samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj -c Debug`; test `dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj -c Debug`. Full suite green each task.
- The daemon owns all writes; extraction runs host-side (write credential), never the sandbox runner. `updated` dates are injected by the daemon, never the model (determinism).
- Spec: `docs/superpowers/specs/2026-07-06-daemon-knowledge-base-design.md`.

---

## Parallelization (for the fan-out)
- **Group A (parallel, disjoint files):** Task 1 (`KnowledgeIndex`, new file), Task 2 (`KnowledgeTableOfContents` layered), Task 3 (review KB-consult: `DaemonAgentFactory` review prompt + executor `_toc` injection). Merge clean.
- **Then Task 4** (`KnowledgeExtractionAgent`, consumes Task 1 + 2 + a new `DaemonAgentFactory` knowledge prompt).
- **Then Task 5** (sweep integration + drop per-review arm + `Program.cs`, consumes Task 4).

---

## Task 1: KnowledgeIndex (`_index.jsonl` + frontmatter)

**Files:** Create `samples/CodeReviewDaemon.Sample/Agents/KnowledgeIndex.cs`; Test `tests/CodeReviewDaemon.Sample.Tests/Agents/KnowledgeIndexTests.cs`.

**Interfaces — Produces:**
- `internal sealed record KnowledgeEntryMeta(string File, string Title, IReadOnlyList<string> Tags, string Scope, IReadOnlyList<string> SourcePrs, string Updated);`
- `internal static class KnowledgeIndex`:
  - `static KnowledgeEntryMeta? ParseFrontmatter(string relFile, string entryMarkdown)` — parse a leading `---`…`---` YAML block (title, tags, scope, sourcePrs, updated); return null when there is no frontmatter block.
  - `static string RenderIndex(IReadOnlyList<KnowledgeEntryMeta> entries)` — one compact JSON object per line (`System.Text.Json`), stable key order (`file,title,tags,scope,sourcePrs,updated`), sorted by `File` ordinal.

**Steps:**
- [ ] **1. Failing tests:** (a) `ParseFrontmatter` on a well-formed entry (`---\ntitle: X\ntags: [a, b]\nscope: system\nsourcePrs: ["github/o-r/42"]\nupdated: 2026-07-06\n---\n# X\nbody`) → the exact `KnowledgeEntryMeta`; (b) no-frontmatter markdown → `null`; (c) `RenderIndex([m1,m2])` → two lines, each valid JSON with the six keys, sorted by `File`; round-trip: each rendered line's `title` matches. Run: `dotnet test … --filter "KnowledgeIndex"` → FAIL.
- [ ] **2. Implement** (hand-rolled minimal YAML-frontmatter reader — only the flat scalar/list keys above; do not add a YAML dependency). `RenderIndex` via `JsonSerializer` with a fixed property order (use an explicit ordered object/`JsonObject`).
- [ ] **3.** Run the filter + full suite → PASS. **4. Commit** `feat(daemon): knowledge-base entry index (_index.jsonl) + frontmatter parse`.

---

## Task 2: Layered KnowledgeTableOfContents

**Files:** Modify `samples/CodeReviewDaemon.Sample/Agents/KnowledgeTableOfContents.cs`; Test `tests/CodeReviewDaemon.Sample.Tests/Agents/KnowledgeTableOfContentsTests.cs` (extend existing if present).

**Context:** Today `KnowledgeTableOfContents.Render(IReadOnlyList<KnowledgeEntry>)` renders a flat list. The KB is now layered: entries live under `system/` and `<repo>/`. `KnowledgeEntry` currently is `(FileName, Title)`.

**Interfaces — Produces:** keep `Render(IReadOnlyList<KnowledgeEntry>)` but have `KnowledgeEntry` carry a relative path incl. its scope dir (`internal sealed record KnowledgeEntry(string RelPath, string Title)` — `RelPath` e.g. `system/x.md` or `LmDotnetTools/y.md`). `Render` groups by the first path segment (scope) under a `## <scope>` heading, entries as `- [Title](RelPath)`, scopes + entries sorted ordinal.

**Steps:**
- [ ] **1. Failing test:** `Render([("system/a.md","A"),("LmDotnetTools/b.md","B")])` → a `# Knowledge Base` doc with a `## LmDotnetTools` and `## system` section (sorted), each listing its `- [Title](relpath)`. Run filter → FAIL (signature/grouping).
- [ ] **2.** Update the record to `RelPath` + implement grouping. Update the one existing caller reference in `KnowledgeAgent` only if Task 2 lands before Task 4 (else Task 4 owns it) — if the field rename breaks `KnowledgeAgent`'s current flat call, adapt it minimally to pass `RelPath` (flat `name` → `name`). Keep the suite green.
- [ ] **3.** Filter + full suite → PASS. **4. Commit** `feat(daemon): layered (system/<repo>) knowledge ToC`.

---

## Task 3: Reviews consult the KB (retrieval)

**Files:** Modify `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs` (review system prompt); Modify `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` (inject `_toc` into the review input); Test the executor + a prompt assertion.

**Interfaces — Consumes:** nothing new. **Produces:** the review prompt contains a KB-consult directive; the review input contains the `KnowledgeBase/_toc.md` contents when present.

**Steps:**
- [ ] **1. Failing tests:** (a) `DaemonAgentFactory.CreateReviewProfile()` (find the real method) `.SystemPrompt` `Contains("KnowledgeBase")` and instructs consulting `_toc.md` + Grep/Read of relevant entries and calling out contradictions with known invariants; (b) the executor, when building the review input for a store-mode run whose checkout has `KnowledgeBase/_toc.md`, includes that ToC text in the input handed to the review agent (assert via the existing executor test fakes — a `_toc.md` seeded in the fake store is surfaced in the review input; absent `_toc.md` → input unchanged). Run → FAIL.
- [ ] **2. Implement:** append the KB-consult paragraph to the review system prompt (verbatim, terse). In the executor's review-input assembly (store mode), read `<ReviewRoot>/KnowledgeBase/_toc.md` via `ISandboxFileSystem` (best-effort; missing file → skip, no error) and prepend a `## Prior knowledge (KnowledgeBase/_toc.md)` block to the review input.
- [ ] **3.** Filter + full suite → PASS. **4. Commit** `feat(daemon): reviews consult KnowledgeBase (_toc injected + prompt directive)`.

---

## Task 4: KnowledgeExtractionAgent (gate + layered write + create-or-update + index)

**Files:** Modify `samples/CodeReviewDaemon.Sample/Agents/KnowledgeAgent.cs` (evolve); Modify `samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs` (knowledge extraction prompt with the gate); Test `tests/CodeReviewDaemon.Sample.Tests/Agents/KnowledgeAgentTests.cs` (extend).

**Interfaces — Consumes:** Task 1 `KnowledgeIndex`/`KnowledgeEntryMeta`; Task 2 layered `KnowledgeEntry(RelPath,Title)`.
**Produces:** `Task<KnowledgeWriteResult?> TryExtractAsync(string repoRoot, string notesInput, string sourcePrRef, string todayUtc, CancellationToken ct)` on the (renamed-in-purpose) knowledge agent — returns `null` when the model emits the gate sentinel `NO_KNOWLEDGE`; else writes/updates the entry and regenerates `_index.jsonl` + `_toc.md`.

**Steps:**
- [ ] **1. Failing tests (real agent over a scripted `IMultiTurnAgent` fake + `FakeSandboxFileSystem`):**
  - (a) **gate:** fake agent returns text beginning `NO_KNOWLEDGE` → `TryExtractAsync` returns `null`, writes NO entry, leaves `_index.jsonl`/`_toc.md` untouched.
  - (b) **create:** fake returns a well-formed entry (a `## SCOPE: system` or `## SCOPE: LmDotnetTools` line + `## TITLE: …` + body) → an entry file written under `KnowledgeBase/<scope>/<slug>.md` with daemon-injected frontmatter (`sourcePrs:[sourcePrRef]`, `updated: todayUtc`), and `_index.jsonl` + `_toc.md` regenerated to include it.
  - (c) **update/dedup:** with an existing `KnowledgeBase/system/x.md` (frontmatter `sourcePrs:[old]`), fake returns an entry the model marks as updating `x` (an `## UPDATES: system/x.md` line) → the existing file is updated and `sourcePrs` becomes `[old, sourcePrRef]` (no second near-duplicate file); index reflects one entry.
  - Run filter → FAIL.
- [ ] **2. Implement:** add `DaemonAgentFactory.CreateKnowledgeExtractionProfile()` whose prompt: consult the provided index/ToC; if nothing durable+generalizable, reply exactly `NO_KNOWLEDGE`; else emit `## SCOPE: <system|repo>`, `## TITLE: <title>`, optional `## UPDATES: <relpath>`, then the entry body (NO frontmatter — the daemon adds it). In `KnowledgeAgent`, parse those markers, resolve create-vs-update (update merges `sourcePrs` + rewrites body under existing slug/scope), inject frontmatter deterministically (`todayUtc`, `sourcePrRef`), write via `ISandboxFileSystem`, then regenerate `_index.jsonl` (via `KnowledgeIndex.RenderIndex` over all parsed entries) and `_toc.md` (layered). Remove the old flat `WriteEntryAsync` signature if unused, or keep it delegating — do not leave dead code.
- [ ] **3.** Filter + full suite → PASS. **4. Commit** `feat(daemon): gated layered knowledge extraction (create/update + index)`.

---

## Task 5: Extract at PR-close + drop the per-review arm + wire it

**Files:** Modify `samples/CodeReviewDaemon.Sample/Orchestration/PrLifecycleSweeper.cs` (Merged path runs extraction before merge); Modify `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs` (remove `RunKnowledgeArmAsync` per-review call); Modify `samples/CodeReviewDaemon.Sample/Program.cs` (wire the extractor into the sweeper behind `EnableKnowledgeAgent`); Test `tests/CodeReviewDaemon.Sample.Tests/Orchestration/PrLifecycleSweeperTests.cs` (extend).

**Interfaces — Consumes:** Task 4 `TryExtractAsync`. **Produces:** on a Merged PR, extraction runs over the PR's notes checkout **before** `MergeToDefaultAsync`; Open/Abandoned unaffected.

**Steps:**
- [ ] **1. Failing tests:** (a) `PrLifecycleSweeper.SweepAsync` with a Merged PR + an extraction delegate → the extraction delegate is invoked for that PR BEFORE `MergeToDefaultAsync` (assert ordering via a recording delegate + the fake runner's command order); (b) Abandoned/Open → extraction NOT invoked; (c) extraction throwing → logged + the merge STILL proceeds (never blocks lifecycle). Run → FAIL.
- [ ] **2. Implement:** give `PrLifecycleSweeper` an optional `Func<ReviewedPr, CancellationToken, Task>? extractKnowledgeAsync` seam; on the Merged branch, `try { await extractKnowledgeAsync(pr, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { log; }` before `MergeToDefaultAsync`. In `DaemonReviewStageExecutor`, delete the `if (_options.EnableKnowledgeAgent) RunKnowledgeArmAsync(...)` per-review call (and `RunKnowledgeArmAsync` if now unused). In `Program.cs`, when `EnableKnowledgeAgent`, build the extraction delegate: on the sweeper's host store checkout, read the PR's notes + `_index.jsonl`, call `TryExtractAsync(storeRoot, notesInput, prRef, todayUtc, ct)`; pass it to the sweeper.
- [ ] **3.** Filter + full suite → PASS. **4. Commit** `feat(daemon): extract knowledge at PR-close (sweep) + drop per-review arm`.

---

## Self-review notes
- Spec coverage: §1 trigger+gate → Task 4 (gate) + Task 5 (at-close). §2 storage → Task 1 (index) + Task 2 (ToC) + Task 4 (layered write/frontmatter/dedup). §3 retrieval → Task 3. §4 components → Tasks 1/2/4/5. §6 error handling → Task 5 step (extraction never blocks merge) + Task 4 gate. §7 testing → each task's tests. All covered.
- Type consistency: `KnowledgeEntry(RelPath,Title)` (Task 2) used by Task 4's ToC regen; `KnowledgeEntryMeta`/`RenderIndex` (Task 1) used by Task 4; `TryExtractAsync(repoRoot,notesInput,sourcePrRef,todayUtc,ct)` (Task 4) called by Task 5.
