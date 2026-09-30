# fw1 — multi-hop questions over a fictional company wiki (synthetic)

Family `fictional-multihop`, split `dev`. Workspace = `fixtures/` (662 Markdown pages, 2.37 MB,
~591k tokens; mostly boilerplate). 10 questions, each 3-5 hops: incident -> service (prose alias)
-> owning team on a date (runbook table, then reorg memos, some postponed or withdrawn) -> on-call lead
that week (rotation table, then swaps agreed in meeting-note prose) -> person (nickname or "First L."
resolved within the team; near-duplicate twins elsewhere), or incident commander -> team on a date
(transfer memos) -> manager on a date (manager-change memos). Checker: normalised exact name match;
score = correct / 10. Trap flips and predicted trap-only scores: `hidden/build-report.json`.
Generator + knobs: `hidden/gen_world.py`, `hidden/build.json`.

---

Project code name: {SEED}.

This workspace is a copy of the internal wiki of Brannock Vale Freight, a (fictional) freight
company: 662 Markdown pages under `people/`, `teams/`, `runbooks/`, `incidents/`, `memos/`,
`meetings/` and `status/`. Nothing in it is about a real company or person, so the answers are only in
these pages; general knowledge will not help. Most of each page is routine boilerplate.

The wiki is not always up to date: pages carry "as of" dates, later memos and meeting notes change
things, and later memos sometimes change earlier memos. When a question pins a date, answer for that
date. People are sometimes mentioned by nickname or by first name and initial.

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the pages you need.

Write `answers.json` in the workspace root:

```json
{
  "q1": { "answer": "<full name or team name>", "evidence": ["incidents/INC-....md", "..."] },
  "q2": { "answer": "...", "evidence": ["..."] }
}
```

- For a person, `answer` is the full name exactly as the heading of their `people/` page writes it
  (not a nickname). For a team, it is the team's name (for example `Larkspur`).
- `evidence` lists the pages the answer rests on; it is optional and not scored.
- Include every question from q1 to q10. If you cannot pin one down, give your best guess anyway.

Questions:

- **q1.** INC-4729 was opened on 2026-03-03. Who was the primary on-call lead, that ISO week, of the team that owned the service the incident's root cause was traced to, as of that day?
- **q2.** INC-4284 was opened on 2026-04-28. Who was the primary on-call lead, that ISO week, of the team that owned the service the incident's root cause was traced to, as of that day?
- **q3.** INC-4133 was opened on 2026-02-18. Who was the primary on-call lead, that ISO week, of the team that owned the service the incident's root cause was traced to, as of that day?
- **q4.** INC-4482 was opened on 2026-03-20. Who was the primary on-call lead, that ISO week, of the team that owned the service the incident's root cause was traced to, as of that day?
- **q5.** INC-4446 was opened on 2026-04-27. Who was the primary on-call lead, that ISO week, of the team that owned the service the incident's root cause was traced to, as of that day?
- **q6.** INC-4112 was opened on 2026-05-20. Who was the line manager of that incident's incident commander on that day?
- **q7.** INC-4537 was opened on 2026-04-16. Who was the line manager of that incident's incident commander on that day?
- **q8.** INC-4683 was opened on 2026-05-11. Who was the line manager of that incident's incident commander on that day?
- **q9.** Which team owned the service that INC-4806's root cause was traced to, as of 2026-04-07? (Not on the day of the incident.)
- **q10.** Which team owned the service that INC-4102's root cause was traced to, as of 2026-04-07? (Not on the day of the incident.)

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
