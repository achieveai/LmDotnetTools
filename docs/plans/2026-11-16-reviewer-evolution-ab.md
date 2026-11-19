> Approved 2026-11-16 (ReviewPlan rev 3; standing approval: $300 pilot cap). Below is the approved plan, with
> edits since approval where the build changed it: these notes, key dot 2 (no variant key), build item 2, the
> manifest and the commands under "How an arm is built", and the line under "What exists today".
>
> **Slice 1 as built** (`review run|batch --shadow --variant`): the shadow half of build item 2 only.
> - The receipt key does **not** gain the variant: about ten readers parse that key. Each arm runs on its own `--db`
>   (and its own workspace and store clone) instead; the receipt records `shadow` and `variant`.
> - An executed run pins its `--db` to its arm (`reviewd/arm`, one insert, so a live and a shadow run that start
>   together cannot both claim it); a run of another arm is refused there. A shadow arm also refuses an unpinned
>   `--db` that already holds live reviews or a live batch queue. A batch keeps the arm in its queue scope.
> - Shadow is Azure DevOps only: read-only egress cannot stop a GitHub post, so `--shadow` refuses GitHub.
> - Shadow conversations are titled `shadow-{repo}-{pr}-{head8}` and skip both title lookups, so live and shadow
>   never reuse each other's conversations and a shadow run reviews heads the live reviewer already did.
> - Every shadow step, prepare included, runs in `--post-session`, a session made by
>   `sandbox create --read-only-egress --read-repos <repos>` (the host keeps a marker listing the repos; the gateway
>   does not report rules back), and so do a shadow batch's eligibility checks and slot sweeps. A batch sweep, an
>   operator `reap-slots` and a `reclaim-slot` on a shadow `--db` each skip or refuse a session without the marker,
>   so a restart that drops `--shadow` cannot reset the arm's slots with full egress. The shadow agent can edit the
>   workspace, so in a full-egress session those steps could be made to post. Read-only egress keeps ADO GET and
>   HEAD, plus POST to exact paths only: each listed repo's git fetch (`git-upload-pack`; push is
>   `git-receive-pack`) and prepare's two read queries (`workitemsbatch`, `pullrequestquery`), which would otherwise
>   fail into warnings and leave the shadow arm thinner context than the live one.
> - Verify checks `would_post.json` (recorded as `wouldPost`, never `verifiedPublication`), and refuses one over
>   1 MiB, which commit would set aside; commit keeps the review branch local (no push).
> - Slice 1 left shadow reviews unjudged (`judge run` refused them). Slice 2 judges them; see its note.
> - Merged-PR replay is not supported yet: `review run` admits open PRs only (`PrSelection.cs`), so slice 1
>   shadows open PRs. Build item 5 adds historical heads.
>
> **Slice 2 as built** (`variant up`, arm checks, judging shadow reviews): build items 1, 3 and the judge half of 6.
> - The manifest, as `variant up` reads it (`mode.file` and `taskPrompt` are paths relative to the manifest;
>   `reviewRepo.directory` is the workspace directory name, used as written; `sha`s are 40-character commits):
>   ```json
>   { "id": "b-007",
>     "reviewRepo": {"repo": "AchieveAi_reviews", "ref": "exp/b-007", "sha": "<commit>", "directory": "exp-b"},
>     "plugins": {"alias": "cr-b-007", "url": "https://github.com/gautam-achieveai/ClaudePlugins", "sha": "<commit>"},
>     "mode": {"name": "code-review-b-007", "file": "modes/b-007.json"},
>     "taskPrompt": "prompts/b-007.md", "provider": "<provider>", "effort": "xhigh" }
>   ```
>   The mode file is a JSON chat-mode body: `systemPrompt` and any optional field the host keeps, with names in any
>   letter case (`env` and `subAgentModelIntelligenceByType` keys are data and match exactly). `variant up` reads the
>   saved mode back and refuses a file the host stores otherwise (a field it does not keep, a number written as a
>   string), because every run would refuse that arm.
> - Build an arm (preview without `--execute`):
>   `variant up --manifest <manifest> --experiment <id> --from-workspace <base workspace> --champion <commit> --execute`.
>   It never moves the clone: it refuses one in `reviewRepo.directory` that is not at `reviewRepo.sha`, or whose
>   `KnowledgeBase/` tree differs from the champion commit's, before it changes anything. Then it registers the alias at
>   `plugins.sha`, creates or replaces the mode (a replace clears every field the file omits), and creates or updates
>   workspace `variant-<id>`, taking `code-reviewer` from the alias only. `--champion` is given per run, but an
>   experiment has one champion: every arm must pass the same one. Each receipt records its clone's `KnowledgeBase/`
>   tree id (`knowledgeBaseTree`), so `compare` can refuse arms whose trees differ.
> - Run it, with the workspace and mode `variant up` printed:
>   `sandbox create --workspace <directory> --callback <url> --read-only-egress --read-repos <org/project/repo,...>`,
>   then `review batch --selection <json> --db <arm db> --session <session> --workspace <workspace> --provider <provider>
>   --mode <mode> [--reasoning-effort <effort>] --shadow --post-session <read-only session> --variant <id>
>   --manifest <manifest> --experiment <id> [--task-prompt-file <file>] --execute`.
> - Every run checks, on the host, that its workspace and mode are the ones `variant up` built for the manifest
>   (directory, reviewer plugin source, mode name and settings) and, when it executes, that the clone is still at
>   `reviewRepo.sha`. A resume must keep the arm it started with (task prompt, manifest, experiment, mode file), and a
>   batch that would resume an attempt of another arm is refused at start.
> - Judging: each arm is judged in its own workspace, so its `KnowledgeBase/` must equal the champion's (the tree check
>   above) instead of the judge reading the champion's store. `judge run` scores a shadow review's `would_post.json`
>   blind (arm labels stripped), only in a read-only session; `judge batch` skips a shadow review whose target session
>   is not read-only. `judge close` refuses a shadow judgment and both judge ticks skip it: it is a score, never a
>   lesson.
# Plan: Reviewers that improve themselves, proven by blind A/B against the judge

**Done when:** one challenger, generated from judge misses and bound to a `ClaudePlugins` branch, has been
reviewed in shadow on held-out merged PRs next to the current reviewer, judged blind, compared
per PR, and given a recorded promote / park decision.

## Key dots
1. **One arm = one variant manifest.** It pins every lever: review-repo branch, `ClaudePlugins` ref, chat mode, task prompt, model.
   `reviewd variant up` turns it into its own workspace. Champion and challenger are built the same way. (See "How an arm is built".)
2. **Shadow reviews.** Both arms review in shadow: never post (read-only egress), save would-post comments in each arm's own `--db` (receipts record `shadow` and `variant`).
3. **Blind, frozen judge.** One champion per experiment: every arm reviews and is judged with the champion's knowledge base (its own clone must hold the same `KnowledgeBase/` tree), with the same prompt and model, and labels hidden.
4. **A/A pilot first.** The champion's manifest, run twice on ~12 merged PRs. It measures noise and cost, which sets the sample size.
5. **First A/B, two ideas:** (H1) feed the knowledge base into reviews, which today read none of it; (H2) one skill edit, drawn from judge misses. Then H1+H2.
6. **Evolve loop driven by `experimentation:research`** (checked: it helps, see Reference). Each round:
   1. Recall from the study store.
   2. Write hypotheses, each with a prediction and a result that would disprove it.
   3. Write up to 3 candidate manifests.
   4. Run on dev PRs, then held-out PRs.
   5. Check it was not a setup fault.
   6. Save the lesson and decision (`research-learning`).

   Code enforces the steps: no batch runs without an experiment record.
7. **Promotion.** Win on held-out PRs → live shadow on new PRs → you approve merging each changed layer (store branch, plugin branch, mode).

## Your decision
**A standing approval for the loop:** a spend cap per round, plus pushing `exp/*` branches to `ClaudePlugins` (public) and to the review repos (private).
- Recommended: **$300 cap for the pilot**; later caps set from the pilot's measured cost per PR.
- Safe default: build only. Nothing runs or is pushed until you set a figure.

## Proof
- A/A pilot: the two runs of the same manifest differ only within noise, so the setup is fair.
- Zero PR comments from shadow runs (egress log).
- Every receipt shows the manifest hash and each pinned SHA (store, plugin, mode, task prompt).
- A per-PR results table for completeness and accuracy, with an interval and a decision record.

## Material risks
- **Gaming the judge.** Mitigation: the judge is frozen and from a different model family; validated-issue counts outrank 1–5 scores; you spot-check 5 judgments per round.
- **The judge grades with lessons it wrote.** The judge proposes KB lessons and then reads the KB. Mitigation: the judge's KB is pinned to before the round, and its own new lessons are excluded.
- **Leakage from history.** A replayed PR can see later commits or lessons learned from itself. Mitigation: date and source-PR filters, plus a trace audit.
- **Private knowledge on a public branch.** Repo facts go only to the private review-repo branch. Only general method goes to `ClaudePlugins`.
- **Disk and setup time.** Each arm needs its own review-repo clone with its own source pool. Start with 2 clones and run candidates one after another.
- **Noise.** It may need ~80+ PRs to see small gains. The pilot tells us before we spend.

**Now / next:** now, this plan. Next, build dots 1–2 and run the A/A pilot (thin slice) in a worktree.

---

## Reference

### Does `experimentation:research` help? (checked 2026-11-15/16)
**Verdict: yes, as the loop's memory and discipline. It is not the runner.** It does not run reviews, compute stats or judge: Reviewd code does that.

1. **Store check (passed).**
   - `research-store.mjs` (Node 24) held a linked hypothesis → experiment → evidence → lesson → decision chain. `validate` returned `valid: true`, and `context` pulled in the whole chain.
   - The schema is strict: an extra `manifest` field was rejected (`unknown field manifest`). So manifests live in `artifacts/` and records cite their path plus hash.
2. **Blind A/B of loop designs.**
   - Setup: one shared brief, 4 agents (2 told to use the skill, 2 forbidden), shuffled labels.
   - The judge scored them on 10 criteria written before any design existed: with skill **19, 19**; without **16, 16**.
   - The pre-registered rule (gap ≥2, no loss on holdout / confounds / noise) → **helps**.
   - The gain is in testable predictions, keeping null results, and testing combinations.
   - The flaw it prevented: both no-skill designs **rejected anything still unclear and never retried it**, which discards real small gains.
3. **Limits.**
   - Only 2 designs per arm and 1 judge from the same model family.
   - I wrote the criteria after reading the skill, so some may echo its wording.
   - The design checks plan quality, not reviewer scores. The real test is the loop itself: after 3 rounds, count the decisions the skill changed (setup faults caught, held-out reuse blocked, parked ideas reopened).

**Ideas taken from the four designs:**
- Check traces that injected KB entries were actually read before calling a hypothesis false.
- Screen each candidate on PRs with its target miss class plus control PRs, to catch new false findings.
- Set the number of PRs per metric from the A/A noise, and Holm-correct when several arms run at once.
- Retire a held-out PR once a lesson cites it, and refill the held-out set from new PRs.
- Record the Reviewd code commit (harness version) in every receipt. All four designs missed this.

**How the skill is wired in:**
- The round lead loads `experimentation:research`.
- The curator uses `research-learning`.
- The study store is the only state between rounds.
- `variant up` and `review batch --variant` refuse to run without an experiment record id.
- `compare` writes the evidence record itself, so results cannot be skipped or rewritten.

### How an arm is built (champion A′ and challenger B use the same recipe)
A review today stacks 7 layers. Each one has a different home, so each needs its own way to vary per arm:

| # | Layer | Home today | How an arm pins it | Exists? |
| --- | --- | --- | --- | --- |
| 1 | Global skills and agents (`code-reviewer`) | `ClaudePlugins` marketplace | `exp/<id>` branch → app marketplace alias pinned to its SHA → workspace `--marketplaces`, `--enabled-plugins` | Yes, except a register command |
| 2 | Reviewer system prompt | System chat mode `code-review-daemon` in host `Prompts.yaml` | A user chat mode per variant (`POST api/chat-modes`, copy + edit) → `review run --mode <id>` | Yes (`ReviewRunner.cs:453`) |
| 3 | Per-run task prompt + `ReviewQualityRules` | Inline C# | `--task-prompt-file` template; its SHA in the receipt | **No, build** |
| 4 | Repo skills (`review-setup`, `bluebird`, …) | Review repo (e.g. AchieveAi_reviews) `.claude/skills` | The arm's own clone of the review repo, checked out at `exp/<id>` → `workspace create --directory` | Yes (`WorkspaceCommands.cs:148-156`) |
| 5 | Repo `CLAUDE.md` | Same review repo | Same branch as layer 4 | Unverified that the host loads it |
| 6 | Knowledge base | Same review repo `KnowledgeBase/` | Same branch as layer 4, plus a "Prior knowledge" block injected (H1) | Injection: **No, build** |
| 7 | Model + effort | CLI flags | `--provider`, `--reasoning-effort` | Yes |

**The variant manifest** (one small JSON per arm, kept in the study store):
```json
{ "id": "b-007", "reviewRepo": {"repo": "AchieveAi_reviews", "ref": "exp/b-007", "sha": "…", "directory": "exp-b"},
  "plugins": {"alias": "cr-b-007", "url": "https://github.com/gautam-achieveai/ClaudePlugins", "sha": "…"},
  "mode": {"name": "code-review-b-007", "file": "modes/b-007.json"},
  "taskPrompt": "prompts/b-007.md", "provider": "…", "effort": "xhigh" }
```

**`reviewd variant up <manifest>`** (new, thin wrapper over calls that already exist):
1. Register or refresh the marketplace alias at the SHA (`RegisterAppMarketplaceAsync`).
2. Point the arm's review-repo clone at `ref` and check the SHA. Refuse if any slot is busy.
3. Create or update the chat mode from its file.
4. Create or update the workspace: the clone directory, `[cr-<id>, sandbox-plugins]`, and `code-reviewer` enabled from the arm's alias only.
5. Print the workspace id and the manifest hash.

As built, step 2 only checks the clone (the operator checks out `ref`), and step 5 also prints the mode id. The full
`variant up`, `sandbox create` and `review batch` commands are in the "Slice 2 as built" note at the top.

**Clones:** there are two experiment clones of each review repo, separate from the production store.
- `exp-A` stays at `main`: the champion.
- `exp-B` is switched to each candidate's branch.
- Production stays untouched. Shadow artifacts land on the experiment branches, never on `main`.
- A plain clone, not `git worktree`: the slot state lives under `/workspace/.git/review-setup` (`RUNBOOK.md:256`), which needs a real `.git` folder.

**Promotion by layer:**
- Plugin branch → PR into `ClaudePlugins` `master`.
- Review-repo branch → merge into the store's `main`. Also back-port to `review-host-kit/skills` when the skill comes from the kit (`RUNBOOK.md:38-39`), or the next deploy overwrites it.
- Mode → update the system mode in `Prompts.yaml`.
- Task prompt → becomes the default template.

### What exists today (verified)
The baseline before slice 1, kept as approved. Slice 1 changed the "A/B in Reviewd" row (see the note at the top).

| Area | State | Evidence |
| --- | --- | --- |
| Judge | `reviewd judge` scores merged PRs on 8 dims (1–5) incl. completeness and accuracy, plus foundCapture recall and true/false/unproven counts | `Prompts/merged-judge.md:319-350,429-471` |
| Judge input | Needs a `completed-verified` **posted** review; runs in the review's workspace | `JudgeRunner.cs:198-215` |
| Judge identity | Commit, heads, prompt SHA, provider, effort | `JudgeRunner.cs:233-242` |
| Overall score | Model-computed mean, never re-checked in code | `merged-judge.md:469-471` |
| A/B in Reviewd | None. One receipt per PR, no variant, every review posts | `ReviewRunner.cs:459`, `CliOptions.cs:65-82` |
| Mode per run | `--mode` overrides the config mode at provision | `ReviewRunner.cs:453,1037-1047` |
| Custom modes | CRUD API `api/chat-modes`; TodoEval already creates one per eval | `EvalHostClient.cs:80-121` |
| Workspace folder | `workspace create --directory`; the sandbox `/workspace` is the review repo; skills load from `/workspace/.claude/skills` | `WorkspaceCommands.cs:148-156`, `ReviewRunnerSandbox.py:177-187` |
| Branch marketplace | **Partly.** App marketplace `{url, ref}` pins a branch/tag/SHA; a workspace selects aliases | `SandboxAppMarketplace.cs:23-27`, `Workspace.cs:30` |
| Branch caveats | Branch pin does not follow new commits; plugin refs carry no ref; no `reviewd` register command | `SandboxClient.AppMarketplaces.cs:113-115`, `SandboxPluginRef.cs:9-20` |
| Repo public? | `gautam-achieveai/ClaudePlugins` is public (needed for app marketplaces) | WebFetch of GitHub page, 2026-11-14 |
| KB in live review | **Not read.** Mode prompt forbids KB unless a block is supplied; Reviewd supplies none; path `/workspace/store/` vs `/workspace/` mismatch | `Prompts.yaml:308-319`, `JudgeSandbox.py:638` |
| KB writes | `judge close` appends lessons, `Improvements/` ledger, author habits | `JudgeClose.cs:556-692` |
| `Improvements/` | Write-only; nothing reads it | grep: only `JudgeClose.cs`, `CliOptions.cs` |
| Skill use | Reviewd prompt never names `code-reviewer:pr-review`; it relies on the mode prompt's "prefer a skill" | `ReviewRunner.cs:1111-1126`, `Prompts.yaml:214-216` |
| Constraint | No per-author history into the reviewer (ADR 0018). Reviewer knowledge is repo/team level only | `docs/adrs/0018-*.md:59-70` |

### Experiment design
- **Arms per PR:** A′ = the champion manifest in shadow (`exp-A`), B = the challenger manifest in shadow (`exp-B`). Compare B vs A′. The live posted review A stays as-is.
- **Why A′, not A:** posting changes the author's response and the judge's evidence. Shadow vs shadow is fair; A vs A′ gives the noise floor.
- **Judging:** copy each arm's review folder into the `exp-A` store under a blinded name, and judge it there. That way the judge's knowledge base and skills never come from the challenger.
- **Corpus:** merged PRs that already have judgments. Split by hash of the PR id, stratified by repo: 60% dev (the mutator may read it), 40% held-out (the mutator never sees it). Refresh the held-out set from new PRs; do not tune on it repeatedly.
- **Primary metrics (your priority):**
  - Completeness = foundCapture (recall over validated issues), and the 1–5 completeness score.
  - Accuracy = false and unproven findings per review, and the 1–5 accuracy score.
- **Decision rule (lexicographic):**
  1. Guardrails: hallucinations not up; cost ≤ 1.3× champion.
  2. Accuracy not worse than the A/A noise margin.
  3. Completeness gain, with the paired 90% bootstrap interval above 0.
  4. Overall score not lower.
  - With several arms at once, Holm-correct the intervals.
  - Otherwise park, with a reopening condition. A null result is not a loss: never "reject on unclear".
- **Dims that need posting** (threadHelp, fix rate) are N/A for both shadow arms.
- **Sample size:** set from the A/A variance. A rough guide: paired SD ≈1 on the 1–5 scale needs ~85 PRs to detect +0.3. A larger effect, or count metrics, needs fewer.
- **One layer per candidate** at first, so a result can be tied to a cause. Combine layers only when two have each helped and a reason says they fit (A, B, A+B).
- **Search policy:** keep a small population, not one winner. Keep candidates that win on different PR families (repo, size, language). GEPA-style revision from judge traces; Arbor-style linked records.

### Smallest build list (Reviewd.Sample)
1. `reviewd variant up <manifest>` (above), including `marketplace register`, which calls the existing SDK `RegisterAppMarketplaceAsync`.
2. `review run|batch --variant <id> --shadow`: the receipt key keeps its shape and each arm runs on its own `--db` (see the note at the top); read-only egress; write `would_post.json` instead of publishing.
3. `--task-prompt-file`: the inline task prompt and `ReviewQualityRules` become a template; its SHA goes in the receipt.
4. Receipt provenance: the manifest hash plus every SHA (store, plugin, mode, task prompt), the Reviewd code commit, model, effort, and the experiment record id.
5. Historical head for merged PRs (`--at-head <sha>`). The slot hides refs newer than the review time.
6. `judge run --variant <id>`: judge in the arm's workspace, whose `KnowledgeBase/` equals the champion's; read `would_post.json`, hide arm labels.
7. Knowledge-base injection as a mode + task-prompt variant (H1); fix the `/workspace/store` path.
8. `compare` command: paired table + bootstrap from `judgment.json` files → a study evidence record. It refuses arms whose receipts record different `knowledgeBaseTree`s.

### Study store
- `.experimentation/reviewer-evolution/` in the review store (private, next to the judgments), managed with `research-store.mjs`. Assumption; easy to move.
- Records: manifests (as experiment arms), hypotheses (prediction + falsifier), experiments (arms, PR set, judge SHA, stop rule), evidence (results table path), lessons (conditions → action → mechanism), decisions.

### Loop roles (when it runs unattended)
- **Mutator:** reads dev-set judge misses and the `Improvements/` ledger. Picks the layer the miss points at (knowledge → KB, process → repo skill or mode, method → global skill). Writes one branch or file plus a manifest. Never touches the judge.
- **Runner:** `variant up`, shadow reviews and judging, within the round's cap.
- **Curator:** records evidence and lessons; proposes promotion. **You approve promotions.**

### Still unverified
- The gateway submodule is not checked out, so it is unknown whether two commits of one repo can be mounted at once.
- Whether the host loads the review repo's `CLAUDE.md` into the reviewer (layer 5).
- Whether a merged ADO PR can be re-reviewed at its old head. Not without code changes: `review run` admits open
  PRs only (`PrSelection.cs`), so this needs build item 5.
- The plugin name clash when two marketplaces both expose `code-reviewer`: the workspace must enable only one.
- Whether live Reviewd reviews actually invoke `code-reviewer:pr-review`. To check from traces in the pilot.
- How much disk and time a second review-repo clone and its source pool take on the host.
