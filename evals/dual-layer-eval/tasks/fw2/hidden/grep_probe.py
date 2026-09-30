"""Adversarial grep probe for fw2: can grep over a question's words land on the gold answer?

For each question: take its distinctive words (lower-cased tokens minus a standard English stop list),
run `grep -ril -- <word> fixtures/` for each, and score every file by how many of the words it
contains. Two readings of "grep returns":
  AND   the files that contain every distinctive word;
  BEST  the files that contain the most distinctive words (what a grep chain narrows to).
A question counts as SHORTCUT when a returned file contains a gold answer string. Aggregation
answers (counts, sums) are stated in no file; for them the probe also reports a literal
whole-token digit match, which is noisy by design.

Mode --translate first maps question words through handbook/glossary.md (depot -> yard, ...),
which is what an agent does after reading the glossary; it is reported as contrary evidence.

Usage: python grep_probe.py [--translate]   (needs grep on PATH; run from anywhere)
"""
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIX = HERE.parent / "fixtures"
STOP = set("""a about above after again against all am an and any are as at be because been before being below
between both but by can could did do does doing down during each few for from further had has have having he her
here hers herself him himself his how i if in into is it its itself just me more most my myself no nor not now of
off on once only or other our ours ourselves out over own same she should so some such than that the their theirs
them themselves then there these they this those through to too under until up very was we were what when where
which while who whom why will with you your yours yourself yourselves""".split())
GLOSS = {"depot": ["yard"], "depots": ["yard"], "manager": ["yardmaster"], "region": ["area"],
         "regional": ["area"], "director": ["steward"], "treasurer": ["keeper", "accounts"],
         "chair": ["presiding"], "secretary": ["clerk"], "board": ["council"], "confirmed": ["substantive"],
         "merger": ["amalgamation", "amalgamated"], "night": ["overnight"], "routes": ["workings"],
         "flooded": ["inundation", "inundated", "water"], "floods": ["inundation", "inundated", "water"],
         "operating": ["use"], "approved": ["motion"]}


def words(q, translate):
    toks = [t for t in re.findall(r"[a-z0-9]+", q.lower()) if t not in STOP and len(t) > 1]
    if translate:
        toks = [w for t in toks for w in GLOSS.get(t, [t])]
    return list(dict.fromkeys(toks))


def grep(word):
    r = subprocess.run(["grep", "-ril", "--", word, str(FIX)], capture_output=True, text=True)
    return {Path(p).resolve() for p in r.stdout.splitlines() if p}


def contains(path, answers, is_int):
    t = path.read_text(encoding="utf-8").lower()
    if is_int:
        return any(re.search(rf"(?<![\w]){re.escape(a)}(?![\w])", t) for a in answers)
    return any(a.lower() in t for a in answers)


def main():
    translate = "--translate" in sys.argv
    key = json.load(open(HERE / "key.json", encoding="utf-8"))
    rows, shortcut = [], 0
    for q in key["questions"]:
        ws = words(q["question"], translate)
        hits = {w: grep(w) for w in ws}
        score = {}
        for w, fs in hits.items():
            for f in fs:
                score[f] = score.get(f, 0) + 1
        top = max(score.values()) if score else 0
        best = [f for f, s in score.items() if s == top]
        and_set = [f for f, s in score.items() if s == len(ws)]
        is_int = q["kind"] == "int"
        best_hit = any(contains(f, q["answers"], is_int) for f in best)
        and_hit = any(contains(f, q["answers"], is_int) for f in and_set)
        sc = (best_hit or and_hit) and not is_int
        shortcut += sc
        rows.append(f"| {q['id']} | {q['family']} | {len(ws)} | {len(and_set)} | {'yes' if and_hit else 'no'} | "
                    f"{top}/{len(ws)} in {len(best)} | {'yes' if best_hit else 'no'} | "
                    f"{'computed' if is_int else ('SHORTCUT' if sc else 'no')} |")
    print(f"mode: {'glossary-translated' if translate else 'question words as written'}")
    print("| q | family | words | AND files | AND has gold | BEST (words matched in files) | BEST has gold | verdict |")
    print("|---|---|---|---|---|---|---|---|")
    print("\n".join(rows))
    print(f"shortcut (a named answer found by grep): {shortcut}/{len(rows)}")


if __name__ == "__main__":
    main()
