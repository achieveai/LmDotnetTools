"""Deterministic fixture generator for ag1 (run once; the generated files are committed).

Writes fixtures/ledgers/<site>_2026-W<nn>.csv (8 sites x 6 ISO weeks), hidden/key.json (answer key),
hidden/build-report.json (sizes + which trap flips which answer), task.md and meta.json's
requiredFixtures. Knobs live in hidden/build.json.

Each ledger is a warehouse movement export: CSV rows plus '#' operator notes. Most rows are noise
for any one question. The rules that decide every answer:

  * a row's quantity is in single units, UNLESS that file's header notes say the export is in
    cases of 12 (worded differently per file; other files carry decoy notes that mention cases
    but do not change the unit);
  * a transaction id that appears on several rows (a re-sent row) counts once;
  * a REVERSAL row cancels the transaction named in its ref_txn column, which may sit in an
    earlier week's file of the same site; the REVERSAL row itself moves nothing;
  * COUNT rows are stock counts, not movements.

Usage: python gen_ledgers.py   (from anywhere; paths are relative to this file)
"""
import json
import random
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
TASK = HERE.parent
CFG = json.load(open(HERE / "build.json", encoding="utf-8"))
rng = random.Random(CFG["seed"])

SITES = CFG["sites"]
WEEKS = CFG["weeks"]  # ISO week numbers of 2026
REGIONS = ["NORD", "SUD", "OST", "WEST", "ZENTRAL"]
CARRIERS = ["Vektor", "Halden", "Brisa", "Ostra", "Kalmar"]
SKUS = [f"K-{1001 + i}" for i in range(CFG["skus"])]
SKU_W = [1.0 / (i + 1) ** 0.8 for i in range(len(SKUS))]
HEADER = "txn_id,timestamp,site,type,sku,qty,region,carrier,ref_txn"

CASE_NOTES = [
    "# unit note: qty in this export is CASES (1 case = 12 units)",
    "# NB export profile P2 was used for this file - quantities are case counts, 12 units per case",
    "# ops: scanner batch mode stayed on all week, so every qty below is cases of 12, not single units",
    "# qty column counts cases this week (multiply by 12 for units) - reconfigured after the audit",
]
DECOY_NOTES = [
    "# FYI: from 2026-W20 this site will export qty as cases of 12; this file is still in single units",
    "# reminder: several SKUs are packed in cases of 12 on the shelf, but qty below is already in units",
    "# the case-count export profile (P2) was tested on the staging box only; this export is units as usual",
]
NOISE_NOTES = [
    "# shift lead early: {name}; late: {name2}",
    "# forklift FL-{n} out of service {d}, back {d2}",
    "# dock {n} door sensor flaky, manual log kept on paper",
    "# weather: heavy rain {d}, two carrier pickups late",
    "# stocktake of aisle {n} done {d} (COUNT rows are stock counts, not movements)",
    "# WMS patch 4.{n} applied {d}, no data impact expected",
    "# handover: {name} covering returns desk this week",
    "# carrier {carrier} switched pickup window to 06:00-08:00",
]
NAMES = ["Ana", "Bram", "Chiara", "Dmitri", "Elif", "Farah", "Goran", "Hanne", "Ines", "Jonas", "Kaja", "Luca"]


def week_monday(w):
    return date(2025, 12, 29) + timedelta(weeks=w - 1)  # ISO week 1 of 2026 starts Mon 2025-12-29


def noise_note(w):
    t = rng.choice(NOISE_NOTES)
    d = week_monday(w) + timedelta(days=rng.randrange(7))
    return t.format(name=rng.choice(NAMES), name2=rng.choice(NAMES), n=rng.randrange(1, 12),
                    d=d.isoformat(), d2=(d + timedelta(days=2)).isoformat(), carrier=rng.choice(CARRIERS))


# ------------------------------------------------------------------ generate
files = [(s, w) for s in SITES for w in WEEKS]
case_files = set(rng.sample(files, CFG["case_files"]))
decoy_files = set(rng.sample([f for f in files if f not in case_files], CFG["decoy_files"]))

txns = {}  # txn_id -> dict(site, week, ts, type, sku, qty_raw, factor, region, carrier, ref)
file_rows = {}
reversed_by = {}  # original txn -> (reversal txn, reversal week)
movable = defaultdict(list)  # site -> list of SHIP/RECEIVE txn ids (for reversals)

for (s, w) in files:
    n_rows = rng.randint(CFG["rows_min"], CFG["rows_max"])
    factor = 12 if (s, w) in case_files else 1
    monday = datetime.combine(week_monday(w), datetime.min.time())
    stamps = sorted(monday + timedelta(seconds=rng.randrange(7 * 86400)) for _ in range(n_rows))
    rows = []
    for i, ts in enumerate(stamps):
        tid = f"{s.upper()}-W{w}-{i + 1:05d}"
        r = rng.random()
        typ = "SHIP" if r < 0.68 else "RECEIVE" if r < 0.86 else "COUNT" if r < 0.96 else "REVERSAL"
        rec = dict(id=tid, site=s, week=w, ts=ts.strftime("%Y-%m-%dT%H:%M:%SZ"), type=typ, sku="", qty="",
                   region="", carrier="", ref="", factor=factor)
        if typ == "REVERSAL":
            cands = [t for t in movable[s][-400:] if t not in reversed_by]
            if not cands:
                rec["type"] = typ = "COUNT"
            else:
                orig = rng.choice(cands)
                rec["ref"] = orig
                rec["sku"] = txns[orig]["sku"]
                reversed_by[orig] = (tid, w)
        if typ in ("SHIP", "RECEIVE"):
            rec["sku"] = rng.choices(SKUS, SKU_W)[0]
            units = rng.choice([1, 2, 3, 4, 6, 12, 12, 24, 24, 36, 48, 60, 96, 120, 144])
            rec["qty"] = max(1, units // 12) if factor == 12 else units
            if typ == "SHIP":
                rec["region"] = rng.choice(REGIONS)
                rec["carrier"] = rng.choice(CARRIERS)
            movable[s].append(tid)
        if typ == "COUNT":
            rec["sku"] = rng.choices(SKUS, SKU_W)[0]
            rec["qty"] = rng.randint(50, 4000)
        txns[tid] = rec
        rows.append(rec)
    # re-sent rows: exact copies inserted later in the same file
    dup_n = max(1, int(len(rows) * CFG["duplicate_rate"]))
    for rec in rng.sample([x for x in rows if x["type"] in ("SHIP", "RECEIVE")], dup_n):
        pos = rng.randrange(rows.index(rec) + 1, len(rows) + 1)
        rows.insert(pos, rec)
    file_rows[(s, w)] = rows


def line(rec):
    return ",".join(str(rec[k]) for k in ("id", "ts", "site", "type", "sku", "qty", "region", "carrier", "ref"))


ledgers = TASK / "fixtures" / "ledgers"
ledgers.mkdir(parents=True, exist_ok=True)
for old in ledgers.glob("*.csv"):
    old.unlink()
total_bytes = 0
for (s, w), rows in file_rows.items():
    notes = [f"# {s.upper()} warehouse movement export, ISO week 2026-W{w}", noise_note(w)]
    notes += [noise_note(w) for _ in range(rng.randint(1, 4))]
    if (s, w) in case_files:
        notes.insert(rng.randrange(1, len(notes) + 1), rng.choice(CASE_NOTES))
    if (s, w) in decoy_files:
        notes.insert(rng.randrange(1, len(notes) + 1), rng.choice(DECOY_NOTES))
    out = notes + [HEADER]
    mid = set(rng.sample(range(len(rows)), rng.randint(1, 3)))
    for i, rec in enumerate(rows):
        if i in mid:
            out.append(noise_note(w))
        out.append(line(rec))
    text = "\n".join(out) + "\n"
    p = ledgers / f"{s}_2026-W{w}.csv"
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    total_bytes += len(text.encode("utf-8"))


# ------------------------------------------------------------------ answers under the rules (and under each trap)
def movements(case=True, dedupe=True, reversals=True):
    """Yield (txn record, units) for every SHIP/RECEIVE that counts under the given interpretation."""
    for (s, w), rows in file_rows.items():
        seen = set()
        for rec in rows:
            if rec["type"] not in ("SHIP", "RECEIVE"):
                continue
            if dedupe and rec["id"] in seen:
                continue
            seen.add(rec["id"])
            if reversals and rec["id"] in reversed_by:
                continue
            yield rec, rec["qty"] * (rec["factor"] if case else 1)


def shipped(pred, **kw):
    return sum(u for r, u in movements(**kw) if r["type"] == "SHIP" and pred(r))


def received(pred, **kw):
    return sum(u for r, u in movements(**kw) if r["type"] == "RECEIVE" and pred(r))


sku_rank = [k for k, _ in Counter(r["sku"] for r in txns.values() if r["type"] == "SHIP").most_common()]
sku_a, sku_b = sku_rank[1], sku_rank[4]
site_s = rng.choice(SITES)
region_r, carrier_c = rng.choice(REGIONS), rng.choice(CARRIERS)
week_w, week_w2 = rng.choice(WEEKS[1:]), rng.choice(WEEKS[1:])
case_days = sorted({r["ts"][:10] for (s, w) in case_files for r in file_rows[(s, w)]})
day_d = rng.choice(case_days)
site_s2, carrier_c2, region_r2 = rng.choice(SITES), rng.choice(CARRIERS), rng.choice(REGIONS)


def q4(sku=None, week=None, **kw):
    sku, week = sku or sku_b, week or week_w2
    per = {s: shipped(lambda r, s=s: r["site"] == s and r["sku"] == sku and r["week"] == week, **kw) for s in SITES}
    best = sorted(per.items(), key=lambda kv: (-kv[1], kv[0]))
    return best[0][0], best


# q4 is picked so that ignoring the case notes changes the winner and the true winner leads by >= 5%.
combos = [(k, w) for k in sku_rank[:12] for w in WEEKS]
rng.shuffle(combos)
for k, w in combos:
    win, table = q4(k, w)
    if table[0][1] >= 1.05 * table[1][1] and q4(k, w, case=False)[0] != win:
        sku_b, week_w2 = k, w
        break
else:
    raise SystemExit("no q4 combo satisfies the trap + margin constraint")


def q3(**kw):
    ids = {r["id"] for r, _ in movements(**kw)
           if r["type"] == "SHIP" and r["region"] == region_r and r["carrier"] == carrier_c and r["week"] == week_w}
    if not kw.get("dedupe", True):  # the careless count counts rows, not ids
        return sum(1 for r, _ in movements(**kw)
                   if r["type"] == "SHIP" and r["region"] == region_r and r["carrier"] == carrier_c and r["week"] == week_w)
    return len(ids)


def answers(**kw):
    return {
        "q1": shipped(lambda r: r["sku"] == sku_a, **kw),
        "q2": received(lambda r: r["site"] == site_s, **kw),
        "q3": q3(**kw),
        "q4": q4(**kw)[0],
        "q5": shipped(lambda r: r["ts"][:10] == day_d, **kw),
        "q6": sum(1 for o, (rid, rw) in reversed_by.items() if txns[o]["type"] == "SHIP" and rw > txns[o]["week"]),
        "q7": shipped(lambda r: r["site"] == site_s2 and r["carrier"] == carrier_c2 and r["region"] == region_r2, **kw),
        "q8": sum(1 for rows in file_rows.values() for k, v in Counter(r["id"] for r in rows).items() if v > 1),
    }


gold = answers()
traps = {"ignore_case_notes": answers(case=False), "count_resent_rows": answers(dedupe=False),
         "ignore_reversals": answers(reversals=False)}
flips = {q: [t for t, a in traps.items() if a[q] != gold[q]] for q in gold}
q4_margin = q4()[1][:3]

W = lambda w: f"2026-W{w}"
questions = [
    ("q1", "int", f"How many units of SKU {sku_a} were shipped in total, across all sites and all six weeks?"),
    ("q2", "int", f"How many units did site {site_s.upper()} receive in total across the six weeks?"),
    ("q3", "int", f"How many shipments (distinct SHIP transactions) went to region {region_r} with carrier {carrier_c} in {W(week_w)}, across all sites?"),
    ("q4", "exact", f"Which site shipped the most units of SKU {sku_b} in {W(week_w2)}? Answer with the three-letter site code."),
    ("q5", "int", f"How many units were shipped in total on {day_d} (UTC date of the timestamp), all sites and SKUs?"),
    ("q6", "int", "How many SHIP transactions were cancelled by a REVERSAL row that sits in a LATER week's file than the shipment itself?"),
    ("q7", "int", f"How many units did site {site_s2.upper()} ship to region {region_r2} with carrier {carrier_c2}, across the six weeks?"),
    ("q8", "int", "How many transaction ids appear on more than one row (re-sent rows), across all files?"),
]
key = {"task": "ag1", "source": "synthetic", "questions": [
    {"id": qid, "kind": kind, "question": text, "answers": [str(gold[qid]).upper() if kind == "exact" else gold[qid]],
     "trap_flips": flips[qid]} for qid, kind, text in questions]}
with open(HERE / "key.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(key, f, indent=2)
    f.write("\n")
report = {"task": "ag1", "fixture_files": len(files), "fixture_bytes": total_bytes, "approx_tokens": total_bytes // 4,
          "rows": sum(len(r) for r in file_rows.values()), "case_files": sorted(f"{s}_2026-W{w}" for s, w in case_files),
          "decoy_files": sorted(f"{s}_2026-W{w}" for s, w in decoy_files), "gold": gold, "trap_answers": traps,
          "trap_flips": flips, "q4_top3": q4_margin}
with open(HERE / "build-report.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(report, f, indent=2)
    f.write("\n")

sites_list = ", ".join(x.upper() for x in SITES)
msg = f"""Project code name: {{SEED}}.

`ledgers/` holds {len(files)} warehouse movement exports: one CSV file per site per ISO week
(sites {sites_list}; weeks 2026-W{WEEKS[0]} to 2026-W{WEEKS[-1]}), about {sum(len(r) for r in file_rows.values()):,} rows in all.
Columns: `{HEADER}`.

How to read them:

- `type` is SHIP, RECEIVE, COUNT or REVERSAL. COUNT rows are stock counts, not movements.
- A REVERSAL row cancels the transaction named in its `ref_txn` column entirely (that transaction
  may be in an earlier week's file of the same site). The REVERSAL row itself moves nothing.
- The same transaction can appear on more than one row when a row was re-sent. Count each
  `txn_id` once.
- Lines starting with `#` are operator notes. Some notes change how a file's rows must be read;
  most do not. Read each file's notes and apply the ones that matter.
- "Units" means single items. "Shipped" means SHIP transactions that were not reversed.

Answer these {len(questions)} questions exactly (whole numbers, no rounding; q4 is a site code):

""" + "\n".join(f"- **{qid}.** {text}" for qid, _, text in questions) + f"""

Write `answers.json` in the workspace root: `{{"q1": 1234, "q2": 567, ..., "q4": "XYZ", ...}}` with every
question from q1 to q{len(questions)}. You may use scripts (Python 3, standard library) as well as reading files.

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
"""
header = f"""# ag1 — exact aggregation over noisy warehouse ledgers (synthetic)

Family `aggregation`, split `dev`. Workspace = `fixtures/` (`ledgers/*.csv`, {len(files)} files, {total_bytes / 1e6:.2f} MB,
~{sum(len(r) for r in file_rows.values()):,} rows). Every answer is an exact number over a small slice of the rows; the rest is
noise. Three traps a careless read or summary loses: {len(case_files)} files say in their header notes that `qty` is in
cases of 12 (worded four ways; {len(decoy_files)} other files carry decoy notes about cases that change nothing), re-sent rows
(same txn_id) count once, and REVERSAL rows cancel a transaction that may sit in an earlier week's file.
`hidden/build-report.json` records which trap flips which answer. Checker: exact integer / site code;
score = correct / {len(questions)}. Generator + knobs: `hidden/gen_ledgers.py`, `hidden/build.json`."""
with open(TASK / "task.md", "w", encoding="utf-8", newline="\n") as f:
    f.write(header + "\n\n---\n\n" + msg)
meta_path = TASK / "meta.json"
meta = json.load(open(meta_path, encoding="utf-8"))
meta["requiredFixtures"] = [{"glob": "ledgers/*.csv", "count": len(files)}]
with open(meta_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(meta, f, indent=2)
    f.write("\n")
print(json.dumps({"gold": gold, "flips": flips, "q4_top3": q4_margin, "bytes": total_bytes}, indent=1))
