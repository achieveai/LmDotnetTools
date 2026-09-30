# mq1 — multi-hop QA over a shared corpus with distractors (MuSiQue)

Family `qa-multihop`, split `dev`. Workspace = `fixtures/` (`docs/*.md`, 1006 article files, 0.54 MB).
8 MuSiQue-answerable questions (2 two-hop, 3 three-hop, 3 four-hop) share one corpus: the 20
paragraphs of each target question plus the paragraphs of 52 other MuSiQue questions as distractors,
merged by title. Stresses: many opens, most content useless, chained lookups whose intermediate
entity must be carried between reads. Checker: normalized exact match or token F1 >= 0.8 against
answer + aliases (`hidden/key.json`); score = correct / 8. Knobs: `hidden/build.json`.

---

Project code name: {SEED}.

`docs/` holds 1006 short encyclopedia articles, one Markdown file per article; the file name is the article title. Most of them have nothing to do with any of the 8 questions below. Every question is multi-hop: its answer takes facts from two to four different articles chained together (find what the first clue points to, then look that up, and so on).

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the articles you need.
Answer from what the files say, not from memory: where the two disagree, the files win.

Write `answers.json` in the workspace root, shaped like this:

```json
{
  "q1": { "answer": "<short answer>", "evidence": ["<file name>.md", "<file name>.md"] },
  "q2": { "answer": "...", "evidence": ["..."] }
}
```

- `answer` is the shortest span that answers the question: a name, date, number or short phrase,
  written the way the articles write it. Not a sentence, no explanation.
- `evidence` lists the files the answer rests on. It is optional and not scored.
- Include every question from q1 to q8. If you cannot pin one down, give your best guess anyway.

Questions:

- **q1.** What is the name of the castle in city where the performer of Darlings was formed?
- **q2.** When did the city where Souvenir's performer was born become the capitol of the state Knowles was from?
- **q3.** When did the majority party in the body which approves members of the American Cabinet gain control of the House?
- **q4.** What label was bought in the UK by the other major New York broadcast that is in addition to ABC, and the one who released Stand Against Fear?
- **q5.** What was the 2017 population of the country where Bedari was made?
- **q6.** Who said one of the greatest thinkers was the person who proposed explanations for the origins of earthquakes and the formation of mountains?
- **q7.** When was the region immediately north of the region where Urim's country is located and the Persian Gulf created?
- **q8.** Between the state university in the state without North Point Mall and where Edwards won the primary and the university in Fort Hill's town which has the more national championships?

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
