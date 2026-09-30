"""Deterministic generator for ag2: ledger aggregation governed by operations memos (output committed).

Hardens ag1 (retired: a script solved it). The ledgers carry no notes at all; the rules that change
the numbers live in `memos/`, ~150 long operations bulletins, each rule stated once, mid-memo, in
paraphrase:

  R1 unit      from week `unit_from_week`, one site's qty column counts cartons of 12
  R2 exclude   one site's exports are a training mirror and are left out of every network figure
  R3 window    movements stamped inside a migration window are void (they were replayed later)
  R4 supersede a memo says a second site switched to cartons from week A; a later memo corrects it
               to week B (> A), so weeks A..B-1 are still single units
  decoy        a proposal to exclude another site from network figures, explicitly rejected

Traps (build-report.json): ignore_memos (a script over the CSVs), ignore_unit, ignore_exclusion,
ignore_window, superseded_version (applies R4 from week A), apply_decoy (also excludes the decoy site).
The build aborts unless ignore_memos scores <= max_script_correct and every trap flips an answer.

Usage: python gen_ledgers.py   (paths are relative to this file)
"""
import json
import random
import shutil
import sys
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
TASK = HERE.parent
sys.path.insert(0, str(TASK.parent.parent / "tools"))
from filler import Filler  # noqa: E402

CFG = json.load(open(HERE / "build.json", encoding="utf-8"))
rng = random.Random(CFG["seed"])
F = Filler(random.Random(CFG["seed"] + 1))

SITES = CFG["sites"]
WEEKS = CFG["weeks"]
REGIONS = ["NORD", "SUD", "OST", "WEST", "ZENTRAL"]
CARRIERS = ["Vektor", "Halden", "Brisa", "Ostra", "Kalmar"]
SKUS = [f"K-{2001 + i}" for i in range(CFG["skus"])]
SKU_W = [1.0 / (i + 1) ** 0.7 for i in range(len(SKUS))]
HEADER = "txn_id,timestamp,site,type,sku,qty,region,carrier,ref_txn"
R = CFG["rules"]
UNIT_SITE, UNIT_FROM = R["unit_site"], R["unit_from_week"]
EXCL = R["excluded_site"]
WIN_START = datetime.fromisoformat(R["window_start"])
WIN_END = datetime.fromisoformat(R["window_end"])
SUP_SITE, SUP_A, SUP_B = R["supersede_site"], R["supersede_first_week"], R["supersede_true_week"]
DECOY = R["decoy_site"]


def week_monday(w):
    return date(2025, 12, 29) + timedelta(weeks=w - 1)


def factor(site, week, fl):
    if fl.get("ignore_memos"):
        return 1
    if site == UNIT_SITE and week >= UNIT_FROM and not fl.get("ignore_unit"):
        return 12
    if site == SUP_SITE and week >= (SUP_A if fl.get("superseded_version") else SUP_B):
        return 12
    return 1


# ------------------------------------------------------------------ rows
txns, file_rows, reversed_by = {}, {}, {}
movable = defaultdict(list)
for s in SITES:
    for w in WEEKS:
        true_f = factor(s, w, {})
        mon = datetime.combine(week_monday(w), datetime.min.time())
        n = rng.randint(CFG["rows_min"], CFG["rows_max"])
        stamps = [mon + timedelta(seconds=rng.randrange(7 * 86400)) for _ in range(n)]
        if WIN_START.date() <= (mon + timedelta(days=6)).date() and WIN_END >= mon:  # thicken the window week
            span = int((WIN_END - WIN_START).total_seconds())
            stamps += [WIN_START + timedelta(seconds=rng.randrange(span)) for _ in range(CFG["window_rows"])]
        stamps.sort()
        rows = []
        for i, ts in enumerate(stamps):
            tid = f"{s.upper()}-{w}-{i + 1:05d}"
            x = rng.random()
            typ = "SHIP" if x < 0.66 else "RECEIVE" if x < 0.85 else "COUNT" if x < 0.95 else "REVERSAL"
            rec = dict(id=tid, site=s, week=w, dt=ts, ts=ts.strftime("%Y-%m-%dT%H:%M:%SZ"), type=typ, sku="",
                       qty="", region="", carrier="", ref="")
            if typ == "REVERSAL" and WIN_START <= ts < WIN_END:
                typ = rec["type"] = "COUNT"  # no reversal is ever void, so q7 is unambiguous
            if typ == "REVERSAL":
                c = [t for t in movable[s][-300:] if t not in reversed_by
                     and not (WIN_START <= txns[t]["dt"] < WIN_END)]
                if not c:
                    rec["type"] = typ = "COUNT"
                else:
                    o = rng.choice(c)
                    rec["ref"], rec["sku"] = o, txns[o]["sku"]
                    reversed_by[o] = rec
            if typ in ("SHIP", "RECEIVE"):
                rec["sku"] = rng.choices(SKUS, SKU_W)[0]
                units = rng.choice([12, 12, 24, 24, 36, 48, 60, 72, 96, 120, 144, 6, 3])
                rec["units"] = units if true_f == 1 else max(12, units // 12 * 12)
                rec["qty"] = rec["units"] // true_f
                if typ == "SHIP":
                    rec["region"], rec["carrier"] = rng.choice(REGIONS), rng.choice(CARRIERS)
                movable[s].append(tid)
            if typ == "COUNT":
                rec["sku"], rec["qty"] = rng.choices(SKUS, SKU_W)[0], rng.randint(50, 4000)
            txns[tid] = rec
            rows.append(rec)
        for rec in rng.sample([r for r in rows if r["type"] in ("SHIP", "RECEIVE")], max(1, int(len(rows) * CFG["duplicate_rate"]))):
            rows.insert(rng.randrange(rows.index(rec) + 1, len(rows) + 1), rec)
        file_rows[(s, w)] = rows


# ------------------------------------------------------------------ resolver
def movements(fl):
    for (s, w), rows in file_rows.items():
        if not fl.get("ignore_memos") and not fl.get("ignore_exclusion") and s == EXCL:
            continue
        if fl.get("apply_decoy") and s == DECOY:
            continue
        f = factor(s, w, fl)
        seen = set()
        for r in rows:
            if r["type"] not in ("SHIP", "RECEIVE") or r["id"] in seen:
                continue
            seen.add(r["id"])
            if r["id"] in reversed_by:
                continue
            if not fl.get("ignore_memos") and not fl.get("ignore_window") and WIN_START <= r["dt"] < WIN_END:
                continue
            yield r, r["qty"] * f


def ship(pred, fl):
    return sum(u for r, u in movements(fl) if r["type"] == "SHIP" and pred(r))


def recv(pred, fl):
    return sum(u for r, u in movements(fl) if r["type"] == "RECEIVE" and pred(r))


def ship_count(pred, fl):
    return len({r["id"] for r, _ in movements(fl) if r["type"] == "SHIP" and pred(r)})


def top_site(sku, week, fl):
    per = Counter()
    for r, u in movements(fl):
        if r["type"] == "SHIP" and r["sku"] == sku and r["week"] == week:
            per[r["site"]] += u
    best = sorted(per.items(), key=lambda kv: (-kv[1], kv[0]))
    return best[0][0].upper(), best[:3]


def reversed_ships(fl):
    n = 0
    for o, rev in reversed_by.items():
        orig = txns[o]
        if orig["type"] != "SHIP":
            continue
        if not fl.get("ignore_memos") and not fl.get("ignore_exclusion") and orig["site"] == EXCL:
            continue
        if fl.get("apply_decoy") and orig["site"] == DECOY:
            continue
        n += 1
    return n


TRAPS = ["ignore_memos", "ignore_unit", "ignore_exclusion", "ignore_window", "superseded_version", "apply_decoy"]
ranked = [k for k, _ in Counter(r["sku"] for r in txns.values() if r["type"] == "SHIP").most_common()]
sku_a = ranked[2]
region_r, carrier_c = rng.choice(REGIONS), rng.choice(CARRIERS)
win_day = WIN_START.date().isoformat()
# q4: the SUP_SITE switch (week SUP_B) must decide the winner in week SUP_B
sku_b = None
for k in ranked[:15]:
    if top_site(k, SUP_B, {})[0] == SUP_SITE.upper() and top_site(k, SUP_B, {"ignore_memos": True})[0] != SUP_SITE.upper():
        sku_b = k
        break
if sku_b is None:
    raise SystemExit("no SKU where the week-B unit switch decides the q4 winner")

QUESTIONS = [
    ("q1", "int", f"How many units of SKU {sku_a} were shipped across the network over the whole period?",
     lambda fl: ship(lambda r: r["sku"] == sku_a, fl)),
    ("q2", "int", f"How many units did site {UNIT_SITE.upper()} receive in weeks 2026-W{UNIT_FROM:02d} to 2026-W{WEEKS[-1]:02d}?",
     lambda fl: recv(lambda r: r["site"] == UNIT_SITE and r["week"] >= UNIT_FROM, fl)),
    ("q3", "int", f"How many shipments (distinct SHIP transactions) does the network count for {win_day} (UTC date of the timestamp)?",
     lambda fl: ship_count(lambda r: r["ts"][:10] == win_day, fl)),
    ("q4", "exact", f"Which site shipped the most units of SKU {sku_b} in 2026-W{SUP_B:02d}? Answer with the three-letter site code.",
     lambda fl: top_site(sku_b, SUP_B, fl)[0]),
    ("q5", "int", f"How many units did the network ship to region {region_r} with carrier {carrier_c} over the whole period?",
     lambda fl: ship(lambda r: r["region"] == region_r and r["carrier"] == carrier_c, fl)),
    ("q6", "int", f"How many units did site {SUP_SITE.upper()} ship in weeks 2026-W{WEEKS[0]:02d} to 2026-W{SUP_B - 1:02d}?",
     lambda fl: ship(lambda r: r["site"] == SUP_SITE and r["week"] < SUP_B, fl)),
    ("q7", "int", "How many SHIP transactions across the network were cancelled by a REVERSAL?",
     reversed_ships),
    ("q8", "int", f"How many units did the network ship in total in 2026-W{SUP_A:02d}?",
     lambda fl: ship(lambda r: r["week"] == SUP_A, fl)),
]
gold = {qid: f({}) for qid, _, _, f in QUESTIONS}
trap_answers = {t: {qid: f({t: True}) for qid, _, _, f in QUESTIONS} for t in TRAPS}
flips = {qid: [t for t in TRAPS if trap_answers[t][qid] != gold[qid]] for qid in gold}
predicted = {t: round(sum(1 for q in gold if t not in flips[q]) / len(gold), 4) for t in TRAPS}
if predicted["ignore_memos"] > CFG["max_script_correct"] / len(gold):
    raise SystemExit(f"a memo-blind script would score {predicted['ignore_memos']}")
if any(p == 1.0 for p in predicted.values()):
    raise SystemExit(f"a trap flips nothing: {predicted}")

# ------------------------------------------------------------------ ledgers
out = TASK / "fixtures"
for sub in ["ledgers", "memos"]:
    if (out / sub).exists():
        shutil.rmtree(out / sub)
    (out / sub).mkdir(parents=True)
total, n_rows = 0, 0
for (s, w), rows in file_rows.items():
    lines = [f"# {s.upper()} movement export, ISO week 2026-W{w:02d}", HEADER]
    lines += [",".join(str(r[k]) for k in ("id", "ts", "site", "type", "sku", "qty", "region", "carrier", "ref")) for r in rows]
    text = "\n".join(lines) + "\n"
    (out / "ledgers" / f"{s}_2026-W{w:02d}.csv").write_text(text, encoding="utf-8", newline="\n")
    total += len(text.encode("utf-8"))
    n_rows += len(rows)


# ------------------------------------------------------------------ memos
def d(w, dow=0):
    return week_monday(w) + timedelta(days=dow)


def fmt(x):
    return f"{x.day} {x.strftime('%B')} {x.year}"


U, S, E, DC = UNIT_SITE.upper(), SUP_SITE.upper(), EXCL.upper(), DECOY.upper()
planted = [
    (d(UNIT_FROM - 1, 3), "Scanner fleet refresh, wave 2",
     f"One consequence for anyone reading the exports: since the new handhelds went live at {U} at the start of "
     f"week {UNIT_FROM}, a line there records outer cartons rather than single pieces, and every carton holds a "
     f"dozen. Figures in {U} exports from that week on therefore need multiplying by twelve before they are "
     f"compared with anything else. The other sites are unaffected."),
    (d(WEEKS[0], 1), "Training environment and data hygiene",
     f"The {E} warehouse is not a live warehouse this half: its system is the training mirror that replays a "
     f"copy of production traffic for new starters. Its exports look real and sit alongside the others, but "
     f"nothing in them happened, so they must be left out of every figure quoted for the network as a whole."),
    (WIN_END.date() + timedelta(days=2), "WMS migration: close-out",
     f"Between {WIN_START.strftime('%H:%M')} UTC on {fmt(WIN_START)} and {WIN_END.strftime('%H:%M')} UTC on "
     f"{fmt(WIN_END)} the old and new WMS both wrote to the export. Everything stamped inside that window was "
     f"replayed from the new system afterwards under fresh ids, and the replay is already in the ledgers outside "
     f"the window, so the rows stamped inside it are void and are not to be counted by anyone."),
    (d(SUP_A - 1, 2), f"{S} export profile change",
     f"From week {SUP_A}, {S} moves to the carton export profile, so a quantity there will mean cartons of twelve "
     f"rather than single pieces."),
    (d(SUP_A, 4), f"Correction: {S} export profile",
     f"The earlier bulletin about the {S} export profile gave the wrong week. The switch slipped: {S} started "
     f"exporting cartons of twelve only from week {SUP_B}. Exports for weeks {SUP_A} to {SUP_B - 1} are still in "
     f"single pieces, whatever the earlier bulletin said."),
    (d(WEEKS[1], 3), "Ops council minutes (extract)",
     f"A proposal to leave {DC} out of network figures while its dock is rebuilt was discussed and rejected: "
     f"{DC} keeps trading throughout, so its movements stay in every network figure."),
]
decoys = [
    "Several SKUs sit on the shelf in cartons of twelve; the exports record pieces unless a bulletin says otherwise for a site.",
    "The label printers at the southern sites were replaced; no change to what the exports record.",
    "A reminder that COUNT rows are stock counts and never movements.",
    "The carrier onboarding checklist was updated; the exports are unchanged.",
    "Scanner firmware 7.2 was rolled out to all sites; the export profile is unchanged.",
]
titles = ["Ops bulletin", "Warehouse operations update", "Weekly operations note", "Site network bulletin",
          "Operations digest", "Logistics council notes"]
memos = [(dt, rng.choice(titles), body) for dt, _, body in planted]  # planted titles look like every other bulletin
while len(memos) < CFG["memos"]:
    memos.append((d(rng.choice(WEEKS), rng.randrange(5)), f"{rng.choice(titles)}",
                  rng.choice(decoys) if rng.random() < 0.3 else None))
memos.sort(key=lambda m: m[0])
mtotal = 0
for i, (dt, title, body) in enumerate(memos):
    parts = [F.policy(), F.agenda(F.r.randint(3, 6)), F.policy(), F.status_table(), F.policy()]
    if body:
        parts.insert(F.r.randrange(2, len(parts)), body)
    text = (f"# {title} {i + 1:03d}\n\nDate: {dt.isoformat()}. Circulation: site leads, finance, analytics.\n\n"
            + "\n\n".join(parts) + f"\n\n{F.pad(CFG['padding'])}\n")
    (out / "memos" / f"bulletin-{i + 1:03d}.md").write_text(text, encoding="utf-8", newline="\n")
    mtotal += len(text.encode("utf-8"))
total += mtotal

# ------------------------------------------------------------------ key, report, task.md, meta
key = {"task": "ag2", "source": "synthetic", "questions": [
    {"id": qid, "kind": kind, "question": text, "answers": [gold[qid]], "trap_flips": flips[qid]}
    for qid, kind, text, _ in QUESTIONS]}
(HERE / "key.json").write_text(json.dumps(key, indent=2) + "\n", encoding="utf-8", newline="\n")
report = {"task": "ag2", "fixture_files": len(file_rows) + len(memos), "fixture_bytes": total,
          "memo_bytes": mtotal, "approx_tokens": total // 4, "rows": n_rows, "gold": gold,
          "trap_answers": trap_answers, "trap_flips": flips, "predicted_trap_scores": predicted,
          "q4_top3": top_site(sku_b, SUP_B, {})[1], "q4_top3_ignore_memos": top_site(sku_b, SUP_B, {"ignore_memos": True})[1]}
(HERE / "build-report.json").write_text(json.dumps(report, indent=2, default=str) + "\n", encoding="utf-8", newline="\n")

sites = ", ".join(x.upper() for x in SITES)
qs = "\n".join(f"- **{qid}.** {text}" for qid, _, text, _ in QUESTIONS)
msg = f"""Project code name: {{SEED}}.

`ledgers/` holds {len(file_rows)} warehouse movement exports, one CSV per site per ISO week (sites
{sites}; weeks 2026-W{WEEKS[0]:02d} to 2026-W{WEEKS[-1]:02d}; about {n_rows:,} rows). Columns: `{HEADER}`.
`memos/` holds {len(memos)} operations bulletins from the same period.

How to count:

- `type` is SHIP, RECEIVE, COUNT or REVERSAL. COUNT rows are stock counts, not movements.
- A REVERSAL row cancels the transaction named in its `ref_txn` column entirely (it may be in an earlier
  week's file of the same site). The REVERSAL row itself moves nothing.
- A transaction can appear on more than one row when a row was re-sent. Count each `txn_id` once.
- "Units" means single pieces. "Shipped" means SHIP transactions that were not cancelled.
- The bulletins are part of the data. A few of them change how the ledgers must be read or which rows
  count, and some later bulletins correct earlier ones. Every rule that is in force applies to every
  question; most bulletins change nothing. The CSV files themselves carry no notes.

Answer these {len(QUESTIONS)} questions exactly (whole numbers; q4 is a site code):

{qs}

Write `answers.json` in the workspace root: `{{"q1": 1234, "q2": 567, ..., "q4": "XYZ", ...}}` with every
question from q1 to q{len(QUESTIONS)}. You may use scripts (Python 3, standard library) as well as reading files.

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
"""
header = f"""# ag2 — ledger aggregation governed by buried memo rules (synthetic)

Family `aggregation`, split `dev`. Workspace = `fixtures/` ({len(file_rows)} `ledgers/*.csv`, ~{n_rows:,} rows, plus
{len(memos)} `memos/*.md` bulletins; {total / 1e6:.2f} MB, ~{total // 4 // 1000}k tokens). Hardens ag1 (retired: a script
solved it). The CSVs carry no notes; five bulletins change the counting (a carton unit at one site from a
week, a training-mirror site excluded from network figures, a void migration window, a unit switch whose
week a later bulletin corrects, and a rejected proposal to exclude another site). Each rule is one
paraphrased paragraph in the middle of a long bulletin. A memo-blind script scores
{predicted['ignore_memos']}. Checker: exact integer / site code; score = correct / {len(QUESTIONS)}.
Generator + knobs: `hidden/gen_ledgers.py`, `hidden/build.json`."""
(TASK / "task.md").write_text(header + "\n\n---\n\n" + msg, encoding="utf-8", newline="\n")
meta_path = TASK / "meta.json"
meta = json.load(open(meta_path, encoding="utf-8"))
meta["requiredFixtures"] = [{"glob": "ledgers/*.csv", "count": len(file_rows)}, {"glob": "memos/*.md", "count": len(memos)}]
meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"bytes": total, "memo_bytes": mtotal, "rows": n_rows, "gold": gold, "flips": flips, "predicted": predicted}, indent=1))
