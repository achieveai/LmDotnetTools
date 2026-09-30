# hq1 — two-hop QA over a shared corpus with distractors (HotpotQA distractor, hard)

Family `qa-multihop`, split `dev`. Workspace = `fixtures/` (`docs/*.md`, 1197 article files, 0.73 MB).
10 HotpotQA dev-distractor questions (level `hard`; 8 bridge, 2 comparison; yes/no answers excluded)
share one corpus: each question's 10 paragraphs (2 gold, 8 distractors) plus the 10 paragraphs of 110
other dev questions, merged by title. Stresses: wide search, lookalike distractor articles, carrying a
bridge entity between reads. Checker: normalized exact match or token F1 >= 0.8; score = correct / 10.
Knobs: `hidden/build.json`.

---

Project code name: {SEED}.

`docs/` holds 1197 short encyclopedia articles (the opening paragraph of each), one Markdown file per article; the file name is the article title. Most of them have nothing to do with any of the 10 questions below. Every question needs two articles: either one article leads to the entity the second one describes, or the question compares two entities.

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
- Include every question from q1 to q10. If you cannot pin one down, give your best guess anyway.

Questions:

- **q1.** Which composer was also a teacher, Philip Glass or Franz Schreker?
- **q2.** What British rock band released the album Brothers in Arms in 1985 which also featured a song with the same title?
- **q3.** What resort in Biloxi, Mississippi does the Chief Operating Officer of the largest single hotel in the United States oversee?
- **q4.** Which of the following has released ten solo albums: Kristin Hersh or Mike Patton?
- **q5.** What North Carolina native did Danja produce songs for?
- **q6.** Multiple award-winning actor Gary Sinise appeared in The Stand in 1994 - a miniseries based on a novel and screenplay by which noted author?
- **q7.** KSCW-DT is part of a duopoly with which virtual channel 12 in Wichita?
- **q8.** LJM was one of what type of company Andrew Fastow used in the Enron scandal?
- **q9.** What abbey, founded in 598, was Clarembald a monk at?
- **q10.** Which airline partners with Gogo Inflight Internet and opened in 1984?

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
