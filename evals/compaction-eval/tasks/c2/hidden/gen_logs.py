"""Deterministic fixture generator for c2 (run once; the generated files are committed).

Writes fixtures/logs/<date>.log for 12 days, hidden/expected.json (CLI answer key) and
hidden/incidents.json (per-day incident key). Each day's log is a time-ordered access log with
operator comment lines ("# ...") interleaved. Exactly one comment per day OPENS an incident ticket
(INC-nnnn) naming the affected path; the other ticket mentions that day are decoys (a ticket
closed on an earlier day, a retro reminder, a deploy note). Every INC id is opened in exactly one
day's file (it may be mentioned as a decoy on a later day), and "opened" appears once per file.

Usage: python gen_logs.py  (from tasks/c2/hidden; writes ../fixtures/logs and ./*.json)
"""
import json
import os
import random
from collections import Counter, defaultdict
from datetime import datetime, timedelta

SEED = 20260302
HERE = os.path.dirname(os.path.abspath(__file__))
LOGS = os.path.join(HERE, "..", "fixtures", "logs")
DAYS = [(datetime(2026, 3, 2) + timedelta(days=i)).date() for i in range(12)]
LINES_PER_DAY = [128, 121, 134, 119, 127, 131, 116, 124, 129, 122, 133, 126]  # 1510 request lines

# path -> (weight, latency low, latency high)
PATHS = {
    "/api/items": (18, 20, 60),
    "/health": (15, 1, 6),
    "/api/orders": (10, 80, 140),
    "/api/search": (8, 150, 420),
    "/static/app.js": (7, 5, 20),
    "/api/items/{id}": (12, 15, 45),
    "/api/orders/{id}": (5, 40, 90),
    "/api/orders/{id}/status": (3, 150, 240),
    "/api/reports/daily": (2, 1500, 2600),
    "/login": (4, 60, 120),
    "/api/cart": (5, 30, 70),
    "/static/style.css": (4, 4, 15),
    "/api/export": (1, 900, 1400),
}
STATUS_WEIGHTS = [(200, 78), (201, 5), (204, 2), (301, 1), (400, 4), (401, 2), (404, 3), (500, 3), (503, 2)]

# Per day: (incident ticket, affected path, open HH:MM, resolve HH:MM, cause). Ticket ids are unique.
INCIDENTS = [
    ("INC-4102", "/api/search", "09:41", "11:20", "search index node idx-2 rebuilding after a bad restart"),
    ("INC-4107", "/api/orders", "13:05", "14:50", "order database failing over to the replica"),
    ("INC-4111", "/api/reports/daily", "07:30", "10:15", "report worker pool stuck at two workers"),
    ("INC-4115", "/login", "16:12", "17:40", "session store evicting under memory pressure"),
    ("INC-4119", "/api/items", "10:22", "12:05", "item cache warming after the eviction sweep"),
    ("INC-4124", "/api/cart", "08:55", "09:48", "cart service pinned to one availability zone"),
    ("INC-4128", "/api/export", "14:37", "18:02", "export queue backed up behind a 9 GB job"),
    ("INC-4133", "/api/orders/{id}/status", "11:16", "13:30", "status lookups hitting the cold replica"),
    ("INC-4137", "/static/app.js", "06:48", "08:10", "CDN origin pull misconfigured after the deploy"),
    ("INC-4141", "/api/items/{id}", "15:03", "16:25", "item detail path missing its index hint"),
    ("INC-4146", "/api/search", "12:44", "14:15", "query planner regression in build 2211"),
    ("INC-4150", "/health", "09:12", "09:58", "health endpoint waiting on a slow dependency probe"),
]

HANDOVER = [
    "# handover from night shift: no open pages, dashboards green, next deploy window at 14:00",
    "# handover: two alerts auto-resolved overnight, nothing to carry forward",
    "# morning check: queue depth normal, replica lag under 2s, no paging",
    "# shift change: on-call is now the platform pair, escalation path unchanged",
    "# handover: canary at 5 percent since last night, holding",
]
DEPLOY = [
    "# deploy build {build} to canary (5 percent), no incident, rollback plan on the wiki",
    "# canary build {build} promoted to 25 percent after a clean hour",
    "# deploy window closed, build {build} is fully rolled out",
    "# config push: request timeout raised to 4500ms on the gateway, no incident",
]
RETRO = [
    "# reminder: retro for {old} ({oldpath}) is scheduled for Thursday, notes go in the retro doc",
    "# note: {old} ({oldpath}) closed yesterday with no further action, keep an eye on the graphs",
    "# follow-up on {old}: the {oldpath} alert threshold was tuned, ticket stays closed",
    "# {old} postmortem published; the {oldpath} fix shipped in a previous build, nothing open",
]
OPEN = "# {t} opened {hhmm}: {cause}; {path} latency elevated until further notice"
RESOLVE = "# {t} resolved {hhmm}: {path} back to normal, ticket stays open only for the retro"


def choose(rng, weighted):
    total = sum(w for _, w in weighted)
    x = rng.uniform(0, total)
    for item, w in weighted:
        x -= w
        if x <= 0:
            return item
    return weighted[-1][0]


def concrete(rng, path):
    return path.replace("{id}", str(rng.randint(1, 500)))


def main():
    rng = random.Random(SEED)
    os.makedirs(LOGS, exist_ok=True)
    status_counts = Counter()
    path_counts = Counter()
    errors_by_hour = Counter()
    latency_sum = defaultdict(int)
    latency_n = Counter()
    total_lines = 0
    incidents = {}
    old_tickets = [("INC-4088", "/api/cart"), ("INC-4091", "/static/style.css"), ("INC-4095", "/api/export")]
    build = 2190
    for di, day in enumerate(DAYS):
        ticket, ipath, t_open, t_res, cause = INCIDENTS[di]
        incidents[day.isoformat()] = {"ticket": ticket, "path": ipath}
        n = LINES_PER_DAY[di]
        # request timestamps: spread across the day, denser in working hours
        secs = sorted(
            int(rng.triangular(0, 86399, 46800)) if rng.random() < 0.8 else rng.randint(0, 86399) for _ in range(n)
        )
        open_s = int(t_open[:2]) * 3600 + int(t_open[3:]) * 60
        res_s = int(t_res[:2]) * 3600 + int(t_res[3:]) * 60
        events = []  # (seconds, order, text)
        for s in secs:
            tmpl = choose(rng, [(p, w) for p, (w, _, _) in PATHS.items()])
            w, lo, hi = PATHS[tmpl]
            path = concrete(rng, tmpl)
            method = "GET"
            if tmpl in ("/api/orders", "/api/cart", "/login"):
                method = rng.choice(["POST", "POST", "GET"])
            elif tmpl == "/api/export":
                method = "POST"
            status = choose(rng, STATUS_WEIGHTS)
            lat = rng.randint(lo, hi)
            if tmpl == ipath and open_s <= s <= res_s:
                lat = lat * rng.randint(4, 7)  # the incident really does show in the numbers
                if rng.random() < 0.3:
                    status = rng.choice([500, 503])
            ip = f"10.{rng.randint(0, 3)}.{rng.randint(1, 254)}.{rng.randint(1, 254)}"
            ts = datetime(day.year, day.month, day.day) + timedelta(seconds=s)
            line = f"{ts.strftime('%Y-%m-%dT%H:%M:%S')}Z {ip} {method} {path} {status} {lat}ms"
            events.append((s, 1, line))
            status_counts[str(status)] += 1
            path_counts[path] += 1
            if status >= 500:
                errors_by_hour[ts.strftime("%Y-%m-%dT%H")] += 1
            latency_sum[path] += lat
            latency_n[path] += 1
            total_lines += 1
        # operator comments: handover early, one decoy retro/closed note, a deploy note, the open and resolve
        old, oldpath = old_tickets[di % len(old_tickets)]
        build += rng.randint(3, 9)
        events.append((rng.randint(5 * 3600, 8 * 3600), 0, rng.choice(HANDOVER)))
        events.append((rng.randint(8 * 3600, 20 * 3600), 0, rng.choice(RETRO).format(old=old, oldpath=oldpath)))
        events.append((rng.randint(9 * 3600, 19 * 3600), 0, rng.choice(DEPLOY).format(build=build)))
        if rng.random() < 0.6:
            events.append((rng.randint(19 * 3600, 23 * 3600), 0, "# end of day: nothing further, graphs quiet"))
        events.append((open_s, 0, OPEN.format(t=ticket, hhmm=t_open, cause=cause, path=ipath)))
        events.append((res_s, 0, RESOLVE.format(t=ticket, hhmm=t_res, path=ipath)))
        events.sort()
        with open(os.path.join(LOGS, f"{day.isoformat()}.log"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(text for _, _, text in events) + "\n")
        old_tickets[di % len(old_tickets)] = (ticket, ipath)  # yesterday's incident becomes tomorrow's decoy

    top_paths = sorted(path_counts.items(), key=lambda kv: (-kv[1], kv[0]))[:5]
    avg = {p: round(latency_sum[p] / latency_n[p], 1) for p in latency_n}
    slowest = sorted(avg.items(), key=lambda kv: (-kv[1], kv[0]))[:3]
    # the keys must be unambiguous: no ties at the cut-off of either ranking
    counts_sorted = sorted(path_counts.values(), reverse=True)
    assert counts_sorted[4] > counts_sorted[5], "tie at top-5 boundary"
    avgs_sorted = sorted(avg.values(), reverse=True)
    assert avgs_sorted[2] > avgs_sorted[3], "tie at slowest-3 boundary"
    assert len({a for _, a in slowest}) == 3
    expected = {
        "status_counts": dict(sorted(status_counts.items())),
        "top_paths_5": [[p, c] for p, c in top_paths],
        "errors_by_hour": dict(sorted(errors_by_hour.items())),
        "slowest_3": [[p, a] for p, a in slowest],
        "total_lines": total_lines,
    }
    with open(os.path.join(HERE, "expected.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(expected, f, indent=2)
        f.write("\n")
    with open(os.path.join(HERE, "incidents.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(incidents, f, indent=2)
        f.write("\n")
    # each ticket is OPENED in exactly one file (it may be mentioned as a decoy on a later day), and each
    # file opens exactly one ticket
    corpus = {d: open(os.path.join(LOGS, f"{d}.log"), encoding="utf-8").read() for d in incidents}
    for d, inc in incidents.items():
        assert sum((inc["ticket"] + " opened") in txt for txt in corpus.values()) == 1, inc
        assert corpus[d].count(" opened ") == 1, d
    print("request lines", total_lines, "top", top_paths, "slowest", slowest)


if __name__ == "__main__":
    main()
