# Prints repo<TAB>pr<TAB>n for each PR whose newest judgment is completed+final and not yet in closed.tsv/close-failed.tsv.
import json, sqlite3, sys, urllib.request
db, judge_dir = sys.argv[1], sys.argv[2]
done = set()
for name in ("closed.tsv", "close-failed.tsv"):
    try:
        for line in open(f"{judge_dir}/{name}", encoding="utf-8"):
            f = line.rstrip("\n").split("\t")
            if len(f) >= 3: done.add(tuple(f[:3]))
    except FileNotFoundError:
        pass
rows = sqlite3.connect(f"file:{urllib.request.pathname2url(db)}?mode=ro", uri=True).execute(
    "SELECT key, value FROM cache WHERE substr(key, 1, 18) = 'reviewd/judge-run/'").fetchall()
newest = {}
for key, value in rows:
    pr, _, n = key.rpartition("/")
    if n.isdigit() and int(n) > newest.get(pr, (0, ""))[0]:
        newest[pr] = (int(n), value)
for pr in sorted(newest):
    n, value = newest[pr]
    try: r = json.loads(value)
    except ValueError: continue
    if r.get("stage") == "completed" and r.get("outputStage") == "final":
        item = (str(r.get("repository")), str(r.get("prId")), str(n))
        if item not in done: print("\t".join(item))
