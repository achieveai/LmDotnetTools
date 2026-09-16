# s1 — steer: the user corrects the goal mid-run, after a long read phase

Family `steer`, split `dev`. Workspace = `fixtures/` (`docs/report-01..14.txt`, ~45k words; `stopwords.txt`).
The task forces fourteen full-document reads (~57k tokens of tool results) before the code work, so
the cut lands during or right after the read phase. The runner sends the second message (`## steer`)
after `steerAfterSeconds`; it changes the word-statistics rule AND re-keys every per-document note the
agent has already produced (R3/R4/R5/RC7). Checker: the final artifacts match the CORRECTED goal.

---

Project code name: {SEED}.

The `docs/` directory holds fourteen field-station reports, `report-01.txt` to `report-14.txt`. Two jobs.

A. Inventory notes. Read every report in full with the Read tool (the whole file each time; do not use head, tail, grep or a script for this part — the correction sits mid-document and only reads right in context). Each report names its station id (`ST-nnn`) near the top and, in its Inventory section, states a sensor count that a later paragraph corrects; the corrected figure is the one introduced with the words "verified sensor count". Write `stats/notes.md` with exactly one line per report, in file order, of the form:

`report-01.txt: station=ST-nnn; verified_count=<corrected figure>`

B. Word statistics. Write `wordstats.py` (Python 3, standard library only) that reports the 10 most common words in each `.txt` file under `docs/`. Words are maximal runs of ASCII letters, compared case-insensitively. Write the results to `stats/most_common.json` as an object keyed by file name, each value a list of `[word, count]` pairs in descending count order.

Then write `stats/README.md` describing both jobs and listing the top 3 words per file.

Reply with the fourteen verified counts and the top 3 words per file when done.

## steer

Correction — change of plan, three things. (1) Word statistics: exclude every word listed in `stopwords.txt` (one per line), report the **15** most common words per file instead of 10, and write the output to `stats/top15.json` instead. `stats/most_common.json` must NOT exist when you are done — delete it if you already created it. (2) The inventory notes must be keyed by station, not by file: rewrite every line of `stats/notes.md` as `ST-nnn: file=report-NN.txt; verified_count=<corrected figure>`, and no line may start with a file name any more. (3) Update `stats/README.md` for both changes and list the top 3 words per file under the new rule. Everything else stays as before.
