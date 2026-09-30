"""Deterministic generator for fw2: the staff wiki of the Tessmarrow Carriers' Co-operative (invented).

Writes fixtures/{council,notices,reports,papers,forum,people,yards,misc,handbook}/*.md, hidden/key.json,
hidden/build-report.json, task.md and meta.json's requiredFixtures. Knobs: hidden/build.json.

Built so grep cannot shortcut and a skim cannot answer:
- House vocabulary differs from the question vocabulary: yard (depot), yardmaster (manager), area
  (region), area steward (regional director), Council (board), Keeper of the Accounts / Presiding
  Member / Clerk to the Council (treasurer / chair / secretary), substantive / interim (confirmed /
  acting), amalgamation (merger), overnight workings (night routes), inundation (flood), stood down
  (closed). Only handbook/glossary.md maps them. The generator asserts that no other file uses a
  question word.
- Every hop is named by description ("the depot that flooded in the spring of 2029"), and each
  true hop has near-duplicates: other inundations the same year, a near-miss report, a corrected
  notice, a draft paper, a canteen rumour, interim covers, rescinded appointments, a declined
  amalgamation.
- Authority lives in prose: drafts say they were never adopted, rumours say they are rumours,
  and later minutes rescind or reverse earlier ones.

One resolver answers every question under the true reading and under each trap: count_interim,
trust_draft, trust_rumour, first_notice, near_miss. A question no trap changes is never selected,
and a trap that changes no selected question aborts the build.

Usage: python gen_world.py   (paths are relative to this file)
"""
import json
import random
import re
import shutil
import sys
from datetime import date, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
TASK = HERE.parent
sys.path.insert(0, str(HERE))
from prose import Prose  # noqa: E402

CFG = json.load(open(HERE / "build.json", encoding="utf-8"))
rng = random.Random(CFG["seed"])
START, END = date(2026, 1, 1), date(2033, 12, 31)
TRAPS = ["count_interim", "trust_draft", "trust_rumour", "first_notice", "near_miss"]
SEASONS = {"spring": (3, 5), "summer": (6, 8), "autumn": (9, 11)}
MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
          "November", "December"]
ROLE_TITLE = {"treasurer": "Keeper of the Accounts", "chair": "Presiding Member", "secretary": "Clerk to the Council"}
WORDS = ["", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
         "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen"]
# Question vocabulary. No file except handbook/glossary.md may contain these (checked after rendering).
BANNED = re.compile(r"\b(depots?|manag\w*|regions?|regional|director\w*|treasurer\w*|chair\w*|secretar\w*|flood\w*|"
                    r"merg\w*|approv\w*|night\w*|routes?|confirm\w*|acting|operating|clos\w*|board)\b", re.I)


# ------------------------------------------------------------------ names (invented)
def syl_name(r, onsets, vowels, ends):
    return (r.choice(onsets) + r.choice(vowels) + r.choice(["n", "r", "l", "s", "m", "d", "st", "rr", "nd"]) + r.choice(ends)).capitalize()


FIRST = ["Aldous", "Beatrix", "Casimir", "Delphine", "Emrys", "Florian", "Gisela", "Hamish", "Ines", "Joachim", "Kerensa",
         "Leopold", "Maren", "Niamh", "Oswin", "Perpetua", "Quentin", "Rosalind", "Severin", "Thea", "Ulric", "Verity",
         "Wendell", "Xanthe", "Yusuf", "Zelda", "Anouk", "Bertram", "Clemency", "Dashiell", "Elowen", "Fergus",
         "Honor", "Idris", "Jolyon", "Katya", "Lorcan", "Mireille", "Noor", "Ottoline", "Piran", "Rhiannon", "Saoirse",
         "Tobias", "Ursula", "Valentin", "Winifred", "Anselm", "Brigid", "Cosmo", "Dagny", "Evander", "Fenella",
         "Gideon", "Hesper", "Isolde", "Jasper", "Kit", "Linnea", "Magnus", "Nell", "Orla", "Peregrine", "Ravi",
         "Sabine", "Tamsin", "Ulla", "Vashti", "Wystan", "Ysolde", "Zoltan"]
ON = ["b", "br", "c", "d", "dr", "f", "g", "gr", "h", "k", "l", "m", "n", "p", "r", "s", "st", "t", "th", "v", "w"]
VO = ["a", "e", "i", "o", "u", "ai", "ea", "ou", "y"]
SUR_END = ["ley", "ton", "wick", "more", "sen", "ard", "holt", "ess", "an", "ridge", "low", "ey"]
YARD_END = ["by", "stead", "holm", "combe", "thwaite", "ham", "mouth", "bury", "field", "hope", "cote", "garth"]
AREA_END = ["ane", "ish", "and", "ow", "ary"]

used = set()


def unique(fn):
    while True:
        n = fn()
        if n.lower() not in used and len(n) >= 5:
            used.add(n.lower())
            return n


yard_names = [unique(lambda: syl_name(rng, ON, VO, YARD_END)) for _ in range(CFG["yards"] + 20)]
area_names = [unique(lambda: syl_name(rng, ON, VO, AREA_END)) for _ in range(CFG["areas"])]
surnames = [unique(lambda: syl_name(rng, ON, VO, SUR_END)) for _ in range(CFG["people"] + CFG["background_people"])]
people_all = []
for s in surnames:
    people_all.append(f"{rng.choice(FIRST)} {s}")
core_people = people_all[:CFG["people"]]
background = people_all[CFG["people"]:]
spare_yard_names = yard_names[CFG["yards"]:]
yards = {n: {"name": n, "opened": START, "gone": None, "area0": None} for n in yard_names[:CFG["yards"]]}
for i, n in enumerate(yards):
    yards[n]["area0"] = area_names[i % len(area_names)] if i < 2 * len(area_names) else rng.choice(area_names)

person_iter = iter(rng.sample(core_people, len(core_people)))


def next_person():
    global person_iter
    try:
        return next(person_iter)
    except StopIteration:
        person_iter = iter(rng.sample(core_people, len(core_people)))
        return next(person_iter)


def meet(y, m):
    return date(y, m, 5 + (y * 7 + m * 5) % 20)


def rand_meet(lo, hi):
    """A Council meeting date in [lo, hi]."""
    cands = [meet(y, m) for y in range(lo.year, hi.year + 1) for m in range(1, 13) if lo <= meet(y, m) <= hi]
    return rng.choice(cands) if cands else None


def fmt(d):
    return rng.choice([f"{d.day} {MONTHS[d.month - 1]} {d.year}", f"{MONTHS[d.month - 1]} {d.day}, {d.year}",
                       f"the {d.day}{'th' if 10 < d.day < 14 else {1: 'st', 2: 'nd', 3: 'rd'}.get(d.day % 10, 'th')} of {MONTHS[d.month - 1]} {d.year}"])


def num(n):
    return WORDS[n] if n < len(WORDS) and rng.random() < 0.6 else str(n)


# ------------------------------------------------------------------ claims
APPTS = {}      # post -> [claim]
TRANSFERS = []  # claim(yard, to, date, src, cancelled)
CLOSURES = []   # claim(yard, date, src, reopen)
MERGERS = {}    # year -> claim(a, b, formed, date, approver, draft_approver)
REJECTED = {}   # year -> claim(a, b, proposed, date, mover)
FLOODS = []     # claim(yard, date, season, takeover:{to, n}, correction, draft, rumour)
NEAR_MISS = []  # (yard, date)
PRIOR = {}      # person -> {"yard": substantive yard before the Council, "cover": interim yard after that}


def claim(**kw):
    kw.setdefault("doc", None)
    return kw


def gen_post(post, t0, gap=(8, 22)):
    lst = APPTS.setdefault(post, [])
    d = rand_meet(t0, t0 + timedelta(days=60)) or t0
    cur = next_person()
    lst.append(claim(kind="sub", person=cur, date=d, src="official", rescinded=None, post=post))
    while True:
        d = rand_meet(d + timedelta(days=30 * gap[0]), d + timedelta(days=30 * gap[1]))
        if d is None or d > END - timedelta(days=60):
            break
        roll = rng.random()
        p = next_person()
        if roll < 0.55:
            lst.append(claim(kind="sub", person=p, date=d, src="official", rescinded=None, post=post))
            cur = p
        elif roll < 0.82:
            lst.append(claim(kind="int", person=p, date=d, src="official", rescinded=None, post=post, covering=cur))
        else:
            rd = rand_meet(d + timedelta(days=25), d + timedelta(days=100))
            lst.append(claim(kind="sub", person=p, date=d, src="official", rescinded=rd, post=post))
    if rng.random() < CFG["rumour_appt_rate"]:
        d = START + timedelta(days=rng.randint(200, (END - START).days - 200))
        lst.append(claim(kind="sub", person=next_person(), date=d, src="rumour", rescinded=None, post=post))


def exists(y, d):
    Y = yards[y]
    if Y["opened"] > d or (Y["gone"] and Y["gone"] <= d):
        return False
    return in_service(y, d, frozenset())


# ------------------------------------------------------------------ resolver (trap-aware)
def appts_for(post, T):
    out = []
    for a in APPTS.get(post, []):
        if a["src"] == "rumour" and "trust_rumour" not in T:
            continue
        if a["rescinded"] and "first_notice" not in T:
            continue
        if a["kind"] == "int" and "count_interim" not in T:
            continue
        out.append(a)
    return sorted(out, key=lambda a: a["date"])


def holder(post, d, T, ev=None):
    c = [a for a in appts_for(post, T) if a["date"] <= d]
    if not c:
        return None
    if ev is not None:
        ev.append(c[-1])
    return c[-1]["person"]


def succession(post, T, ev=None):
    seen = []
    for a in appts_for(post, T):
        if a["person"] not in seen:
            seen.append(a["person"])
            if ev is not None:
                ev.append(a)
    return seen


def area_of(y, d, T, ev=None):
    a = yards[y]["area0"]
    for t in sorted(TRANSFERS, key=lambda t: t["date"]):
        if t["yard"] != y or t["date"] > d:
            continue
        ok = (t["src"] == "official" and (not t["cancelled"] or "first_notice" in T)) or \
             (t["src"] == "draft" and "trust_draft" in T) or (t["src"] == "rumour" and "trust_rumour" in T)
        if ok:
            a = t["to"]
            if ev is not None:
                ev.append(t)
    return a


def in_service(y, d, T):
    Y = yards[y]
    if Y["opened"] > d or (Y["gone"] and Y["gone"] <= d):
        return False
    closed = False
    for c in sorted(CLOSURES, key=lambda c: c["date"]):
        if c["yard"] != y or c["date"] > d:
            continue
        if c["src"] == "official":
            closed = True
            if c["reopen"] and c["reopen"] <= d and "first_notice" not in T:
                closed = False
        elif (c["src"] == "draft" and "trust_draft" in T) or (c["src"] == "rumour" and "trust_rumour" in T):
            closed = True
    return not closed


def flood_for(season, year, T, ev=None):
    same = [f for f in FLOODS if f["date"].year == year]
    hit = [f for f in same if f["season"] == season]
    if "near_miss" in T:
        hit = [f for f in same if f["season"] != season][:1]
    if ev is not None and hit:
        ev.append(hit[0])
    return hit[0] if hit else None


def takeover(f, T, ev=None):
    if "trust_draft" in T and f["draft"]:
        return f["draft"], f["takeover"]["n"]
    if "trust_rumour" in T and f["rumour"]:
        return f["rumour"], f["takeover"]["n"]
    t = f["takeover"] if (not f["correction"] or "first_notice" in T) else f["correction"]
    if ev is not None:
        ev.append(t)
    return t["to"], t["n"]


def merger_approver(year, T, ev=None):
    m = MERGERS[year]
    if ev is not None:
        ev.append(m)
    if "trust_draft" in T and m["draft_approver"]:
        return m["draft_approver"]
    return m["approver"]


def merger_formed(year, T, ev=None):
    if "trust_draft" in T and year in REJECTED:
        return REJECTED[year]["proposed"]
    if ev is not None:
        ev.append(MERGERS[year])
    return MERGERS[year]["formed"]


# ------------------------------------------------------------------ world events
for a in area_names:
    gen_post(("area", a), START, (10, 26))
for role in ROLE_TITLE:
    gen_post(("board", role), START, (9, 20))
for y in list(yards):
    gen_post(("yard", y), START, (10, 30))

# area transfers (official, some later cancelled; declined papers; rumours)
for kind, n in (("official", CFG["transfers"]), ("draft", CFG["draft_transfers"]), ("rumour", CFG["rumour_transfers"])):
    for _ in range(n):
        y = rng.choice(list(yards))
        d = rand_meet(date(2026, 6, 1), date(2032, 6, 1))
        cur = area_of(y, d, frozenset())
        to = rng.choice([a for a in area_names if a != cur])
        cancelled = rand_meet(d + timedelta(days=25), d + timedelta(days=90)) if kind == "official" and rng.random() < CFG["transfer_cancel_rate"] else None
        TRANSFERS.append(claim(yard=y, to=to, date=d, src=kind, cancelled=cancelled))

# amalgamations: one per year, approved on the motion of the substantive steward of the area
for yr in CFG["merger_years"]:
    for _ in range(200):
        d = rand_meet(date(yr, 2, 1), date(yr, 11, 30))
        a0 = rng.choice(area_names)
        cands = [y for y in yards if exists(y, d) and area_of(y, d, frozenset()) == a0]
        stew = holder(("area", a0), d, frozenset())
        others = [holder(("area", a), d, frozenset()) for a in area_names if a != a0]
        if len(cands) >= 4 and stew and all(others):
            break
    a, b, ra, rb = rng.sample(cands, 4)
    formed = spare_yard_names.pop()
    yards[formed] = {"name": formed, "opened": d, "gone": None, "area0": a0}
    yards[a]["gone"] = yards[b]["gone"] = d
    MERGERS[yr] = claim(a=a, b=b, formed=formed, date=d, approver=stew, area=a0,
                        draft_approver=rng.choice([o for o in others if o != stew]))
    REJECTED[yr] = claim(a=ra, b=rb, proposed=spare_yard_names.pop(), date=rand_meet(date(yr, 1, 1), date(yr, 12, 20)),
                         mover=next_person())
    gen_post(("yard", formed), d, (6, 16))

# stand-downs (official, some reversed), declined proposals, rumours
for kind, n in (("official", CFG["closures"]), ("draft", CFG["draft_closures"]), ("rumour", CFG["rumour_closures"])):
    for _ in range(n):
        for _ in range(100):
            y = rng.choice(list(yards))
            d = rand_meet(date(2027, 1, 1), date(2033, 6, 1))
            if exists(y, d) and not any(c["yard"] == y for c in CLOSURES):
                break
        reopen = rand_meet(d + timedelta(days=120), d + timedelta(days=600)) if kind == "official" and rng.random() < CFG["reopen_rate"] else None
        CLOSURES.append(claim(yard=y, date=d, src=kind, reopen=reopen))

# inundations: 2-3 per year in distinct seasons, each with a takeover and its near-duplicates
for yr in range(2027, 2033):
    for season in rng.sample(list(SEASONS), rng.choice([2, 3, 3])):
        lo, hi = SEASONS[season]
        d = date(yr, rng.randint(lo, hi), rng.randint(1, 27))
        live = [y for y in yards if exists(y, d)]
        y = rng.choice(live)
        area = area_of(y, d, frozenset())
        same_area = [z for z in live if z != y and area_of(z, d, frozenset()) == area] or [z for z in live if z != y]
        to = rng.choice(same_area)
        rest = [z for z in live if z not in (y, to)]
        corr = rng.choice(rest) if rng.random() < CFG["correction_rate"] else None
        rest2 = [z for z in rest if z != corr]
        draft = rng.choice(rest2) if rng.random() < CFG["draft_takeover_rate"] else None
        rumour = rng.choice([z for z in rest2 if z != draft]) if rng.random() < CFG["rumour_takeover_rate"] else None
        n = rng.randint(4, 16)
        FLOODS.append(claim(yard=y, date=d, season=season,
                            takeover=claim(to=to, n=n, date=d + timedelta(days=rng.randint(3, 9))),
                            correction=claim(to=corr, n=rng.randint(4, 16), date=d + timedelta(days=rng.randint(12, 30))) if corr else None,
                            draft=draft, rumour=rumour))
    for _ in range(CFG["near_misses_per_year"]):
        season = rng.choice(list(SEASONS))
        lo, hi = SEASONS[season]
        d = date(yr, rng.randint(lo, hi), rng.randint(1, 27))
        NEAR_MISS.append((rng.choice([y for y in yards if exists(y, d)]), d))
FLOODS.sort(key=lambda f: f["date"])

# Council members' careers before the Council: substantive yard, then an interim cover elsewhere
for post, lst in APPTS.items():
    if post[0] == "board":
        for a in lst:
            if a["person"] not in PRIOR:
                y1, y2 = rng.sample(list(yards)[:CFG["yards"]], 2)
                PRIOR[a["person"]] = {"yard": y1, "cover": y2, "from": rng.randint(2008, 2016), "years": rng.randint(4, 9)}


# ------------------------------------------------------------------ question families
def dec31(y):
    return date(y, 12, 31)


def f1(p, T, ev=None):
    f = flood_for(p["season"], p["y1"], T, ev)
    if not f:
        return None
    dest, _ = takeover(f, T, ev)
    return holder(("yard", dest), dec31(p["y2"]), T, ev)


def f6(p, T, ev=None):
    f = flood_for(p["season"], p["y1"], T, ev)
    if not f:
        return None
    dest, _ = takeover(f, T, ev)
    d = p["d"]
    return holder(("area", area_of(dest, d, T, ev)), d, T, ev)


def f2(p, T, ev=None):
    succ = succession(("board", p["role"]), T, ev)
    i = p["k"] - 1 + (1 if "near_miss" in T else 0)
    if i >= len(succ):
        return None
    pr = PRIOR[succ[i]]
    return pr["cover"] if "count_interim" in T else pr["yard"]


def f3(p, T, ev=None):
    m = MERGERS[p["ym"]]
    who = merger_approver(p["ym"], T, ev)
    region = next((a for a in area_names if holder(("area", a), m["date"], T) == who), None)
    if region is None:
        return None
    end = dec31(p["y3"])
    d = date(p["y3"], 1, 1) if "near_miss" in T else end
    n = 0
    for y in yards:
        pts = [yards[y]["opened"]] + [t["date"] for t in TRANSFERS if t["yard"] == y and t["date"] <= end] + \
              [t["cancelled"] for t in TRANSFERS if t["yard"] == y and t.get("cancelled") and t["cancelled"] <= end]
        if any(area_of(y, t, T) == region for t in pts if t <= end) and in_service(y, d, T):
            n += 1
    return n


def f4(p, T, ev=None):
    f0 = flood_for(p["season"], p["y1"], frozenset(), ev)
    region = area_of(f0["yard"], dec31(p["yf"]) if "near_miss" in T else date(p["yf"], 1, 1), T, ev)
    total = 0
    for f in FLOODS:
        if f["date"].year == p["yf"]:
            dest, n = takeover(f, T, ev)
            if area_of(dest, f["date"], T) == region:
                total += n
    return total


def f5(p, T, ev=None):
    c = merger_formed(p["ym"], T, ev)
    end = date(p["y2"], 1, 1) if "near_miss" in T else dec31(p["y2"])
    return len({a["person"] for a in appts_for(("yard", c), T) if a["date"] <= end})


FAM = {"F1": f1, "F2": f2, "F3": f3, "F4": f4, "F5": f5, "F6": f6}
ORD = {1: "first", 2: "second", 3: "third", 4: "fourth"}


def qtext(fam, p):
    if fam == "F1":
        return (f"At the end of {p['y2']}, who was the confirmed manager of the depot that took over the night routes "
                f"of the depot that flooded in the {p['season']} of {p['y1']}?")
    if fam == "F6":
        d = p["d"]
        return (f"On {d.day} {MONTHS[d.month - 1]} {d.year}, who was the confirmed regional director responsible for the "
                f"depot that took over the night routes of the depot that flooded in the {p['season']} of {p['y1']}?")
    if fam == "F2":
        return (f"Which depot did the {ORD[p['k']]} person to hold the {p['role']} role on a confirmed basis last run, as "
                f"its confirmed manager, before joining the board?")
    if fam == "F3":
        return (f"How many of the depots that were ever part of the region directed by the person who approved the "
                f"{p['ym']} merger (the region that person directed when approving it) were still operating at the end of {p['y3']}?")
    if fam == "F4":
        return (f"In total, how many night routes were handed to depots in the region that the depot flooded in the "
                f"{p['season']} of {p['y1']} belonged to at the start of {p['yf']}, because of floods during {p['yf']}?")
    if fam == "F5":
        return (f"How many different people held the confirmed manager post at the depot formed by the {p['ym']} merger, "
                f"from its formation to the end of {p['y2']}?")


def candidates(fam):
    out = []
    if fam in ("F1", "F6", "F4"):
        for f in FLOODS:
            y1 = f["date"].year
            if fam == "F1":
                out += [{"season": f["season"], "y1": y1, "y2": y2} for y2 in range(y1, min(y1 + 3, 2034))]
            elif fam == "F6":
                out += [{"season": f["season"], "y1": y1, "d": date(yy, mm, 30)} for yy in range(y1, min(y1 + 3, 2034)) for mm in (6, 11)]
            else:
                out += [{"season": f["season"], "y1": y1, "yf": yf} for yf in (y1, y1 + 1) if any(g["date"].year == yf for g in FLOODS)]
    elif fam == "F2":
        out = [{"role": r, "k": k} for r in ROLE_TITLE for k in (2, 3, 4)]
    elif fam == "F3":
        out = [{"ym": ym, "y3": y3} for ym in MERGERS for y3 in range(ym, 2034)]
    elif fam == "F5":
        out = [{"ym": ym, "y2": y2} for ym in MERGERS for y2 in range(ym + 1, 2034)]
    return out


def evaluate(fam, p):
    fn = FAM[fam]
    gold = fn(p, frozenset())
    if gold is None or (isinstance(gold, int) and gold < 2):
        return None
    ta = {t: fn(p, frozenset([t])) for t in TRAPS}
    flips = [t for t in TRAPS if ta[t] != gold]
    return {"gold": gold, "trap": ta, "flips": flips} if flips else None


pool = {}
for fam in FAM:
    pool[fam] = []
    for p in candidates(fam):
        e = evaluate(fam, p)
        if e:
            pool[fam].append({"fam": fam, "p": p, **e})
    rng.shuffle(pool[fam])
    pool[fam].sort(key=lambda c: -len(c["flips"]))

chosen, golds, keys_used = [], set(), set()
for fam, n in CFG["family_counts"].items():
    for c in pool[fam]:
        if sum(1 for x in chosen if x["fam"] == fam) >= n:
            break
        ent = (fam, c["p"].get("season"), c["p"].get("y1"), c["p"].get("ym"), c["p"].get("role"))
        if ent in keys_used or (isinstance(c["gold"], str) and c["gold"] in golds):
            continue
        keys_used.add(ent)
        golds.add(c["gold"]) if isinstance(c["gold"], str) else None
        chosen.append(c)
    if sum(1 for x in chosen if x["fam"] == fam) < n:
        sys.exit(f"family {fam}: only {sum(1 for x in chosen if x['fam'] == fam)} usable candidates")
order = rng.sample(chosen, len(chosen))  # drawn before rendering, so prose changes never reorder the key
dead = [t for t in TRAPS if not any(t in c["flips"] for c in chosen)]
if dead:
    sys.exit(f"traps that flip no chosen question: {dead}")


# ------------------------------------------------------------------ rendering
P = Prose(random.Random(CFG["seed"] + 7), background, list(yards))
files = {}
docno = {"council": 0, "notices": 0, "reports": 0, "papers": 0, "forum": 0, "misc": 0}


def emit(rel, text):
    assert rel not in files, rel
    files[rel] = text.rstrip("\n") + "\n"


def new_doc(folder, prefix):
    docno[folder] += 1
    return f"{folder}/{prefix}-{docno[folder]:04d}.md"


def post_phrase(post):
    kind, x = post
    if kind == "yard":
        return rng.choice([f"yardmaster at {x}", f"yardmaster of the {x} yard", f"the {x} yardmaster's post"])
    if kind == "area":
        return rng.choice([f"area steward for {x}", f"steward of the {x} area", f"the {x} stewardship"])
    return ROLE_TITLE[x]


def appt_text(a):
    who, post = a["person"], post_phrase(a["post"])
    if a["kind"] == "int":
        cov = a.get("covering")
        return rng.choice([
            f"{who} will cover as interim {post} while {cov} is on secondment; {cov} remains the substantive holder.",
            f"The Council asked {who} to act as caretaker ({post}) for a period, {cov} keeping the substantive post throughout.",
            f"For the coming months {who} steps in on an interim basis as {post}. This is cover, not a substantive appointment.",
        ])
    return rng.choice([
        f"The Council appointed {who} as substantive {post}.",
        f"{who} was appointed {post} on a substantive basis, with immediate effect.",
        f"Following interviews, {who} takes up the post of {post} as substantive holder.",
    ])


P.scale = CFG["fact_doc_scale"]
council_items = {}  # meeting date -> [text]


def add_item(d, text, c=None, kind="council"):
    council_items.setdefault(d, []).append((text, c))


for post, lst in APPTS.items():
    for a in lst:
        if a["src"] == "official":
            add_item(a["date"], appt_text(a), a)
            if a["rescinded"]:
                add_item(a["rescinded"], rng.choice([
                    f"The minute of {fmt(a['date'])} appointing {a['person']} as {post_phrase(post)} is rescinded: {a['person']} withdrew before taking up the post, and the previous arrangement stands.",
                    f"{a['person']} has declined the appointment as {post_phrase(post)} made on {fmt(a['date'])}. That appointment is void.",
                ]), a)
for t in TRANSFERS:
    if t["src"] == "official":
        add_item(t["date"], rng.choice([
            f"The {t['yard']} yard moves into the {t['to']} area, and reports to that area's steward from this meeting.",
            f"Agreed that {t['yard']} should in future sit within the {t['to']} area.",
        ]), t)
        if t["cancelled"]:
            add_item(t["cancelled"], rng.choice([
                f"The move of {t['yard']} into the {t['to']} area, agreed on {fmt(t['date'])}, is cancelled; the yard stays where it was.",
                f"Members reversed the earlier decision to place {t['yard']} in the {t['to']} area. No change takes effect.",
            ]), t)
for c in CLOSURES:
    if c["src"] == "official":
        add_item(c["date"], rng.choice([
            f"The Council agreed to stand down the {c['yard']} yard. Staff will be redeployed.",
            f"{c['yard']} is to be mothballed and taken out of use.",
        ]), c)
        if c["reopen"]:
            add_item(c["reopen"], rng.choice([
                f"The {c['yard']} yard, stood down on {fmt(c['date'])}, is to return to use.",
                f"Members agreed to bring {c['yard']} back into use after its period in mothballs.",
            ]), c)
for yr, m in MERGERS.items():
    add_item(m["date"], rng.choice([
        f"On the motion of {m['approver']}, the Council agreed the amalgamation of the {m['a']} and {m['b']} yards into a single yard, to be called {m['formed']}. The motion was carried.",
        f"{m['approver']} moved that {m['a']} and {m['b']} be amalgamated as {m['formed']}; carried without dissent.",
    ]), m)
for yr, r in REJECTED.items():
    add_item(r["date"], rng.choice([
        f"The proposal, moved by {r['mover']}, to amalgamate {r['a']} and {r['b']} as '{r['proposed']}' was declined. No further action.",
        f"Members considered and turned down the idea of combining {r['a']} with {r['b']} under the name {r['proposed']}.",
    ]), r)

for d in sorted(council_items):
    rel = new_doc("council", "minutes")
    body = [f"# Council minutes, {fmt(d)}", "", f"Present: {', '.join(rng.sample(core_people, rng.randint(5, 9)))}.", ""]
    items = council_items[d][:]
    fill = [(P.board_filler_item(), None) for _ in range(rng.randint(3, 6))]
    items = fill[:1] + items + fill[1:]
    for i, (text, c) in enumerate(items):
        body += [f"## Item {i + 1}", "", text, ""]
        if c is not None and c.get("doc") is None:
            c["doc"] = rel
        elif c is not None:
            c.setdefault("docs", []).append(rel)
    emit(rel, "\n".join(body))

# inundation reports, traffic notices, corrections
for f in FLOODS:
    rel = new_doc("reports", "report")
    f["doc"] = rel
    emit(rel, f"""# Incident report: high water at {f['yard']}

Date of incident: {fmt(f['date'])}.

{rng.choice([f"Heavy rain upstream sent the brook over its banks and water entered the {f['yard']} yard to a depth of about {rng.randint(20, 90)} centimetres.",
             f"The {f['yard']} yard was inundated after the culvert under the access road failed during a prolonged downpour.",
             f"Water came through the gatehouse and across the loading apron at {f['yard']}; the yard could not be used for several weeks."])}
{P.para([lambda: P.board_filler_item()], 1, 2)}

{rng.choice(["Insurers have been notified.", "Loss adjusters visited the following week.", "A full survey is to follow."])}
The yard lead thanked {P.p()} and {P.p()} for moving stock to the upper floor.
""")
    t = f["takeover"]
    rel = new_doc("notices", "notice")
    t["doc"] = rel
    emit(rel, f"""# Traffic notice, {fmt(t['date'])}

{P.para([P.forum_chatter], 1, 1)}

{rng.choice([f"Following the inundation at {f['yard']}, its overnight workings ({num(t['n'])} in all) pass to {t['to']} until further notice.",
             f"With {f['yard']} out of use, the {num(t['n'])} overnight workings that ran from there are reallocated to the {t['to']} yard.",
             f"{t['to']} will take on the overnight workings from {f['yard']}, {num(t['n'])} of them, while the water damage is repaired."])}

Daytime work from {f['yard']} is being spread across neighbouring yards and is not covered by this notice.
""")
    if f["correction"]:
        c = f["correction"]
        rel = new_doc("notices", "notice")
        c["doc"] = rel
        emit(rel, f"""# Traffic notice, {fmt(c['date'])}

{rng.choice([f"Correction. The earlier notice about the overnight workings from {f['yard']} named the wrong yard. They go to {c['to']}, not {t['to']}, and there are {num(c['n'])} of them.",
             f"Please disregard the allocation in the notice of {fmt(t['date'])}: {t['to']} could not take the extra work. The {f['yard']} overnight workings ({num(c['n'])}) pass to {c['to']} instead.",
             f"Update on {f['yard']}: after the first notice, planners moved the overnight workings again. {c['to']} now has all {num(c['n'])}; {t['to']} has none of them."])}
""")
    if f["draft"]:
        rel = new_doc("papers", "paper")
        emit(rel, f"""# Paper: covering {f['yard']} while it dries out

{rng.choice(["This is a working draft for discussion only. Nothing in it has been agreed and it should not be quoted as a decision.",
             "Circulated for comment. The planners' final allocation may differ; this paper was never adopted.",
             "Early thinking only. The traffic team will issue the real allocation separately."])}

The obvious candidate to absorb the overnight workings from {f['yard']} is {f['draft']}, which has spare bays after dark.
{P.board_filler_item()}
""")
    if f["rumour"]:
        rel = new_doc("forum", "thread")
        emit(rel, f"""# Forum: {f['yard']} after the water

**{P.p()}:** {P.forum_chatter()}

**{P.p()}:** {rng.choice([f"Heard in the canteen the {f['yard']} overnight workings are all going to {f['rumour']}. Could be nonsense.",
                         f"My cousin reckons {f['rumour']} is getting the {f['yard']} overnight workings. Nothing official yet.",
                         f"Rumour mill says {f['rumour']} for the overnight stuff from {f['yard']}. Take with salt."])}

**{P.p()}:** {rng.choice(["Wait for the notice.", "Believe it when I see it.", "That's not what I heard.", P.forum_chatter()])}
""")
for y, d in NEAR_MISS:
    rel = new_doc("reports", "report")
    emit(rel, f"""# Incident report: high water near {y}

Date: {fmt(d)}.

{rng.choice([f"The brook rose to within a metre of the {y} gate but the yard itself stayed dry and work carried on as normal.",
             f"Water covered the lane outside {y} for an afternoon. Nothing entered the yard and no workings were moved.",
             f"Sandbags went down at {y} as a precaution. The water never reached the yard."])}

{P.board_filler_item()}
""")

# declined / rumoured transfers and stand-downs, rumoured appointments
for t in TRANSFERS:
    if t["src"] == "draft":
        emit(new_doc("papers", "paper"), f"""# Paper: should {t['yard']} change area?

{P.board_filler_item()}

This paper proposes moving {t['yard']} into the {t['to']} area. {rng.choice(["The Council did not take it forward.", "It was discussed and set aside; nothing changed.", "This is a draft; it was never put to a vote."])}
""")
    elif t["src"] == "rumour":
        emit(new_doc("forum", "thread"), f"""# Forum: area shake-up?

**{P.p()}:** Someone said {t['yard']} is going into the {t['to']} area. Anyone know?

**{P.p()}:** {rng.choice(["First I've heard.", "Pure gossip as far as I know.", "Probably just talk."])}
""")
for c in CLOSURES:
    if c["src"] == "draft":
        emit(new_doc("papers", "paper"), f"""# Paper: the future of {c['yard']}

A draft for discussion. It argues that {c['yard']} could be mothballed to save money. {rng.choice(["The Council declined the proposal.", "No decision was taken on this paper.", "Members were not persuaded; the yard stays in use."])}

{P.board_filler_item()}
""")
    elif c["src"] == "rumour":
        emit(new_doc("forum", "thread"), f"""# Forum: {c['yard']} for the chop?

**{P.p()}:** Heard {c['yard']} is being mothballed. {rng.choice(["No idea if it's true.", "Can anyone confirm?".replace("confirm", "back that up"), "Could be scaremongering."])}

**{P.p()}:** {P.forum_chatter()}
""")
for post, lst in APPTS.items():
    for a in lst:
        if a["src"] == "rumour":
            rel = new_doc("forum", "thread")
            a["doc"] = rel
            emit(rel, f"""# Forum: new faces

**{P.p()}:** Word is {a['person']} is getting the post of {post_phrase(post)}. {rng.choice(["Not announced yet.", "Just canteen talk.", "Don't quote me."])}

**{P.p()}:** {P.forum_chatter()}
""")
for yr, m in MERGERS.items():
    emit(new_doc("papers", "paper"), f"""# Paper: combining {m['a']} and {m['b']}

Draft for comment, prepared before the Council meeting. {rng.choice(["It was superseded by the decision recorded in the minutes.", "The minutes, not this paper, record what was decided."])}

We expect {m['draft_approver']} to move the amalgamation, and suggest the combined yard be named {m['formed']}.
{P.board_filler_item()}
""")

# people
person_file = {}
for i, p in enumerate(sorted(core_people)):
    rel = f"people/p-{1001 + i}.md"
    person_file[p] = rel
    if p in PRIOR:
        pr = PRIOR[p]
        career = (f"{p} began as a driver and ran the {pr['yard']} yard as its substantive yardmaster from {pr['from']} for "
                  f"{pr['years']} years. {rng.choice(['Afterwards', 'After that', 'Later'])} came a spell as interim cover at "
                  f"{pr['cover']}, looking after the yard while its own yardmaster was away, before election to the Council.")
    else:
        career = f"{p} {rng.choice(['came up through the traffic office', 'started in the workshop', 'joined from a rival carrier', 'began as a yard hand'])} and {rng.choice(['knows most of the yards well', 'is a familiar face in the canteen', 'has trained many new starters'])}."
    emit(rel, f"# {p}\n\n{career}\n\n{P.para([P.forum_chatter], 1, 2)}\n")

# yard pages: a stale snapshot, dated
for i, (y, Y) in enumerate(sorted(yards.items())):
    snap = date(2026, 3, 1) if Y["opened"] == START else Y["opened"] + timedelta(days=40)
    emit(f"yards/y-{101 + i}.md", f"""# {y}

Page last updated {fmt(snap)}. Details may have changed since.

- Area at that time: {area_of(y, snap, frozenset())}
- Yardmaster at that time: {holder(('yard', y), snap, frozenset()) or 'vacant'}

{P.local_history()}
""")

# handbook: the only file with the question vocabulary
emit("handbook/glossary.md", """# Glossary of co-operative terms

Newcomers from other firms use different words. In this wiki:

| Elsewhere | Here |
|---|---|
| depot | yard |
| depot manager | yardmaster |
| region | area |
| regional director | area steward |
| board (of directors) | the Council |
| chair of the board | Presiding Member |
| treasurer | Keeper of the Accounts |
| company secretary | Clerk to the Council |
| confirmed (permanent) appointment | substantive appointment |
| acting appointment | interim appointment, caretaker, cover |
| merger | amalgamation |
| closed | stood down, mothballed, taken out of use |
| night routes | overnight workings |
| flood | inundation, high water |

Only Council minutes and traffic notices record decisions. Papers are drafts for discussion, and the
staff forum is gossip. A later minute or notice overrides an earlier one.
""")

# filler
fill_kinds = [
    lambda d: P.weather_log(fmt(d)), lambda d: P.canteen(f"{MONTHS[d.month - 1]} {d.year}"), lambda d: P.match_report(),
    lambda d: P.maintenance(rng.randint(100, 999)), lambda d: P.local_history(), lambda d: P.book_club(),
    lambda d: P.allotment(f"{MONTHS[d.month - 1]} {d.year}"), lambda d: P.toolbox_talk(), lambda d: P.letter(),
    lambda d: P.postcard(), lambda d: P.training(), lambda d: P.tribute(), lambda d: P.kit_review(),
    lambda d: P.social_committee(f"{MONTHS[d.month - 1]} {d.year}"),
]
P.scale = CFG["filler_scale"]
for i in range(CFG["filler_docs"]):
    d = START + timedelta(days=rng.randint(0, (END - START).days))
    emit(new_doc("misc", "doc"), fill_kinds[i % len(fill_kinds)](d) if i < len(fill_kinds) else rng.choice(fill_kinds)(d))

# ------------------------------------------------------------------ checks
leaks = [(rel, m.group(0)) for rel, t in files.items() if rel != "handbook/glossary.md" for m in BANNED.finditer(t)]
if leaks:
    sys.exit(f"question vocabulary leaked into {len(leaks)} files: {sorted(set(w.lower() for _, w in leaks))} e.g. {leaks[:3]}")

out = TASK / "fixtures"
if out.exists():
    shutil.rmtree(out)
total = 0
for rel, text in files.items():
    p = out / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    total += len(text.encode("utf-8"))


# ------------------------------------------------------------------ key, report, task.md, meta
def ans_list(g):
    if isinstance(g, int):
        return [str(g)]
    if g in yards or any(g == r["proposed"] for r in REJECTED.values()):
        return [g, f"{g} yard", f"{g} depot"]
    return [g]


def evidence(c):
    ev = []
    FAM[c["fam"]](c["p"], frozenset(), ev)
    docs = []
    for x in ev:
        for d in [x.get("doc")] + x.get("docs", []):
            if d and d not in docs:
                docs.append(d)
    if c["fam"] == "F2":
        docs.append(person_file[succession(("board", c["p"]["role"]), frozenset())[c["p"]["k"] - 1]])
    return ["handbook/glossary.md"] + docs


gold = {f"q{i + 1}": c["gold"] for i, c in enumerate(order)}
flips = {f"q{i + 1}": c["flips"] for i, c in enumerate(order)}
trap_answers = {t: {f"q{i + 1}": ("(none)" if c["trap"][t] is None else c["trap"][t]) for i, c in enumerate(order)} for t in TRAPS}
key = {"task": "fw2", "source": "synthetic", "questions": [
    {"id": f"q{i + 1}", "kind": "int" if isinstance(c["gold"], int) else "norm", "question": qtext(c["fam"], c["p"]),
     "answers": ans_list(c["gold"]), "evidence": evidence(c), "family": c["fam"], "trap_flips": c["flips"]}
    for i, c in enumerate(order)]}
with open(HERE / "key.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(key, f, indent=2)
    f.write("\n")
n = len(order)
predicted = {t: round(sum(1 for q in gold if t not in flips[q]) / n, 4) for t in TRAPS}
counts = {k: sum(1 for r in files if r.startswith(k + "/")) for k in
          ["council", "notices", "reports", "papers", "forum", "people", "yards", "misc", "handbook"]}
report = {"task": "fw2", "fixture_files": len(files), "fixture_bytes": total, "approx_tokens": total // 4,
          "gold": gold, "trap_answers": trap_answers, "trap_flips": flips, "predicted_trap_scores": predicted,
          "families": {f"q{i + 1}": c["fam"] for i, c in enumerate(order)}, "counts": counts,
          "candidate_pool": {k: len(v) for k, v in pool.items()}}
with open(HERE / "build-report.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(report, f, indent=2, default=str)
    f.write("\n")

qs = "\n".join(f"- **q{i + 1}.** {qtext(c['fam'], c['p'])}" for i, c in enumerate(order))
msg = f"""Project code name: {{SEED}}.

This workspace is a copy of the staff wiki of the Tessmarrow Carriers' Co-operative, a (fictional)
haulage co-operative: {len(files)} Markdown pages. Nothing in it is about a real organisation or
person, so the answers are only in these pages; general knowledge will not help. Most pages are
everyday staff chatter that has nothing to do with the questions.

The co-operative has its own vocabulary, and the questions below use ordinary business words, so
you will need to work out what the wiki calls things. Not everything written down is true or still
current: some pages are drafts, some are gossip, and later decisions change earlier ones. When a
question pins a date, answer for that date.

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the pages you need.

Write `answers.json` in the workspace root:

```json
{{
  "q1": {{ "answer": "<full name, depot name, or whole number>", "evidence": ["council/....md", "..."] }},
  "q2": {{ "answer": "...", "evidence": ["..."] }}
}}
```

- A person's `answer` is their full name as written in the wiki; a depot's is its name as written
  (for example `Marlbury`); a count or total is a whole number.
- `evidence` lists the pages the answer rests on; it is optional and not scored.
- Include every question from q1 to q{n}. If you cannot pin one down, give your best guess anyway.

Questions:

{qs}

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
"""
header = f"""# fw2 — indirect multi-hop and judged aggregation over a fictional co-operative's wiki (synthetic)

Family `fictional-multihop`, split `dev`. Workspace = `fixtures/` ({len(files)} Markdown pages, {total / 1e6:.2f} MB,
~{total // 4 // 1000}k tokens; mostly varied staff chatter). {n} questions, 3-6 hops each, every hop named by description:
inundation (by season) -> traffic notice (corrected, drafted, rumoured) -> yard -> yardmaster / area steward on a date
(interim covers, rescinded appointments, cancelled area moves), Council-role succession -> career profile, and counts or
sums over yards selected by judgement (amalgamation mover -> area -> yards ever in it -> still in use). The question
vocabulary appears only in handbook/glossary.md. Checker: normalised exact match / integer; score = correct / {n}.
Trap answers and predicted trap-only scores: `hidden/build-report.json`. Generator + knobs: `hidden/gen_world.py`,
`hidden/build.json`."""
with open(TASK / "task.md", "w", encoding="utf-8", newline="\n") as f:
    f.write(header + "\n\n---\n\n" + msg)
meta_path = TASK / "meta.json"
meta = json.load(open(meta_path, encoding="utf-8")) if meta_path.exists() else {
    "family": "fictional-multihop", "split": "dev", "seeds": ["aurora", "basalt", "cascade", "delta", "ember"],
    "timeoutMinutes": 45}
meta["requiredFixtures"] = [{"glob": f"{k}/*.md", "count": v} for k, v in counts.items()]
with open(meta_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(meta, f, indent=2)
    f.write("\n")
print(json.dumps({"files": len(files), "bytes": total, "gold": gold, "flips": flips, "predicted": predicted,
                  "families": report["families"], "pool": report["candidate_pool"]}, indent=1, default=str))
