# fw2 — indirect multi-hop and judged aggregation over a fictional co-operative's wiki (synthetic)

Family `fictional-multihop`, split `dev`. Workspace = `fixtures/` (567 Markdown pages, 1.32 MB,
~329k tokens; mostly varied staff chatter). 10 questions, 3-6 hops each, every hop named by description:
inundation (by season) -> traffic notice (corrected, drafted, rumoured) -> yard -> yardmaster / area steward on a date
(interim covers, rescinded appointments, cancelled area moves), Council-role succession -> career profile, and counts or
sums over yards selected by judgement (amalgamation mover -> area -> yards ever in it -> still in use). The question
vocabulary appears only in handbook/glossary.md. Checker: normalised exact match / integer; score = correct / 10.
Trap answers and predicted trap-only scores: `hidden/build-report.json`. Generator + knobs: `hidden/gen_world.py`,
`hidden/build.json`.

---

Project code name: {SEED}.

This workspace is a copy of the staff wiki of the Tessmarrow Carriers' Co-operative, a (fictional)
haulage co-operative: 567 Markdown pages. Nothing in it is about a real organisation or
person, so the answers are only in these pages; general knowledge will not help. Most pages are
everyday staff chatter that has nothing to do with the questions.

The co-operative has its own vocabulary, and the questions below use ordinary business words, so
you will need to work out what the wiki calls things. Not everything written down is true or still
current: some pages are drafts, some are gossip, and later decisions change earlier ones. When a
question pins a date, answer for that date.

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the pages you need.

Write `answers.json` in the workspace root:

```json
{
  "q1": { "answer": "<full name, depot name, or whole number>", "evidence": ["council/....md", "..."] },
  "q2": { "answer": "...", "evidence": ["..."] }
}
```

- A person's `answer` is their full name as written in the wiki; a depot's is its name as written
  (for example `Marlbury`); a count or total is a whole number.
- `evidence` lists the pages the answer rests on; it is optional and not scored.
- Include every question from q1 to q10. If you cannot pin one down, give your best guess anyway.

Questions:

- **q1.** How many different people held the confirmed manager post at the depot formed by the 2027 merger, from its formation to the end of 2032?
- **q2.** At the end of 2033, who was the confirmed manager of the depot that took over the night routes of the depot that flooded in the spring of 2032?
- **q3.** How many of the depots that were ever part of the region directed by the person who approved the 2029 merger (the region that person directed when approving it) were still operating at the end of 2029?
- **q4.** How many of the depots that were ever part of the region directed by the person who approved the 2027 merger (the region that person directed when approving it) were still operating at the end of 2032?
- **q5.** On 30 November 2032, who was the confirmed regional director responsible for the depot that took over the night routes of the depot that flooded in the autumn of 2030?
- **q6.** Which depot did the fourth person to hold the chair role on a confirmed basis last run, as its confirmed manager, before joining the board?
- **q7.** In total, how many night routes were handed to depots in the region that the depot flooded in the summer of 2032 belonged to at the start of 2032, because of floods during 2032?
- **q8.** On 30 November 2031, who was the confirmed regional director responsible for the depot that took over the night routes of the depot that flooded in the spring of 2030?
- **q9.** How many different people held the confirmed manager post at the depot formed by the 2030 merger, from its formation to the end of 2032?
- **q10.** At the end of 2030, who was the confirmed manager of the depot that took over the night routes of the depot that flooded in the spring of 2028?

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
