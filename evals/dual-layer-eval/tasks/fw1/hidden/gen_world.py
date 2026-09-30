"""Deterministic generator for fw1: a fictional freight company's internal wiki (run once; output committed).

Writes fixtures/{people,teams,runbooks,incidents,memos,meetings,status}/*.md, hidden/key.json,
hidden/build-report.json, task.md and meta.json's requiredFixtures. Knobs: hidden/build.json.

The world (Brannock Vale Freight, all names invented) has people, teams, services and incidents.
Facts the questions chain through, and where each lives:

  service <- alias       runbook PROSE ("incident write-ups call it the dock-scheduling API")
  incident -> service    incident PROSE ("root cause was traced to <alias>"; another alias ruled out)
  service -> team        runbook TABLE (as of 2026-01-05), then reorg memos in PROSE, some later
                         postponed or cancelled by another memo
  team -> on-call lead   team page TABLE (weekly rotation), then swaps agreed in meeting-note PROSE
  person <- mention      "Mara O." (first name + initial, resolved by team) or a nickname stated
                         only in the person's profile prose; near-duplicate twins in other teams
  person -> team         profile TABLE, then transfer memos in PROSE
  team -> manager        team page TABLE, then manager-change memos in PROSE

Every question is planted: its entities are reserved and its facts injected, then the answer is
computed by one resolver under the true rules and again under each trap (ignore_memos,
first_announcement, ignore_swaps, near_duplicate, nickname_literal, wrong_date). A planted
question whose answer no trap changes is a generator bug and aborts the build.

Usage: python gen_world.py   (paths are relative to this file)
"""
import json
import random
import shutil
import sys
from datetime import date, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
TASK = HERE.parent
sys.path.insert(0, str(TASK.parent.parent / "tools"))
from filler import Filler  # noqa: E402

CFG = json.load(open(HERE / "build.json", encoding="utf-8"))
rng = random.Random(CFG["seed"])
F = Filler(random.Random(CFG["seed"] + 1))

WEEK1 = date(2025, 12, 29)  # Monday of ISO week 1, 2026
WEEKS = list(range(2, 27))
BASELINE = date(2026, 1, 5)


def monday(w):
    return WEEK1 + timedelta(weeks=w - 1)


def iso_week(d):
    return d.isocalendar()[1]


def prose_date(d):
    return f"{d.day} {d.strftime('%B')} {d.year}"


def weekday_in(w):
    return monday(w) + timedelta(days=rng.randrange(5))


# ------------------------------------------------------------------ people
NICK = {"Rebecca": "Bex", "Alexander": "Sasha", "Katarzyna": "Kasia", "Theodore": "Teddy",
        "Margarethe": "Greta", "Nikolai": "Kolya", "Oluwaseun": "Seun", "Guillermo": "Memo",
        "Francesca": "Cesca", "Bartholomew": "Bart", "Jonathan": "Jonty", "Wilhelmina": "Mina",
        "Ignatius": "Iggy", "Anastasia": "Asya", "Gwendolyn": "Wren", "Konstantin": "Kostya",
        "Rosalind": "Roz", "Siobhan": "Shiv", "Dorothea": "Thea", "Evangelos": "Vangelis"}
PLAIN = ["Mara", "Tomasz", "Ines", "Priya", "Joaquin", "Hedda", "Kwame", "Lucia", "Emeka", "Sanne",
         "Radu", "Yusuf", "Mirela", "Anders", "Chiamaka", "Farid", "Leonie", "Tariq", "Ottilie",
         "Rafael", "Zofia", "Benedikt", "Amara", "Viktor", "Nkechi", "Henrik", "Paloma", "Idris",
         "Linnea", "Matteo", "Saoirse", "Dmitri", "Keziah", "Aurelio", "Freya", "Ciaran"]
SYL_A = ["Ald", "Bren", "Cor", "Dav", "Ell", "Fen", "Gar", "Hal", "Isb", "Jor", "Kel", "Lun", "Mor",
         "Nev", "Ost", "Pell", "Quar", "Ros", "Sten", "Tor", "Ulv", "Vask", "Wen", "Yar", "Zel", "Okaf"]
SYL_B = ["ander", "by", "castle", "dale", "enko", "ford", "gard", "holm", "ico", "ley", "mann",
         "noor", "ova", "quist", "rick", "sen", "tova", "uzzi", "vall", "wick", "or"]


def twin_surname(s, taken):
    vowels = "aeiou"
    idx = [i for i, c in enumerate(s) if c in vowels and i > 0]
    rng.shuffle(idx)
    for i in idx:
        for v in vowels:
            if v != s[i]:
                cand = s[:i] + v + s[i + 1:]
                if cand not in taken:
                    return cand
    raise SystemExit(f"no twin surname for {s}")


teams = [dict(name=n) for n in CFG["teams"]]
people = []
surnames = set()
firsts = list(NICK) + PLAIN
while len(people) < CFG["people"] - CFG["twins"]:
    s = rng.choice(SYL_A) + rng.choice(SYL_B)
    if s in surnames:
        continue
    surnames.add(s)
    people.append(dict(first=rng.choice(firsts), last=s))
for i, p in enumerate(people):
    p["team"] = i % len(teams)
rng.shuffle(people)
# near-duplicate twins: same first name, surname one vowel apart, always in a different team
for p in rng.sample(people, CFG["twins"]):
    s = twin_surname(p["last"], surnames)
    surnames.add(s)
    t = rng.choice([i for i in range(len(teams)) if i != p["team"]])
    q = dict(first=p["first"], last=s, team=t)
    p["twin"] = q
    q["twin"] = p
    people.append(q)
rng.shuffle(people)
used_names = set()
for i, p in enumerate(people):
    p["id"] = f"p-{1001 + i}"
    p["name"] = f"{p['first']} {p['last']}"
    assert p["name"] not in used_names
    used_names.add(p["name"])
nicks = set()
for p in people:
    n = NICK.get(p["first"])
    if n and n not in nicks and rng.random() < 0.7:
        p["nick"] = n
        nicks.add(n)
BY_NAME = {p["name"]: p for p in people}


def members0(t):
    return [p for p in people if p["team"] == t]


for t, tm in enumerate(teams):
    ms = members0(t)
    tm["manager"] = rng.choice(ms)
    order = ms[:]
    rng.shuffle(order)
    tm["rota"] = {w: order[(k) % len(order)] for k, w in enumerate(WEEKS)}


def short(p):  # "Mara O." — unambiguous only inside a team
    return f"{p['first']} {p['last'][0]}."


def short_unique_in_team(p, t):
    return sum(1 for q in members0(t) if short(q) == short(p)) == 1


# ------------------------------------------------------------------ services
PRE = ["Tide", "Ledger", "Dock", "Harbor", "Crane", "Manifest", "Berth", "Quay", "Rail", "Tally",
       "Gate", "Pallet", "Route", "Haul", "Cargo", "Yard", "Keel", "Buoy"]
SUF = ["watch", "line", "keeper", "scope", "book", "flow", "smith", "mark", "relay", "sight", "post", "wright"]
FUNC = ["dock-scheduling", "customs-declaration", "yard-inventory", "carrier-rating", "label-printing",
        "manifest-reconciliation", "rate-quote", "driver-dispatch", "berth-allocation", "invoice-matching",
        "ETA-prediction", "container-tracking", "hazmat-screening", "slot-booking", "demurrage-billing",
        "gate-check", "reefer-telemetry", "weighbridge", "chassis-pool", "bill-of-lading", "port-call",
        "seal-verification", "empty-return", "tariff-lookup"]
KIND = ["API", "service", "worker", "pipeline", "backend", "scheduler"]
services, codes, aliases = [], set(), set()
while len(services) < CFG["services"]:
    name = rng.choice(PRE) + rng.choice(SUF)
    alias = f"{rng.choice(FUNC)} {rng.choice(KIND)}"
    code = (name[:2] + name[-1]).upper()
    if any(s["name"] == name for s in services) or alias in aliases or code in codes:
        continue
    aliases.add(alias)
    codes.add(code)
    services.append(dict(name=name, alias=alias, code=code, team=rng.randrange(len(teams)),
                         tier=rng.choice([1, 2, 2, 3])))
SVC = {s["name"]: s for s in services}

# ------------------------------------------------------------------ events
moves = []    # service ownership: svc, frm, to, eff, memo, cancelled, postponed_to, followup
xfers = []    # person transfers: person, frm, to, eff, memo
mgrs = []     # manager changes: team, new, eff, memo
swaps = {}    # (team, week) -> (person, mention)
decoy_swaps = []
incidents = []
reserved_people, reserved_svcs = set(), set()


def team_of(p, d, fl):
    t = p["team"]
    if not fl.get("ignore_memos"):
        for x in sorted(xfers, key=lambda x: x["eff"]):
            if x["person"] is p and x["eff"] <= d:
                t = x["to"]
    return t


def manager(t, d, fl):
    m = teams[t]["manager"]
    if not fl.get("ignore_memos"):
        for x in sorted(mgrs, key=lambda x: x["eff"]):
            if x["team"] == t and x["eff"] <= d:
                m = x["new"]
    return m


def owner(s, d, fl):
    t = s["team"]
    if fl.get("ignore_memos"):
        return t
    ev = []
    for m in moves:
        if m["svc"] is not s:
            continue
        if fl.get("first_announcement"):
            ev.append((m["eff"], m["to"]))
        elif not m["cancelled"]:
            ev.append((m["postponed_to"] or m["eff"], m["to"]))
    for eff, to in sorted(ev, key=lambda e: e[0]):
        if eff <= d:
            t = to
    return t


def lead(t, w, fl):
    if not fl.get("ignore_swaps") and (t, w) in swaps:
        return swaps[(t, w)][0]
    return teams[t]["rota"][w]


memo_no = [100]


def next_memo():
    memo_no[0] += rng.randint(1, 4)
    return f"memo-{memo_no[0]:04d}"


def pick_person(pred):
    cands = [p for p in people if p["id"] not in reserved_people and pred(p)]
    p = rng.choice(cands)
    reserved_people.add(p["id"])
    return p


def pick_service():
    s = rng.choice([s for s in services if s["name"] not in reserved_svcs])
    reserved_svcs.add(s["name"])
    return s


inc_ids = rng.sample(range(4100, 4999), CFG["incidents"])


def new_incident(s, d, commander, commander_mention):
    others = [x for x in services if x is not s]
    inc = dict(id=f"INC-{inc_ids[len(incidents)]}", svc=s, date=d, cmd=commander, cmd_mention=commander_mention,
               ruled_out=rng.choice(others), detected_by=rng.choice(others), sev=rng.choice(["SEV-2", "SEV-3", "SEV-2", "SEV-1"]))
    incidents.append(inc)
    return inc


questions = []

# ---- type A: incident -> alias -> service -> owner on date (memo) -> rota that week + prose swap -> person
used_a_teams = set()
for k, variant in enumerate(CFG["a_variants"]):
    w = rng.randint(8, 24)
    d = weekday_in(w)
    use_twin = k % 2 == 0

    def ok_x(p, t):
        return (p["id"] not in reserved_people and p["team"] == t and p is not teams[t]["rota"][w]
                and p is not teams[t]["manager"]
                and (("twin" in p and short_unique_in_team(p, t)) if use_twin else "nick" in p))

    # the team that truly owns the service that day must have a person the mention can resolve to
    t_true = rng.choice([t for t in range(len(teams)) if t not in used_a_teams
                         and any(ok_x(p, t) for p in people)])
    used_a_teams.add(t_true)
    if variant == "postponed":  # announced move away from t_true, pushed past the incident date
        s = rng.choice([x for x in services if x["name"] not in reserved_svcs and x["team"] == t_true])
        t0, t1 = t_true, rng.choice([i for i in range(len(teams)) if i != t_true])
    else:  # move into t_true before the incident date
        s = rng.choice([x for x in services if x["name"] not in reserved_svcs and x["team"] != t_true])
        t0, t1 = s["team"], t_true
    reserved_svcs.add(s["name"])
    e1 = d - timedelta(days=rng.randint(8, 40))
    mv = dict(svc=s, frm=t0, to=t1, eff=e1, memo=None, cancelled=False, postponed_to=None,
              announced=e1 - timedelta(days=rng.randint(7, 20)))
    if variant == "postponed":
        mv["postponed_to"] = d + timedelta(days=rng.randint(10, 45))
        mv["followup_date"] = e1 - timedelta(days=rng.randint(1, 5))
    moves.append(mv)
    assert owner(s, d, {}) == t_true
    base = teams[t_true]["rota"][w]
    x = pick_person(lambda p: ok_x(p, t_true))
    mention = short(x) if use_twin else x["nick"]
    swaps[(t_true, w)] = (x, mention)
    cmd = pick_person(lambda p: True)
    inc = new_incident(s, d, cmd, cmd["name"])
    questions.append(dict(type="A", inc=inc, week=w, mention=mention, person=x,
                          text=f"{inc['id']} was opened on {d.isoformat()}. Who was the primary on-call lead, "
                               f"that ISO week, of the team that owned the service the incident's root cause was "
                               f"traced to, as of that day?"))

# ---- type B: incident -> commander (nickname / near-duplicate full name) -> team on date (transfer memo) -> manager on date (memo)
for k, variant in enumerate(CFG["b_variants"]):
    s = pick_service()
    d = weekday_in(rng.randint(9, 24))
    if variant == "twin":
        c = pick_person(lambda p: "twin" in p and p["twin"]["id"] not in reserved_people)
        reserved_people.add(c["twin"]["id"])
        mention = c["name"]
    else:
        c = pick_person(lambda p: "nick" in p and "twin" not in p)
        mention = c["nick"]
    u0 = c["team"]
    u = u0
    if variant in ("nick_transfer", "twin"):
        u = rng.choice([i for i in range(len(teams)) if i != u0 and (c.get("twin") is None or i != c["twin"]["team"])])
        xfers.append(dict(person=c, frm=u0, to=u, eff=d - timedelta(days=rng.randint(10, 50)), memo=None))
    if variant in ("twin", "nick_mgr"):
        newm = pick_person(lambda p, u=u: p["team"] == u and p is not teams[u]["manager"])
        mgrs.append(dict(team=u, new=newm, eff=d - timedelta(days=rng.randint(5, 40)), memo=None))
    inc = new_incident(s, d, c, mention)
    questions.append(dict(type="B", inc=inc, person=c, mention=mention,
                          text=f"{inc['id']} was opened on {d.isoformat()}. Who was the line manager of that "
                               f"incident's incident commander on that day?"))

# ---- type C: incident -> alias -> service -> owner on a pinned later date (memo, then a superseding memo)
for k, variant in enumerate(CFG["c_variants"]):
    s = pick_service()
    d = weekday_in(rng.randint(6, 12))
    t0 = s["team"]
    t1 = rng.choice([i for i in range(len(teams)) if i != t0])
    e1 = d + timedelta(days=rng.randint(10, 30))
    mv = dict(svc=s, frm=t0, to=t1, eff=e1, memo=None, cancelled=False, postponed_to=None,
              announced=e1 - timedelta(days=rng.randint(7, 20)))
    moves.append(mv)
    if variant == "cancelled":
        mv["cancelled"] = True
        mv["followup_date"] = e1 + timedelta(days=rng.randint(2, 9))  # cancelled after it was due: retro-active
        pin = e1 + timedelta(days=rng.randint(20, 45))
    else:  # second move supersedes the first
        t2 = rng.choice([i for i in range(len(teams)) if i not in (t0, t1)])
        e2 = e1 + timedelta(days=rng.randint(15, 40))
        moves.append(dict(svc=s, frm=t1, to=t2, eff=e2, memo=None, cancelled=False, postponed_to=None,
                          announced=e2 - timedelta(days=rng.randint(7, 14))))
        pin = e2 + timedelta(days=rng.randint(5, 30))
    inc = new_incident(s, d, pick_person(lambda p: True), None)
    inc["cmd_mention"] = inc["cmd"]["name"]
    questions.append(dict(type="C", inc=inc, pin=pin,
                          text=f"Which team owned the service that {inc['id']}'s root cause was traced to, "
                               f"as of {pin.isoformat()}? (Not on the day of the incident.)"))

# ---- background events (other services / people), for realism and as distractors
for _ in range(CFG["extra_moves"]):
    s = pick_service()
    e = BASELINE + timedelta(days=rng.randint(20, 160))
    moves.append(dict(svc=s, frm=s["team"], to=rng.choice([i for i in range(len(teams)) if i != s["team"]]),
                      eff=e, memo=None, cancelled=False, postponed_to=None, announced=e - timedelta(days=10)))
for _ in range(CFG["extra_transfers"]):
    p = pick_person(lambda p: "twin" not in p)
    xfers.append(dict(person=p, frm=p["team"], to=rng.choice([i for i in range(len(teams)) if i != p["team"]]),
                      eff=BASELINE + timedelta(days=rng.randint(20, 160)), memo=None))
q_weeks = {(q["inc"]["svc"]["team"], q.get("week")) for q in questions}
for _ in range(CFG["extra_swaps"]):
    t, w = rng.randrange(len(teams)), rng.choice(WEEKS)
    if (t, w) in swaps or any(q["type"] == "A" and q["week"] == w for q in questions):
        continue
    p = rng.choice([m for m in members0(t) if m is not teams[t]["rota"][w]])
    swaps[(t, w)] = (p, p.get("nick") or p["first"])
for _ in range(CFG["decoy_swaps"]):
    t, w = rng.randrange(len(teams)), rng.choice(WEEKS)
    p = rng.choice(members0(t))
    decoy_swaps.append((t, w, p, p.get("nick") or short(p)))
while len(incidents) < CFG["incidents"]:
    s = rng.choice(services)
    c = rng.choice(people)
    new_incident(s, weekday_in(rng.choice(WEEKS)), c, c.get("nick") if rng.random() < 0.3 and "nick" in c else c["name"])


# "First L." must stay unambiguous inside the team on the day, after every transfer is applied
for q in questions:
    if q["type"] == "A" and q["mention"] == short(q["person"]):
        t = owner(q["inc"]["svc"], q["inc"]["date"], {})
        same = [p for p in people if team_of(p, q["inc"]["date"], {}) == t and short(p) == q["mention"]]
        if same != [q["person"]]:
            raise SystemExit(f"ambiguous mention {q['mention']} in team {teams[t]['name']}")


# ------------------------------------------------------------------ answers under truth and traps
def answer(q, fl):
    inc = q["inc"]
    if q["type"] == "A":
        d = inc["date"]
        t = owner(inc["svc"], d, fl)
        p = lead(t, q["week"], fl)
        if fl.get("near_duplicate") and "twin" in p and swaps.get((t, q["week"]), (None,))[0] is p:
            return p["twin"]["name"]
        if fl.get("nickname_literal") and swaps.get((t, q["week"]), (None, None))[0] is p and p.get("nick") == swaps[(t, q["week"])][1]:
            return p["nick"]
        return p["name"]
    if q["type"] == "B":
        d = inc["date"]
        c = q["person"]
        if fl.get("near_duplicate") and "twin" in c:
            c = c["twin"]
        if fl.get("nickname_literal") and q["mention"] == c.get("nick"):
            return c["nick"]  # never got as far as the profile: the manager is unknown
        return manager(team_of(c, d, fl), d, fl)["name"]
    d = inc["date"] if fl.get("wrong_date") else q["pin"]
    return teams[owner(inc["svc"], d, fl)]["name"]


TRAPS = ["ignore_memos", "first_announcement", "ignore_swaps", "near_duplicate", "nickname_literal", "wrong_date"]
gold = {f"q{i + 1}": answer(q, {}) for i, q in enumerate(questions)}
trap_answers = {t: {f"q{i + 1}": answer(q, {t: True}) for i, q in enumerate(questions)} for t in TRAPS}
flips = {qid: [t for t in TRAPS if trap_answers[t][qid] != gold[qid]] for qid in gold}
bad = [qid for qid, f in flips.items() if not f]
if bad:
    raise SystemExit(f"planted questions with no trap sensitivity: {bad}")
if len(set(gold.values())) < len(gold) - 2:
    raise SystemExit("too many repeated gold answers")

# ------------------------------------------------------------------ documents
out = TASK / "fixtures"
for sub in ["people", "teams", "runbooks", "incidents", "memos", "meetings", "status"]:
    if (out / sub).exists():
        shutil.rmtree(out / sub)
    (out / sub).mkdir(parents=True)
files = {}


def emit(rel, text):
    files[rel] = text


team_file = {t: f"teams/team-{11 + t}.md" for t in range(len(teams))}
person_file = {p["id"]: f"people/{p['id']}.md" for p in people}
svc_file = {s["name"]: f"runbooks/{s['name'].lower()}.md" for s in services}
LOC = ["Tilbury", "Rotterdam", "Gdansk", "Valencia", "Antwerp", "Remote (UK)", "Remote (PL)"]

for p in people:
    t = teams[p["team"]]["name"]
    about = [f"{p['first']} works on {F.r.choice(['platform tooling', 'carrier integrations', 'the yard systems', 'data quality', 'billing flows', 'observability', 'developer experience'])}."]
    if "nick" in p:
        about.append(f"Almost everyone calls {p['first']} \"{p['nick']}\", and chat handles and meeting notes use that name.")
    about.append(F.policy(2))
    emit(person_file[p["id"]], f"""# {p['name']}

| Field | Value |
|---|---|
| Employee ID | {p['id'].upper()} |
| Team (as of 2026-01-05) | {t} |
| Location | {F.r.choice(LOC)} |
| Start date | {F.r.randint(2015, 2025)}-{F.r.randint(1, 12):02d}-01 |

## About

{' '.join(about)}

## Current focus

{F.status_table()}

## Working agreements

{F.policy()}

## Notes from the last check-in

{F.agenda(F.r.randint(3, 5))}

{F.pad(CFG["padding"])}
""")

for t, tm in enumerate(teams):
    ms = members0(t)
    mt = "\n".join(f"| {m['name']} | {m['id'].upper()} |" for m in sorted(ms, key=lambda m: m['name']))
    rota = "\n".join(f"| 2026-W{w:02d} | {monday(w).isoformat()} | {tm['rota'][w]['name']} |" for w in WEEKS)
    emit(team_file[t], f"""# Team {tm['name']}

| Field | Value |
|---|---|
| Team | {tm['name']} |
| Manager (as of 2026-01-05) | {tm['manager']['name']} |
| Channel | #team-{tm['name'].lower()} |

Membership and reporting lines change through reorganisation memos; this page is refreshed once a
half and was last refreshed on 2026-01-05.

## Members (as of 2026-01-05)

| Name | Employee ID |
|---|---|
{mt}

## Mission

{F.policy()}

## Rituals

{F.agenda(4)}

## Primary on-call rotation, H1 2026

Swaps agreed in the weekly sync are recorded in the meeting notes, not here.

| ISO week | Week of | Primary on-call lead |
|---|---|---|
{rota}

## Current workstreams

{F.status_table()}

## Health metrics

{F.metric_table()}

{F.template_sections()}
""")

for s in services:
    alias_line = (f"Incident write-ups and most chat threads never use the name {s['name']}; they call it the "
                  f"{s['alias']}, so search for that phrase when you are matching incidents to this runbook.")
    emit(svc_file[s["name"]], f"""# {s['name']} runbook

| Field | Value |
|---|---|
| Service | {s['name']} |
| Short code | {s['code']} |
| Owning team (as of 2026-01-05) | {teams[s['team']]['name']} |
| Tier | {s['tier']} |
| Repository | git.bvf.internal/{s['name'].lower()} |

## What it is

{F.policy(2)} {alias_line} {F.policy(2)}

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > {F.r.randint(1, 5)}% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > {F.r.randint(300, 2000)} ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > {F.r.randint(1, 50)}k messages | Confirm consumers are healthy before replaying |

## Standard procedures

{F.agenda(5)}

## Dashboards

{F.metric_table()}

{F.template_sections()}
""")

for inc in incidents:
    d = inc["date"]
    tl = [f"| {h:02d}:{m:02d} | {F.r.choice(['Alert fired', 'Customer report received', 'Status page updated', 'Mitigation attempted', 'Metrics recovering', 'Monitoring'])} |"
          for h, m in sorted((F.r.randint(6, 20), F.r.randint(0, 59)) for _ in range(F.r.randint(5, 9)))]
    emit(f"incidents/{inc['id']}.md", f"""# {inc['id']} — {F.r.choice(['Degraded throughput', 'Elevated errors', 'Delayed updates', 'Partial outage', 'Stale data'])}

| Field | Value |
|---|---|
| Incident | {inc['id']} |
| Opened | {d.isoformat()} |
| Severity | {inc['sev']} |
| Detected by | {inc['detected_by']['code']} synthetic check |
| Status | Resolved |

## Summary

{F.policy(2)} {inc['cmd_mention']} took incident command shortly after the first page and ran the
bridge until the all-clear. Early suspicion fell on the {inc['ruled_out']['alias']}, which was ruled out
within the first hour.

## Timeline (UTC)

| Time | Event |
|---|---|
{chr(10).join(tl)}

## Analysis

{F.policy(3)} After the review the root cause was traced to a bad configuration push to the
{inc['svc']['alias']}; everything else in the blast radius was downstream of it. {F.policy(2)}

## Follow-up actions

{F.actions()}

{F.pad(CFG["padding"] + 1)}
""")

# memos: planted decisions buried mid-memo, plus decoys and pure policy memos
memos = []


def memo(d, title, decision):
    memos.append(dict(date=d, title=title, decision=decision))


for m in moves:
    s, a, b = m["svc"], teams[m["frm"]]["name"], teams[m["to"]]["name"]
    memo(m["announced"], f"Platform realignment: {s['name']}",
         f"After the review described above, {s['name']} moves from Team {a} to Team {b} with effect from "
         f"{prose_date(m['eff'])}. From that date Team {b} carries its operational duties, including the on-call "
         f"cover, and Team {a} keeps read access to the dashboards for a transition period.")
    if m.get("followup_date"):
        if m["cancelled"]:
            memo(m["followup_date"], f"Correction: {s['name']} realignment withdrawn",
                 f"The move of {s['name']} to Team {b} announced on {prose_date(m['announced'])} is withdrawn in full "
                 f"and is treated as never having taken effect. Team {a} retains {s['name']} and its duties "
                 f"without interruption.")
        else:
            memo(m["followup_date"], f"Update on the {s['name']} realignment",
                 f"The move of {s['name']} to Team {b} announced on {prose_date(m['announced'])} will not happen on "
                 f"{prose_date(m['eff'])} as planned; it now takes effect on {prose_date(m['postponed_to'])}. "
                 f"Until then Team {a} keeps {s['name']} and everything that comes with it.")
for x in xfers:
    p = x["person"]
    memo(x["eff"] - timedelta(days=F.r.randint(5, 15)), "People update",
         f"{p['name']} moves from Team {teams[x['frm']]['name']} to Team {teams[x['to']]['name']}, starting "
         f"{prose_date(x['eff'])}, and from then reports to that team's manager.")
for x in mgrs:
    memo(x["eff"] - timedelta(days=F.r.randint(5, 15)), f"Leadership change in Team {teams[x['team']]['name']}",
         f"{x['new']['name']} takes over as manager of Team {teams[x['team']]['name']} from "
         f"{prose_date(x['eff'])}; everyone in the team reports to {x['new']['first']} from that date.")
for _ in range(CFG["decoy_memos"]):
    s = F.r.choice(services)
    b = teams[F.r.choice([i for i in range(len(teams)) if i != s["team"]])]["name"]
    memo(BASELINE + timedelta(days=F.r.randint(0, 170)), f"Discussion paper: future home of {s['name']}",
         f"One option on the table is for Team {b} to take over {s['name']}. This paper only frames the discussion: "
         f"no decision has been made, nothing changes, and the current arrangement stays as it is.")
while len(memos) < CFG["memos"]:
    memo(BASELINE + timedelta(days=F.r.randint(0, 170)),
         F.r.choice(["Travel policy refresh", "Expense guidance", "Security awareness month", "Office hours",
                     "Quarterly planning timeline", "Records retention reminder", "Tooling survey results"]), None)
memos.sort(key=lambda m: m["date"])
for i, m in enumerate(memos):
    # the decision is one paragraph in the middle of the considerations, with no heading of its own
    items = F.agenda(F.r.randint(5, 8)).split("\n")
    if m["decision"]:
        items.insert(F.r.randrange(2, len(items) - 1), "\n" + m["decision"] + "\n")
    emit(f"memos/memo-{i + 1:04d}.md", f"""# {m['title']}

Date: {m['date'].isoformat()}. Circulation: all staff.

## Background

{F.policy()} {F.policy()}

## Considerations

{chr(10).join(items)}

## Next steps

{F.actions()}

{F.template_sections()}
""")

# meeting notes: planted swaps (and decoys) sit among ordinary agenda items
notes = []
for (t, w), (p, mention) in swaps.items():
    base = teams[t]["rota"][w]
    notes.append((t, monday(w) - timedelta(days=F.r.randint(3, 10)),
                  f"- {mention} offered to take the primary on-call for {w and f'2026-W{w:02d}'} (week of "
                  f"{prose_date(monday(w))}) in place of {base['first']}, who is on leave. Agreed; the rotation "
                  f"page will not be edited, this note is the record."))
for t, w, p, mention in decoy_swaps:
    notes.append((t, monday(w) - timedelta(days=F.r.randint(3, 10)),
                  f"- Asked whether {mention} could cover the primary on-call for 2026-W{w:02d}; not possible this "
                  f"time, so the rotation stands as published."))
while len(notes) < CFG["meetings"]:
    notes.append((F.r.randrange(len(teams)), BASELINE + timedelta(days=F.r.randint(0, 170)), None))
notes.sort(key=lambda n: (n[1], n[0]))
for i, (t, d, item) in enumerate(notes):
    items = F.agenda().split("\n")
    if item:
        items.insert(F.r.randrange(1, len(items)), item)
    emit(f"meetings/mtg-{i + 1:04d}.md", f"""# Team {teams[t]['name']} weekly sync — {d.isoformat()}

Attendees: most of the team. Notes: {F.name()}.

## Agenda

{chr(10).join(items)}

## Status

{F.status_table()}

## Announcements

{F.chatter()}

## Actions

{F.actions()}

{F.pad(CFG["padding"])}
""")

for i in range(CFG["status_reports"]):
    d = BASELINE + timedelta(days=7 * (i % 25))
    emit(f"status/status-{i + 1:04d}.md", f"""# Weekly status — {F.r.choice([tm['name'] for tm in teams])} — {d.isoformat()}

## Highlights

{F.agenda(3)}

## Metrics

{F.metric_table()}

## Workstreams

{F.status_table()}

{F.template_sections()}
""")

total = 0
for rel, text in files.items():
    p = out / rel
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    total += len(text.encode("utf-8"))


# ------------------------------------------------------------------ key, report, task.md, meta
def evidence(q):
    inc = q["inc"]
    ev = [f"incidents/{inc['id']}.md", svc_file[inc["svc"]["name"]]]
    if q["type"] == "B":
        ev.append(person_file[q["person"]["id"]])
    return ev


key = {"task": "fw1", "source": "synthetic", "questions": [
    {"id": f"q{i + 1}", "kind": "norm", "question": q["text"],
     "answers": [gold[f"q{i + 1}"]] + ([f"Team {gold[f'q{i + 1}']}"] if q["type"] == "C" else []),
     "evidence": evidence(q), "type": q["type"], "trap_flips": flips[f"q{i + 1}"]}
    for i, q in enumerate(questions)]}
with open(HERE / "key.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(key, f, indent=2)
    f.write("\n")
n = len(questions)
predicted = {t: round(sum(1 for q in gold if t not in flips[q]) / n, 4) for t in TRAPS}
report = {"task": "fw1", "fixture_files": len(files), "fixture_bytes": total, "approx_tokens": total // 4,
          "gold": gold, "trap_answers": trap_answers, "trap_flips": flips, "predicted_trap_scores": predicted,
          "counts": {k: sum(1 for r in files if r.startswith(k + "/")) for k in
                     ["people", "teams", "runbooks", "incidents", "memos", "meetings", "status"]}}
with open(HERE / "build-report.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(report, f, indent=2)
    f.write("\n")

qs = "\n".join(f"- **q{i + 1}.** {q['text']}" for i, q in enumerate(questions))
msg = f"""Project code name: {{SEED}}.

This workspace is a copy of the internal wiki of Brannock Vale Freight, a (fictional) freight
company: {len(files)} Markdown pages under `people/`, `teams/`, `runbooks/`, `incidents/`, `memos/`,
`meetings/` and `status/`. Nothing in it is about a real company or person, so the answers are only in
these pages; general knowledge will not help. Most of each page is routine boilerplate.

The wiki is not always up to date: pages carry "as of" dates, later memos and meeting notes change
things, and later memos sometimes change earlier memos. When a question pins a date, answer for that
date. People are sometimes mentioned by nickname or by first name and initial.

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the pages you need.

Write `answers.json` in the workspace root:

```json
{{
  "q1": {{ "answer": "<full name or team name>", "evidence": ["incidents/INC-....md", "..."] }},
  "q2": {{ "answer": "...", "evidence": ["..."] }}
}}
```

- For a person, `answer` is the full name exactly as the heading of their `people/` page writes it
  (not a nickname). For a team, it is the team's name (for example `Larkspur`).
- `evidence` lists the pages the answer rests on; it is optional and not scored.
- Include every question from q1 to q{n}. If you cannot pin one down, give your best guess anyway.

Questions:

{qs}

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
"""
header = f"""# fw1 — multi-hop questions over a fictional company wiki (synthetic)

Family `fictional-multihop`, split `dev`. Workspace = `fixtures/` ({len(files)} Markdown pages, {total / 1e6:.2f} MB,
~{total // 4 // 1000}k tokens; mostly boilerplate). {n} questions, each 3-5 hops: incident -> service (prose alias)
-> owning team on a date (runbook table, then reorg memos, some postponed or withdrawn) -> on-call lead
that week (rotation table, then swaps agreed in meeting-note prose) -> person (nickname or "First L."
resolved within the team; near-duplicate twins elsewhere), or incident commander -> team on a date
(transfer memos) -> manager on a date (manager-change memos). Checker: normalised exact name match;
score = correct / {n}. Trap flips and predicted trap-only scores: `hidden/build-report.json`.
Generator + knobs: `hidden/gen_world.py`, `hidden/build.json`."""
with open(TASK / "task.md", "w", encoding="utf-8", newline="\n") as f:
    f.write(header + "\n\n---\n\n" + msg)
meta_path = TASK / "meta.json"
meta = json.load(open(meta_path, encoding="utf-8"))
meta["requiredFixtures"] = [{"glob": f"{k}/*.md", "count": v} for k, v in report["counts"].items()]
with open(meta_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(meta, f, indent=2)
    f.write("\n")
print(json.dumps({"files": len(files), "bytes": total, "gold": gold, "flips": flips, "predicted": predicted}, indent=1))
