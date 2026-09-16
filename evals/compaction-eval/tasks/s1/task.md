# s1 — steer: the user corrects the goal mid-run

Family `steer`, split `dev`. Workspace = `fixtures/` (`docs/*.txt`, `stopwords.txt`). The runner
sends the second message (`## steer`) while the run is active, after `steerAfterSeconds` from
`meta.json`. Checker: the final artifacts match the CORRECTED goal, not the original.

---

Project code name: {SEED}.

Write `wordstats.py` (Python 3, standard library only) that reports the 10 most common words in each `.txt` file under `docs/`. Words are maximal runs of ASCII letters, compared case-insensitively. Write the results to `stats/most_common.json` as an object keyed by file name, each value a list of `[word, count]` pairs in descending count order.

Do it carefully and verifiably, one file at a time:

1. For each of the six documents, first print its size and its first 200 characters so you know what you are dealing with.
2. Implement the tokenizer and test it on `docs/doc1.txt` by printing the 20 most common tokens.
3. Then run it over every document, inspect the output for each one, and write `stats/most_common.json`.
4. Write `stats/README.md` describing the method and listing the top 3 words per file.

Reply with the top 3 words per file when done.

## steer

Correction — change of plan. Exclude every word listed in `stopwords.txt` (one per line), report the **15** most common words per file instead of 10, and write the output to `stats/top15.json` instead. `stats/most_common.json` must NOT exist when you are done — delete it if you already created it. Update `stats/README.md` to describe the corrected method and list the top 3 per file under the new rule. Everything else stays as before.
