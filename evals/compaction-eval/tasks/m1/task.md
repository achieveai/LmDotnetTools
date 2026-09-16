# m1 — collaboration: a worker's question must be answered after the lead's own compaction

Family `collab`, split `dev`. Workspace = `fixtures/` (`specs/*.md`, 8 specifications the lead must read
in full, ~36k words; `workstreams/<name>/brief.md`, 3 briefs the workers read). Three values live only in
the lead's brief; each worker needs one and must ask. The lead reads the whole spec corpus (~45k tokens
of tool results) right after dispatch, so its cut lands while the questions are open (RC3 target), and
the gate ids it extracted must survive the cut into `gates.md`, the release plan and `summary.md` (R3).
Checker: every worker's `config.txt` carries the right value; `gates.md` names the active gate per spec;
`summary.md` lists all three values and the workers' handoff codes.

---

Project code name: {SEED}.

You are the release lead. Run this with three sub-agents on the shared todo board, one per workstream (`gateway`, `storage`, `pipeline`), and do your own reading and writing job in parallel.

Private configuration — these values are known only to you. Do NOT put them in any sub-agent's brief. A worker that needs one must ask you for it, and you answer when the question arrives:

- Workstream "gateway": listen port = 8443
- Workstream "storage": region = eu-west-2
- Workstream "pipeline": build tag = r-{SEED}-7

Setup: lay out the board first — one main task per workstream with these four items beneath it: (1) read `workstreams/<name>/brief.md` in full (the whole file), (2) write `workstreams/<name>/plan.md` with five numbered steps for that workstream, (3) write `workstreams/<name>/config.txt` containing exactly one line `<key>=<value>` where the key is `port`, `region` or `build_tag` and the value is the one from the private configuration — the worker must ask you for it, (4) write `workstreams/<name>/report.md` of at least 400 words describing what it did, ending with the line `handoff: <code>` where the code is the handoff code for THIS cycle stated in its brief (the brief also names a void code from the previous cycle; that one must not be reported). Then dispatch one sub-agent per workstream, telling it its workstream name, its board items, and that item 3's value must be requested from you.

Your own job while they work, in this order:

1. Read every file under `specs/` in full with the Read tool (eight component specifications; the whole file each time, not head or grep — each spec also names retired gates, and only the surrounding text tells them apart from the active one). Each spec declares exactly one ACTIVE release gate in a sentence that starts `Release gate GATE-nn:`. Write `gates.md` with one line per spec file, in file order: `<file>: GATE-nn — <the gate's condition as written>`.
2. Write `release-plan.md` in six sections — Scope, Risks, Rollout, Rollback, Communication, Sign-off — each at least 150 words, added one section at a time (read the file before each addition, then append). The Scope section must cite all eight active gate ids.
3. Between the steps above, check for and answer any worker questions.

When every worker has reported done, read each `workstreams/<name>/report.md` in full, review the board, then write `summary.md` with one line per workstream: `<name>: <key>=<value>; handoff=<code>` — the value from your private configuration, the code from that worker's report. Reply with that summary.
