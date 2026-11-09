"""Builds B:\\published\\_dashboard\\reviewd.html from the Reviewd runners' review databases.

Read-only: opens each reviews.db with mode=ro and never writes to a runner. Run by the
"Reviewd Dashboard" scheduled task every 5 minutes; the page reloads itself every minute.
"""
import datetime as dt
import html
import json
import re
import sqlite3
import sys
from collections import Counter, defaultdict
from pathlib import Path

PUBLISHED = Path(r"B:\published")
RUNNERS = [("GitHub", PUBLISHED / "Reviewd"), ("ADO", PUBLISHED / "ADO-Reviewd")]
OUT = PUBLISHED / "_dashboard" / "reviewd.html"
RECENT = 50
MAX_AGE_HOURS = 168
MAX_RESETS = 3  # PrSelection.MaxResetRetries: a head the reaper reset this often waits for a new push
TERMINAL = {"completed-verified", "failed-released", "released"}
STAGE_ORDER = {s: i for i, s in enumerate([
    "prepare-pending", "review-running", "review-completed", "confirmation-running",
    "confirmation-completed", "verify-pending", "commit-pending", "completed-verified", "failed-released"])}
NOW = dt.datetime.now(dt.timezone.utc)


def ts(value):
    if not value:
        return None
    try:
        t = dt.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return t if t.tzinfo else t.replace(tzinfo=dt.timezone.utc)
    except ValueError:
        return None


def ago(t):
    if t is None:
        return "?"
    s = int((NOW - t).total_seconds())
    if s < 60:
        return f"{s}s ago"
    if s < 3600:
        return f"{s // 60} min ago"
    if s < 86400:
        return f"{s // 3600} h {s % 3600 // 60} min ago"
    return f"{s // 86400} d {s % 86400 // 3600} h ago"


def span(a, b):
    if a is None or b is None:
        return ""
    m = int((b - a).total_seconds() // 60)
    return f"{m} min" if m < 120 else f"{m // 60} h {m % 60} min"


def when(t):
    return "" if t is None else f'<span title="{t:%Y-%m-%d %H:%M:%SZ}">{ago(t)}</span>'


def e(x):
    return html.escape("" if x is None else str(x))


def load(path):
    db = sqlite3.connect(f"file:{(path / 'state' / 'reviews.db').as_posix()}?mode=ro", uri=True, timeout=10)
    try:
        rows = db.execute(
            "select key, value from cache where key like 'reviewd/attempt-history/%' or key like 'reviewd/review-attempt/%'"
            " or key like 'reviewd/failure/%' or key like 'reviewd/pr-snapshot/%'").fetchall()
        latest = db.execute("select value from cache where key like 'reviewd/batch-state/%' order by rowid desc limit 1").fetchone()
    finally:
        db.close()
    attempts, failures, prs, current = {}, {}, {}, set()
    # History first, then each PR's live record, so the live record wins for its claim.
    for key, value in sorted(rows, key=lambda r: r[0].startswith("reviewd/review-attempt/")):
        try:
            d = json.loads(value)
        except ValueError:
            continue
        if key.startswith(("reviewd/attempt-history/", "reviewd/review-attempt/")):
            token = d.get("claimToken") or key
            old = attempts.get(token)
            if key.startswith("reviewd/review-attempt/"):
                current.add(token)
                attempts[token] = d
            elif old is None or STAGE_ORDER.get(d.get("stage"), -1) >= STAGE_ORDER.get(old.get("stage"), -1):
                attempts[token] = d
        elif key.startswith("reviewd/failure/"):
            failures[key.split("/", 2)[2].split("/", 1)[1]] = d  # "<repo...>/<pr>" without the provider
        else:
            repo = key.split("/", 2)[2].split("/", 1)[1]
            for pr, snap in d.items():
                prs[f"{repo}/{pr}"] = snap
    # The round the batch is executing now: its queued entries start as soon as their repo has a free slot.
    round_status = {}
    if latest:
        for entry in json.loads(latest[0]).get("entries", []):
            round_status[f"{entry['repo'].lower()}/{entry['pr']}"] = (entry.get("head"), entry.get("status"))
    return list(attempts.values()), failures, prs, current, round_status


RECORD_START = re.compile(r"^(\S+ #\d+: |PR \S+ |Review stopped|Reaper|Owner recovery|Batch |info:|\s{4}|\{)")


def log_reasons(path):
    """Maps claim tokens and operation ids to the reason line the batch logged for them."""
    by_claim, by_op, last_claim = {}, {}, {}
    for name in ("batch.err.log.prev", "batch.err.log"):
        f = path / "state" / "logs" / name
        if not f.exists():
            continue
        lines = f.read_text(encoding="utf-8", errors="replace").splitlines()
        for i, line in enumerate(lines):
            m = re.match(r"^(\S+) #(\d+): .*claim ([0-9a-f]{32})", line)
            if m:
                last_claim[f"{m[1].lower()}/{m[2]}"] = m[3]
                continue
            m = re.match(r"^PR (\S+)/(\d+) [^:]*: (.*)", line)
            if not m:
                continue
            text = [m[3]]
            for nxt in lines[i + 1:i + 9]:
                if RECORD_START.match(nxt):
                    break
                text.append(nxt.strip())
            reason = " ".join(t for t in text if t)
            claim = last_claim.pop(f"{m[1].lower()}/{m[2]}", None)
            if claim:
                by_claim[claim] = reason
            for op in re.findall(r"operation ([0-9a-f]{32})", reason):
                by_op[op] = reason
    return by_claim, by_op


def pr_key(a):
    return f"{a.get('repository', '').lower()}/{a.get('prId')}"


def ended(a, failure):
    t = ts(a.get("finishedAt")) or ts(a.get("reviewCompletedAt"))
    if t is None and failure and failure.get("head") == a.get("headSha"):
        t = ts(failure.get("at"))
    return t or ts(a.get("startedAt"))


def reason_for(a, failure, logged):
    for field in ("cleanupError", "retiredReason", "operatorNote"):
        if a.get(field):
            return str(a[field])
    by_claim, by_op = logged
    if a.get("claimToken") in by_claim:
        return by_claim[a["claimToken"]]
    for op in ("prepareOperationId", "verifyOperationId", "commitOperationId", "cleanupOperationId"):
        if a.get(op) in by_op:
            return by_op[a[op]]
    if failure and failure.get("head") == a.get("headSha"):
        return str(failure.get("reason", ""))
    return "not recorded (older than the kept batch logs)"


def short_reason(text):
    # Keep both ends: the head names the failing step, the tail holds the actual error.
    text = " ".join(str(text).split()).replace(" --- sandbox output tail --- Traceback (most recent call last):", " … ")
    return text if len(text) <= 300 else text[:110] + " … " + text[-185:]


def build_runner(name, path):
    status = json.loads((path / "state" / "status.json").read_text(encoding="utf-8-sig"))
    source = {}
    if (path / "SOURCE.json").exists():
        source = json.loads((path / "SOURCE.json").read_text(encoding="utf-8-sig"))
    paused = (path / "state" / "PAUSED").exists()
    selection = {}
    sel_path = path / "state" / "selection.json"
    if sel_path.exists():
        for repo, entries in json.loads(sel_path.read_text(encoding="utf-8-sig")).get("repositories", {}).items():
            for entry in entries:
                selection[f"{repo.lower()}/{entry['prId']}"] = entry.get("headSha")
    attempts, failures, prs, current, round_status = load(path)
    logged = log_reasons(path)

    reviews = [a for a in attempts if not a.get("admissionRefused")]
    refused = [a for a in attempts if a.get("admissionRefused")]
    live = [a for a in reviews if a.get("claimToken") in current
            and a.get("stage") not in TERMINAL and a.get("cleanupState") == "owned"]
    finished = [a for a in reviews if a not in live]

    # Rounds: the nth verified review of a PR, counted in time order.
    rounds = {}
    per_pr = defaultdict(int)
    for a in sorted(reviews, key=lambda a: a.get("startedAt", "")):
        if a.get("stage") == "completed-verified":
            per_pr[pr_key(a)] += 1
            rounds[a.get("claimToken")] = per_pr[pr_key(a)]
    tries = Counter((pr_key(a), a.get("headSha")) for a in reviews)
    verified_heads = {(pr_key(a), a.get("headSha")) for a in reviews if a.get("stage") == "completed-verified"}
    live_prs = {pr_key(a) for a in live}
    # Each PR's live attempt record, for the picker's reset limit (PrSelection.MaxResetRetries).
    current_attempt = {pr_key(a): a for a in attempts if a.get("claimToken") in current}

    def title(k):
        return (prs.get(k) or {}).get("Title", "")

    def label(a):
        return f"{a.get('repository', '').split('/')[-1]} #{a.get('prId')}"

    # Reviewing now.
    now_rows = []
    for a in sorted(live, key=lambda a: a.get("startedAt", "")):
        k, f = pr_key(a), failures.get(pr_key(a))
        started = ts(a.get("startedAt"))
        stopped = f and f.get("head") == a.get("headSha") and ts(f.get("at")) and started and ts(f.get("at")) > started
        note = f"<b>stopped:</b> {e(short_reason(f.get('reason')))}" if stopped else ""
        now_rows.append(f"<tr><td>{e(label(a))}</td><td>{e(title(k))}</td><td>{e(a.get('stage'))}</td>"
                        f"<td>{when(started)}</td><td><code>{e(a.get('headSha', '')[:8])}</code></td><td>{note}</td></tr>")

    # Pending: open, non-draft PRs whose current head has no verified review and nothing running.
    pend_rows = []
    pending = 0
    for k, snap in prs.items():
        if snap.get("Lifecycle") in ("Merged", "Abandoned", "Closed") or snap.get("IsDraft"):
            continue
        head = snap.get("HeadSha")
        if (k, head) in verified_heads or k in live_prs:
            continue
        f = failures.get(k)
        created, updated = ts(snap.get("CreatedAt")), ts(snap.get("SourceUpdatedAt"))
        limit = dt.timedelta(hours=MAX_AGE_HOURS)
        in_age = (created and NOW - created <= limit) or (updated and NOW - updated <= limit)
        if f and f.get("head") == head and int(f.get("count", 0)) >= 3:
            why = f"skipped until a new push: failed {f.get('count')}x. {short_reason(f.get('reason'))}"
        elif (ca := current_attempt.get(k)) and ca.get("headSha") == head and int(ca.get("resetCount") or 0) >= MAX_RESETS:
            why = f"skipped until a new push: reset {ca.get('resetCount')}x by the slot reaper"
        elif not in_age:
            continue  # outside the age limit: Reviewd never reviews it, so it is not pending
        elif round_status.get(k) == (head, "queued"):
            busy = sum(1 for a in live if pr_key(a).rsplit("/", 1)[0] == k.rsplit("/", 1)[0])
            why = f"in the current round; starts when one of this repo's slots frees ({busy} busy)"
        elif selection.get(k) == head:
            why = "picked for the next round; starts after the current round finishes"
        else:
            why = "not in the latest pick yet (discovery runs every 5 min)"
        pending += 1
        repo, pr = k.rsplit("/", 1)
        pend_rows.append((updated or created, f"<tr><td>{e(repo.split('/')[-1])} #{e(pr)}</td><td>{e(snap.get('Title'))}</td>"
                          f"<td>{when(updated or created)}</td><td><code>{e((head or '')[:8])}</code></td><td>{e(why)}</td></tr>"))
    pend_rows = [r for _, r in sorted(pend_rows, key=lambda x: x[0] or NOW)]

    # Recently finished.
    fin_rows = []
    recent = sorted(finished, key=lambda a: ended(a, failures.get(pr_key(a))) or NOW, reverse=True)[:RECENT]
    for a in recent:
        k, f = pr_key(a), failures.get(pr_key(a))
        stage = a.get("stage")
        if stage == "completed-verified":
            outcome, cls, why = f"done, round {rounds.get(a.get('claimToken'), '?')}", "ok", ""
        elif stage == "failed-released":
            outcome, cls, why = "failed", "bad", reason_for(a, f, logged)
        elif a.get("claimToken") not in current:
            outcome, cls, why = f"abandoned at {stage}", "warn", "a later attempt replaced it before it finished"
        elif a.get("verifiedPublication"):
            outcome, cls, why = f"posted; stopped at {stage}", "warn", reason_for(a, f, logged)
        else:
            outcome, cls, why = f"stopped at {stage}", "bad", reason_for(a, f, logged)
        n = tries[(k, a.get("headSha"))]
        fin_rows.append(
            f"<tr><td>{e(label(a))}</td><td>{e(title(k))}</td><td class='{cls}'>{e(outcome)}</td>"
            f"<td>{when(ended(a, f))}</td><td>{span(ts(a.get('startedAt')), ended(a, f))}</td>"
            f"<td><code>{e(a.get('headSha', '')[:8])}</code>{f' · try {n}' if n > 1 else ''}</td>"
            f"<td title=\"{e(' '.join(str(why).split()))}\">{e(short_reason(why))}</td></tr>")

    day = [a for a in reviews if (ended(a, failures.get(pr_key(a))) or NOW) > NOW - dt.timedelta(hours=24)]
    done24 = sum(a.get("stage") == "completed-verified" for a in day)
    fail24 = sum(a.get("stage") != "completed-verified" and a not in live for a in day)
    ref24 = [a for a in refused if (ts(a.get("startedAt")) or NOW) > NOW - dt.timedelta(hours=24)]
    ref_top = Counter(short_reason(a["admissionRefused"])[:120] for a in ref24).most_common(1)

    services = " · ".join(f"{s} {'up' if status.get(s) else '<b class=bad>DOWN</b>'}"
                          for s in ("gateway", "host", "webhook", "batch"))
    if paused:
        services += " · <b class=warn>PAUSED</b>"
    checked = ts(status.get("checkedAt"))
    head = (f"<h2>{e(name)} runner</h2><p class=meta>{services} · watchdog checked {when(checked)}"
            f" · CLI <code>{e(str(source.get('cliCommit', ''))[:8])}</code></p>"
            f"<p class=tiles><span><b>{len(live)}</b> reviewing</span><span><b>{pending}</b> pending</span>"
            f"<span><b>{done24}</b> done (24 h)</span><span><b class='{'bad' if fail24 else ''}'>{fail24}</b> failed (24 h)</span></p>")
    if ref24:
        head += (f"<p class=meta>{len(ref24)} slot admissions refused in 24 h (not reviews). Most common: "
                 f"{e(ref_top[0][0])}</p>")

    def table(caption, cols, rows, empty):
        if not rows:
            return f"<h3>{caption}</h3><p class=empty>{empty}</p>"
        return (f"<h3>{caption}</h3><div class=scroll><table><thead><tr>{''.join(f'<th>{c}</th>' for c in cols)}</tr></thead>"
                f"<tbody>{''.join(rows)}</tbody></table></div>")

    return head + table("Reviewing now", ["PR", "Title", "Stage", "Started", "Head", "Note"], now_rows, "Nothing running.") \
        + table("Pending", ["PR", "Title", "Waiting since (last update)", "Head", "Why"], pend_rows, "Nothing pending.") \
        + table(f"Last {RECENT} finished", ["PR", "Title", "Outcome", "Ended", "Took", "Head", "Reason"], fin_rows, "None yet.")


def main():
    parts = []
    for name, path in RUNNERS:
        try:
            parts.append(build_runner(name, path))
        except Exception as ex:  # one broken runner must not blank the other
            parts.append(f"<h2>{e(name)} runner</h2><p class=bad>Could not read: {e(ex)}</p>")
    page = f"""<!doctype html><html lang=en><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1"><meta http-equiv=refresh content=60>
<title>Reviewd Dashboard</title><style>
:root{{--bg:#fff;--fg:#1d1d1f;--mut:#666;--line:#ddd;--ok:#1a7f37;--bad:#c62828;--warn:#9a6700;--tile:#f3f4f6}}
@media (prefers-color-scheme:dark){{:root{{--bg:#16171a;--fg:#e8e8ea;--mut:#9a9aa2;--line:#33343a;--ok:#4ac26b;--bad:#ff6b6b;--warn:#e3b341;--tile:#222328}}}}
body{{background:var(--bg);color:var(--fg);font:15px/1.45 system-ui,Segoe UI,sans-serif;margin:0 auto;padding:16px;max-width:1300px}}
h1{{font-size:22px;margin:0 0 4px}} h2{{margin:28px 0 4px;font-size:19px}} h3{{margin:18px 0 6px;font-size:16px}}
.meta{{color:var(--mut);margin:2px 0}} .empty{{color:var(--mut)}}
.tiles{{display:flex;gap:10px;flex-wrap:wrap}} .tiles span{{background:var(--tile);border-radius:8px;padding:6px 12px}}
.tiles b{{font-size:18px}} .scroll{{overflow-x:auto}}
table{{border-collapse:collapse;width:100%}} th,td{{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line);vertical-align:top}}
th{{color:var(--mut);font-weight:600;white-space:nowrap}} td:nth-child(1),td:nth-child(4){{white-space:nowrap}}
.ok{{color:var(--ok)}} .bad{{color:var(--bad)}} .warn{{color:var(--warn)}} code{{font-size:13px}}
</style></head><body><h1>Reviewd Dashboard</h1>
<p class=meta>Built {NOW:%Y-%m-%d %H:%M:%SZ}. Rebuilt every 5 min; this page reloads every minute. Hover a time for the exact UTC time.</p>
{''.join(parts)}</body></html>"""
    tmp = OUT.with_suffix(".tmp")
    tmp.write_text(page, encoding="utf-8")
    tmp.replace(OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
