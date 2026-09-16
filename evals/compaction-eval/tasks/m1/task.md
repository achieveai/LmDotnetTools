# m1 — collaboration: a worker's question must be answered after the lead's own compaction

Family `collab`, split `dev`. Workspace = `fixtures/` (empty `workstreams/`). Three values live
only in the lead's brief; each worker needs one and must ask. The lead does a long writing job
between dispatch and the answers, so its cut lands while the questions are open (RC3 target).
Checker: every worker's `config.txt` carries the right value; the lead's `summary.md` lists all three.

---

Project code name: {SEED}.

You are the release lead. Run this with three sub-agents on the shared todo board, one per workstream, and do your own writing job in parallel.

Private configuration — these values are known only to you. Do NOT put them in any sub-agent's brief. A worker that needs one must ask you for it, and you answer when the question arrives:

- Workstream "gateway": listen port = 8443
- Workstream "storage": region = eu-west-2
- Workstream "pipeline": build tag = r-{SEED}-7

Setup: lay out the board first — one main task per workstream with these three items beneath it: (1) write `workstreams/<name>/plan.md` with five numbered steps for that workstream, (2) write `workstreams/<name>/config.txt` containing exactly one line `<key>=<value>` where the key is `port`, `region` or `build_tag` and the value is the one from the private configuration — the worker must ask you for it, (3) write `workstreams/<name>/checklist.md` with three verification checks. Then dispatch one sub-agent per workstream, telling it its workstream name, its board items, and that item 2's value must be requested from you.

Your own job while they work: write `release-plan.md` in six sections — Scope, Risks, Rollout, Rollback, Communication, Sign-off — each at least 150 words, added one section at a time (read the file before each addition, then append). Between sections, check for and answer any worker questions.

When every worker has reported done, review the board, then write `summary.md` listing each workstream's key and value on its own line as `<name>: <key>=<value>`, and reply with that summary.
