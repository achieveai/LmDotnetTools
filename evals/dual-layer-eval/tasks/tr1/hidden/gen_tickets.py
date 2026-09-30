"""tr1 generator: Q1 2026 support tickets of a fictional hosting company, judged against its
service-credit policy.

Writes fixtures/ (policy/, status/, tickets/), hidden/key.json, hidden/build-report.json, task.md and
meta.json's requiredFixtures. Deterministic from hidden/build.json: the world (who had which incident,
when, why) comes from one RNG and is fixed before any prose is drawn from a second RNG, so prose
changes never move the key.

Each ticket's status is decided by six policy clauses, and one of them is amended by an appendix. The
resolver `judge` runs once under the true reading and once per trap (a plausible misreading of one
clause). A question that fewer than `min_flips_per_question` traps change, or a trap that changes no
question, rejects the world and the next attempt is drawn.

Usage: python gen_tickets.py   (paths are relative to this file)
"""
import json
import random
import shutil
from datetime import datetime, timedelta
from pathlib import Path

import render

HERE = Path(__file__).resolve().parent
TASK = HERE.parent
FIX = TASK / "fixtures"
CFG = json.load(open(HERE / "build.json", encoding="utf-8"))

TRAPS = ["no_appendix", "appendix_overreach", "miss_unless", "verbal_advice", "customer_estimate",
         "ignore_maintenance", "late_notice_window", "no_effective_date", "count_duplicates",
         "include_nonprod", "ignore_deadline"]
Q_START, Q_END = datetime(2026, 1, 3), datetime(2026, 3, 27)
ENT_DATE = datetime(2026, 2, 1)
DEADLINE = timedelta(days=7)
NOTICE = timedelta(hours=72)


# ------------------------------------------------------------------------------------------ world
def build_world(rng):
    customers = []
    names = render.customer_names(rng, sum(CFG["customers"].values()))
    i = 0
    for plan, n in CFG["customers"].items():
        for _ in range(n):
            customers.append({"name": names[i], "plan": plan, "contacts": render.contact_names(rng, rng.randint(2, 3))})
            i += 1
    rng.shuffle(customers)

    windows = []
    days = sorted(rng.sample(range(4, 84), CFG["maintenance_windows"]))
    late = set(rng.sample(range(len(days)), CFG["late_notice_windows"]))
    for k, d in enumerate(days):
        ws = Q_START + timedelta(days=d, hours=rng.choice([1, 2, 22, 23]))
        we = ws + timedelta(minutes=rng.choice([60, 90, 120, 150]))
        ann = ws - (timedelta(hours=rng.randint(4, 40)) if k in late else timedelta(days=rng.randint(4, 12)))
        windows.append({"id": f"MW-{k + 1:02d}", "start": ws, "end": we, "announced": ann, "late": k in late,
                        "scope": render.MAINT_SCOPES[k % len(render.MAINT_SCOPES)]})

    def clear_of_windows(s, e):
        return all(e <= w["start"] or s >= w["end"] for w in windows)

    def day_time(lo=Q_START, hi=Q_END):
        while True:
            s = lo + timedelta(minutes=rng.randint(0, int((hi - lo).total_seconds() // 60)))
            s = s.replace(second=0, microsecond=0)
            if 6 <= s.hour <= 21:
                return s

    def pick_customer(plans):
        return rng.choice([c for c in customers if c["plan"] in plans])

    incidents = []

    def add(kind, cause, plans=("Enterprise", "Business", "Standard"), env="prod", mon=None, claimed=None,
            start=None, delay=None, window=None, sub=None):
        c = pick_customer(plans)
        if start is None:
            while True:
                start = day_time()
                if clear_of_windows(start, start + timedelta(minutes=(mon or 60) + 30)):
                    break
        if mon is not None and claimed is None:
            claimed = max(mon, int(mon * rng.uniform(1.0, 1.6)))
        if delay is None:
            delay = timedelta(minutes=rng.randint(4, 60 * 40))
        incidents.append({"kind": kind, "cause": cause, "customer": c["name"], "plan": c["plan"], "env": env,
                          "start": start, "mon": mon, "claimed": claimed, "delay": delay, "window": window,
                          "sub": sub if sub is not None else rng.randint(0, 99)})

    S = CFG["slots"]
    for _ in range(S["platform_ok"]):
        add("platform_ok", "platform", mon=rng.randint(31, 190))
    for _ in range(S["ent_pre_feb_short"]):
        add("ent_pre_feb_short", "platform", plans=("Enterprise",), mon=rng.randint(15, 28),
            start=day_time(Q_START, ENT_DATE - timedelta(hours=2)))
    for _ in range(S["ent_post_feb_short"]):
        add("ent_post_feb_short", "platform", plans=("Enterprise",), mon=rng.randint(15, 29),
            start=day_time(ENT_DATE, Q_END))
    for _ in range(S["estimate_inflated"]):
        m = rng.randint(12, 27)
        add("estimate_inflated", "platform", plans=("Business", "Standard"), mon=m, claimed=rng.randint(40, 150))
    for _ in range(S["estimate_modest"]):
        m = rng.randint(32, 44)
        add("estimate_modest", "platform", plans=("Business", "Standard"), mon=m, claimed=rng.randint(15, 25))

    def in_window(kind, w, eff_lo, eff_hi, plans=("Business", "Standard")):
        into = rng.randint(10, max(11, int((w["end"] - w["start"]).total_seconds() // 60) - 10))
        s = w["start"] + timedelta(minutes=into)
        overlap = int((w["end"] - s).total_seconds() // 60)
        eff = rng.randint(eff_lo, eff_hi)
        add(kind, "platform", plans=plans, mon=overlap + eff, start=s, window=w["id"])

    ann = [w for w in windows if not w["late"]]
    lat = [w for w in windows if w["late"]]
    for k in range(S["maint_announced"]):
        in_window("maint_announced", ann[k % len(ann)], 5, 25)
    for k in range(S["maint_announced_still_ok"]):
        in_window("maint_announced_still_ok", ann[(k + S["maint_announced"]) % len(ann)], 34, 80)
    for k in range(S["maint_late_notice"]):
        in_window("maint_late_notice", lat[k % len(lat)], 30, 45)
    for _ in range(S["late_report"]):
        add("late_report", "platform", mon=rng.randint(35, 150), start=day_time(Q_START, Q_END - timedelta(days=24)),
            delay=timedelta(days=rng.randint(8, 20), hours=rng.randint(0, 20)))
    for _ in range(S["nonprod"]):
        add("nonprod", "platform", env="nonprod", mon=rng.randint(35, 240))
    for _ in range(S["partner"]):
        add("partner", "partner", mon=rng.randint(33, 150))
    for _ in range(S["own_provider"]):
        add("own_provider", "own_provider", mon=rng.randint(35, 200))
    for _ in range(S["config_written"]):
        add("config_written", "config_written", mon=rng.randint(35, 160))
    for _ in range(S["config_verbal"]):
        add("config_verbal", "config_verbal", mon=rng.randint(35, 160))
    for _ in range(S["config_plain"]):
        add("config_plain", "config_plain", mon=rng.randint(35, 200))
    for _ in range(S["isp"]):
        add("isp", "isp", mon=rng.randint(35, 180))
    for _ in range(S["no_outage"]):
        add("no_outage", "none", mon=None, claimed=None)

    incidents.sort(key=lambda x: (x["start"], x["customer"]))
    for n, inc in enumerate(incidents):
        inc["iid"] = n

    # tickets: one per incident, plus duplicates from a second contact, plus the written-advice source tickets
    tickets = []
    for inc in incidents:
        tickets.append({"inc": inc, "opened": inc["start"] + inc["delay"], "dup": False})
    dup_pool = [i for i in incidents if i["kind"] in ("platform_ok", "partner", "ent_post_feb_short", "config_written")]
    for inc in rng.sample(dup_pool, CFG["duplicates"]):
        first = inc["start"] + inc["delay"]
        tickets.append({"inc": inc, "opened": first + timedelta(minutes=rng.randint(20, 60 * 30)), "dup": True})
    for inc in incidents:
        if inc["cause"] == "config_written":
            src = {"kind": "advice_source", "cause": "none", "customer": inc["customer"], "plan": inc["plan"],
                   "env": "prod", "start": inc["start"] - timedelta(days=rng.randint(3, 20), hours=rng.randint(1, 9)),
                   "mon": None, "claimed": None, "delay": timedelta(0), "window": None, "sub": inc["sub"], "iid": None,
                   "advice_for": inc["iid"]}
            tickets.append({"inc": src, "opened": src["start"], "dup": False})
    tickets.sort(key=lambda t: (t["opened"], t["inc"]["customer"]))
    for n, t in enumerate(tickets):
        t["id"] = f"T-{4101 + n * 3 + rng.randint(0, 2)}"
    # link each written-advice incident to its source ticket
    src_of = {t["inc"]["advice_for"]: t["id"] for t in tickets if t["inc"]["kind"] == "advice_source"}
    for inc in incidents:
        if inc["cause"] == "config_written":
            inc["advice_ticket"] = src_of[inc["iid"]]
    return customers, windows, incidents, tickets


# ------------------------------------------------------------------------------------------ policy
def overlap_minutes(s, e, w):
    return max(0, int((min(e, w["end"]) - max(s, w["start"])).total_seconds() // 60))


def judge(inc, first_opened, windows, T):
    """Qualifying minutes of an incident under trap set T, or None when it does not qualify."""
    c = inc["cause"]
    if c == "none":
        return None
    if inc["env"] != "prod" and "include_nonprod" not in T:
        return None
    ok = {"platform": True, "partner": "no_appendix" not in T, "own_provider": "appendix_overreach" in T,
          "config_written": "miss_unless" not in T, "config_verbal": "verbal_advice" in T,
          "config_plain": False, "isp": False}[c]
    if not ok:
        return None
    if first_opened - inc["start"] > DEADLINE and "ignore_deadline" not in T:
        return None
    base = inc["claimed"] if "customer_estimate" in T else inc["mon"]
    s, e = inc["start"], inc["start"] + timedelta(minutes=inc["mon"])
    ded = 0
    for w in windows:
        timely = w["start"] - w["announced"] >= NOTICE
        if (timely and "ignore_maintenance" not in T) or (not timely and "late_notice_window" in T):
            ded += overlap_minutes(s, e, w)
    eff = max(0, base - ded)
    ent = inc["plan"] == "Enterprise" and (inc["start"] >= ENT_DATE or "no_effective_date" in T)
    return eff if eff >= (15 if ent else 30) else None


def qualifying(tickets, windows, T):
    """[(ticket, qualifying minutes)] - one entry per incident (its earliest ticket) unless count_duplicates."""
    first = {}
    for t in tickets:
        iid = t["inc"]["iid"]
        if iid is not None and (iid not in first or t["opened"] < first[iid]["opened"]):
            first[iid] = t
    out = []
    for t in tickets:
        iid = t["inc"]["iid"]
        if iid is None or (first[iid] is not t and "count_duplicates" not in T):
            continue
        eff = judge(t["inc"], first[iid]["opened"], windows, T)
        if eff is not None:
            out.append((t, eff))
    return out


MONTHS = ["January", "February", "March"]


def answers_for(R, params):
    by_cust = {}
    for t, _ in R:
        by_cust[t["inc"]["customer"]] = by_cust.get(t["inc"]["customer"], 0) + 1
    top = max(by_cust.values()) if by_cust else 0
    leaders = sorted(c for c, n in by_cust.items() if n == top)
    return {
        "q1": len(R),
        "q2": sorted(t["id"] for t, eff in R if t["inc"]["plan"] == "Enterprise" and eff < 30),
        "q3": leaders[0] if leaders else "",
        "q3_unique": len(leaders) == 1,
        "q4": sum(1 for t, _ in R if t["inc"]["cause"] in ("partner", "own_provider")),
        "q5": sum(eff for t, eff in R if t["inc"]["customer"] == params["q5_customer"]),
        "q6": sum(1 for t, _ in R if t["inc"]["start"].month == params["q6_month"]),
        "q7": sorted(t["id"] for t, _ in R if t["inc"]["cause"].startswith("config")),
        "q8": len(by_cust),
    }


QIDS = ["q1", "q2", "q3", "q4", "q5", "q6", "q7", "q8"]


def try_attempt(attempt):
    rng = random.Random(CFG["seed"] * 1000 + attempt)
    customers, windows, incidents, tickets = build_world(rng)
    R = {tuple(): qualifying(tickets, windows, frozenset())}
    for t in TRAPS:
        R[(t,)] = qualifying(tickets, windows, frozenset([t]))
    best = None
    for cust in sorted(c["name"] for c in customers):
        for month in (1, 2, 3):
            p = {"q5_customer": cust, "q6_month": month}
            gold = answers_for(R[tuple()], p)
            if not gold["q3_unique"] or len(gold["q2"]) < 3 or len(gold["q7"]) < 2:
                return None
            # q5 names its customer, so it must not be q3's answer (the memory assertion would catch the leak)
            if cust == gold["q3"] or sum(1 for t, _ in R[tuple()] if t["inc"]["customer"] == cust) < 3:
                continue
            ta = {t: answers_for(R[(t,)], p) for t in TRAPS}
            flips = {q: [t for t in TRAPS if ta[t][q] != gold[q]] for q in QIDS}
            score = (min(len(f) for f in flips.values()), len(flips["q5"]) + len(flips["q6"]))
            if best is None or score > best[0]:
                best = (score, p, gold, ta, flips)
    if best is None:
        return None
    (mn, _), p, gold, ta, flips = best
    if mn < CFG["min_flips_per_question"]:
        return None
    if any(all(t not in flips[q] for q in QIDS) for t in TRAPS):
        return None
    return customers, windows, incidents, tickets, R, p, gold, ta, flips


def main():
    res = None
    for attempt in range(CFG["max_attempts"]):
        res = try_attempt(attempt)
        if res:
            break
    if not res:
        raise SystemExit("no world met the constraints; loosen build.json")
    customers, windows, incidents, tickets, R, p, gold, ta, flips = res

    prng = random.Random(CFG["seed"] + 7)
    files = {"policy/service-credit-policy.md": render.policy(),
             "status/maintenance-calendar-q1-2026.md": render.calendar(windows, prng)}
    agents = render.AGENTS
    for t in tickets:
        t["agent"] = agents[prng.randrange(len(agents))]
    for t in tickets:
        files[f"tickets/{t['id']}.md"] = render.ticket(t, tickets, windows, customers, agents, prng)
    render.lint(files)

    if FIX.exists():
        shutil.rmtree(FIX)
    for rel, text in files.items():
        f = FIX / rel
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(text, encoding="utf-8", newline="\n")

    qtext = render.questions(p)
    policy_ev = "service-credit-policy.md"
    key = {"task": "tr1", "questions": []}
    for q in QIDS:
        g = gold[q]
        if q in ("q2", "q7"):
            kind, answers = "idset", [", ".join(g)]
            ev = [policy_ev] + [f"{i}.md" for i in g]
        elif q == "q3":
            kind, answers = "norm", [g]
            ev = [policy_ev] + sorted(f"{t['id']}.md" for t, _ in R[tuple()] if t["inc"]["customer"] == g)
        else:
            kind, answers = "int", [str(g)]
            ev = [policy_ev]
        if q in ("q5", "q6"):
            ev.append("maintenance-calendar-q1-2026.md")
        key["questions"].append({"id": q, "kind": kind, "question": qtext[q], "answers": answers,
                                 "evidence": ev, "trap_flips": flips[q]})

    def as_answer(q, v):
        return v if isinstance(v, list) else str(v) if not isinstance(v, str) else v

    trap_answers = {t: {q: as_answer(q, ta[t][q]) for q in QIDS} for t in TRAPS}
    n = len(QIDS)
    report = {
        "task": "tr1",
        "fixture_files": len(files),
        "fixture_bytes": sum(len(v.encode("utf-8")) for v in files.values()),
        "approx_tokens": sum(len(v.encode("utf-8")) for v in files.values()) // 4,
        "ticket_tokens_min_max": [min(len(v.encode("utf-8")) // 4 for k, v in files.items() if k.startswith("tickets/")),
                                  max(len(v.encode("utf-8")) // 4 for k, v in files.items() if k.startswith("tickets/"))],
        "params": p,
        "gold": {q: gold[q] for q in QIDS},
        "trap_answers": trap_answers,
        "trap_flips": {t: [q for q in QIDS if t in flips[q]] for t in TRAPS},
        "predicted_trap_scores": {t: round(1 - sum(1 for q in QIDS if t in flips[q]) / n, 4) for t in TRAPS},
        "qualifying_incidents": [{"ticket": t["id"], "customer": t["inc"]["customer"], "kind": t["inc"]["kind"],
                                  "minutes": eff} for t, eff in R[tuple()]],
        "tickets_by_kind": {},
    }
    for t in tickets:
        k = t["inc"]["kind"] + ("+dup" if t["dup"] else "")
        report["tickets_by_kind"][k] = report["tickets_by_kind"].get(k, 0) + 1
    report["ticket_truth"] = {t["id"]: {"kind": t["inc"]["kind"], "dup": t["dup"], "customer": t["inc"]["customer"],
                                        "plan": t["inc"]["plan"], "env": t["inc"]["env"],
                                        "start": t["inc"]["start"].isoformat() if t["inc"]["iid"] is not None else None,
                                        "mon": t["inc"]["mon"], "claimed": t["inc"]["claimed"],
                                        "window": t["inc"]["window"]} for t in tickets}
    json.dump(key, open(HERE / "key.json", "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
    json.dump(report, open(HERE / "build-report.json", "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)

    n_tickets = sum(1 for k in files if k.startswith("tickets/"))
    (TASK / "task.md").write_text(render.task_md(n_tickets, report, qtext), encoding="utf-8", newline="\n")
    meta_path = TASK / "meta.json"
    meta = json.load(open(meta_path, encoding="utf-8")) if meta_path.exists() else {
        "family": "judged-aggregation", "split": "dev", "seeds": ["aurora", "basalt", "cascade", "delta", "ember"],
        "timeoutMinutes": 45}
    meta["requiredFixtures"] = [{"glob": f"{d}/*.md", "count": sum(1 for k in files if k.startswith(d + "/"))}
                                for d in ("policy", "status", "tickets")]
    meta_path.write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({k: report[k] for k in ("fixture_files", "fixture_bytes", "approx_tokens", "ticket_tokens_min_max",
                                             "params", "gold", "predicted_trap_scores", "tickets_by_kind")}, indent=1,
                     default=str))
    print("attempt", attempt)


if __name__ == "__main__":
    main()
