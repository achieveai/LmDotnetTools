#!/usr/bin/env python3
"""Reviewd doctor: checks every Reviewd install on this machine, applies the safe fixes, and tells Kay9 only what it
could not fix, plus one daily digest. Run by reviewd-doctor.timer every 15 minutes.

  doctor.py check              read-only: print every problem (exit 1 if any)
  doctor.py tick [--dry-run]   check, fix, alert, and send the daily digest when due
  doctor.py digest [--dry-run] build the digest now; send it unless --dry-run
  doctor.py unquarantine <store, e.g. viva-glint, or NOVA> <PR id>
                               forget a PR's repeated failures, so the next pick reviews it again

It launches no reviews and keeps no review state. It reads each install's database and logs, and acts only through
that install's own bin/ scripts or systemctl. It never restarts the shared review host. Its memory (what it already
tried or alerted) is data/doctor-state.json in the install it runs from; its log is logs/doctor.log, one JSON line
per fix, alert and cleared problem.
"""
import datetime
import glob
import json
import os
import re
import select
import sqlite3
import subprocess
import sys
import time
from pathlib import Path

HOME = Path(__file__).resolve().parent.parent  # the install this copy runs from (Nova's)
ROOT = Path(os.environ.get("REVIEWD_ROOT", HOME.parent))
NO_PROGRESS = datetime.timedelta(hours=2)
NO_PICK = datetime.timedelta(minutes=50)
# Keep-alive picks within 10 minutes of a finished selection; give it that long before calling a pick missing.
PICK_GRACE = datetime.timedelta(minutes=15)
RESET_FAILED = datetime.timedelta(minutes=60)
RUNAWAY = datetime.timedelta(hours=3)
LONG_REVIEW = datetime.timedelta(hours=3)
MAX_FIX_ATTEMPTS = 2
ALERT_AFTER = datetime.timedelta(minutes=30)
REALERT = datetime.timedelta(hours=24)
DIGEST_HOUR = 9
MAX_RESTARTS_PER_TICK = 2
# safe-restart-batch.sh's exit when another restart of that install holds its lock: try again next tick.
RESTART_BUSY = 75
# ReviewBatch.QuarantineAfter. The digest lists a quarantine only while it is recent: a failure record is never
# deleted when its head moves on, so an old one says nothing about today.
QUARANTINE_AFTER = 3
QUARANTINE_LISTED = datetime.timedelta(days=7)
# The same lines keepalive.sh reads as scheduling progress, plus quarantines.
SCHEDULING = re.compile(r"^(Batch scheduling finished|\S.* #\d+: (prepare|verify|commit|owner cleanup)|PR \S.* "
                        r"(requeued|held on lane|quarantined)|Lane \S.* held|Review stopped)")
FAILURE = re.compile(r"^PR \S.* (?:requeued|held on lane \d+|quarantined after \d+ identical failures on head \S+): (.*)")
# Reaper lines that mean the slot's conversation still has live agents: the reaper waits on those by design.
ACTIVE = ("still active", "in progress", "did not quiesce")


def ts(value):
    if not value:
        return None
    text = re.sub(r"\.(\d{6})\d+", r".\1", str(value).replace("Z", "+00:00"))
    try:
        stamp = datetime.datetime.fromisoformat(text)
    except ValueError:
        return None
    return stamp if stamp.tzinfo else stamp.replace(tzinfo=datetime.timezone.utc)


def signature(message):
    # ReviewBatch.FailureSignature's normalization, so one kind of failure is one signature.
    return re.sub(r"[0-9]+", "N", re.sub(r"[0-9a-fA-F]{8,}", "#", message)).strip()


class Install:
    def __init__(self, path):
        self.path = Path(path)
        group = self.path.name[len("reviewd-"):] if self.path.name.startswith("reviewd-") else ""
        self.name = group or "NOVA"
        self.timer = f"reviewd-keepalive@{group}.timer" if group else "reviewd-keepalive.timer"
        self.service = self.timer.replace(".timer", ".service")
        self.db = self.path / "data" / "reviews.db"
        self.batch_log = self.path / "logs" / "review-batch.log"
        self.keepalive_log = self.path / "logs" / "keepalive.log"


def installs(root=None):
    root = Path(root or ROOT)
    return [Install(p) for p in sorted(glob.glob(str(root / "reviewd*"))) if (Path(p) / "bin" / "env.sh").exists()]


class Probe:
    """Every read of the live system that is not a file, so tests can replace it."""

    def batch_pids(self, install):
        out = subprocess.run(["bash", "-c", 'source "$1/bin/env.sh" >/dev/null 2>&1; install_batch_pids', "_",
                              str(install.path)], capture_output=True, text=True, timeout=60).stdout
        return out.split()

    def batch_release(self, pid):
        try:
            found = re.search(r"releases/cli/([^/]+)/", Path(f"/proc/{pid}/maps").read_text())
        except OSError:
            return None
        return found and found.group(1)

    def linked_release(self, install):
        target = os.path.realpath(install.path / "cli")
        return os.path.basename(target) if "/releases/cli/" in target else None

    def timer_active(self, name):
        return subprocess.run(["systemctl", "--user", "is-active", "--quiet", name]).returncode == 0

    def http_status(self, url):
        return subprocess.run(["curl", "-s", "-o", "/dev/null", "-m", "10", "-w", "%{http_code}", url],
                              capture_output=True, text=True).stdout.strip()


def read_db(install):
    if not install.db.exists():
        return []
    db = sqlite3.connect(f"file:{install.db}?mode=ro", uri=True, timeout=5)
    try:
        rows = db.execute("select key, value from cache where key like 'reviewd/review-attempt/%' or key like "
                          "'reviewd/slot-owner/%' or key like 'reviewd/failure/%' or key like 'reviewd/review-run/%' "
                          "or key like 'reviewd/attempt-history/%'").fetchall()
    finally:
        db.close()
    out = []
    for key, value in rows:
        try:
            row = json.loads(value)
        except ValueError:
            continue
        if isinstance(row, dict):
            out.append((key, row))
    return out


def log_lines(path):
    # The newest rotated copy too: a daily copytruncate empties the live log.
    lines = []
    rotated = sorted((p for p in glob.glob(str(path) + "-*") if not p.endswith(".gz")), key=os.path.getmtime)
    for p in rotated[-1:] + [str(path)]:
        if os.path.exists(p):
            with open(p, errors="replace") as source:
                lines.extend(source.read().splitlines())
    return lines


def facts(install, now, probe):
    rows = read_db(install)
    lines = log_lines(install.batch_log)
    scheduling = [line for line in lines if SCHEDULING.match(line)]
    picks = [stamp for stamp in (ts(m.group(1)) for m in (re.match(r"^\[(\S+)\] pick.sh exit", line)
                                                         for line in log_lines(install.keepalive_log)) if m) if stamp]
    pids = probe.batch_pids(install)
    owners, in_flight = [], []
    for key, row in rows:
        if key.startswith("reviewd/slot-owner/") and row.get("state") == "owned" and ts(row.get("leaseExpiresAt")):
            subject = f"{row.get('repository')}#{row.get('prId')}/slot{row.get('slot')}"
            reaper = [line for line in lines if line.startswith("Reaper ") and f" {subject}: " in line]
            owners.append({"subject": subject, "lapsed": now - ts(row["leaseExpiresAt"]),
                           "reaper": reaper[-1] if reaper else ""})
        elif (key.startswith("reviewd/review-attempt/") and row.get("cleanupState") != "released"
              and row.get("stage") != "completed-verified" and ts(row.get("startedAt"))):
            in_flight.append({"subject": f"{row.get('repository')}#{row.get('prId')}", "stage": row.get("stage"),
                              "age": now - ts(row["startedAt"])})
    return {
        "install": install.name,
        "timer_active": probe.timer_active(install.timer),
        "pids": pids,
        "release": probe.batch_release(pids[0]) if len(pids) == 1 else None,
        "linked": probe.linked_release(install),
        "last_line": scheduling[-1] if scheduling else "",
        "lines": len(scheduling),
        "finished": bool(scheduling) and scheduling[-1].startswith("Batch scheduling finished"),
        "last_pick": max(picks) if picks else None,
        "owners": owners,
        "in_flight": in_flight,
        "failures": [m.group(1) for m in map(FAILURE.match, lines) if m],
        "rows": rows,
    }


def problems(f, now, memory):
    """Problems for one install. memory[install] records when the doctor first saw the current newest scheduling line
    (the no-progress clock); this function updates it."""
    found, name = [], f["install"]

    def add(kind, detail, fix=None, subject=""):
        found.append({"key": f"{name}:{kind}:{subject}", "install": name, "kind": kind, "detail": detail, "fix": fix})

    if not f["timer_active"]:
        add("timer-inactive", "keep-alive timer is not active", "start-timer")
    if len(f["pids"]) != 1:
        add("batch-down", f"expected 1 batch process, found {len(f['pids'])} (keep-alive starts a missing one)")
    elif f["release"] and f["linked"] and f["release"] != f["linked"]:
        add("stale-release", f"batch runs {f['release']}, installed CLI is {f['linked']}", "restart-batch")
    seen = memory.setdefault(name, {})
    # A lane renews its slot's lease every 5 minutes for as long as it works, including while it waits on a long
    # review that prints nothing. A live lease is a working batch; long-review reports it at 3 h instead.
    working = any(owner["lapsed"] < datetime.timedelta(0) for owner in f["owners"])
    if seen.get("line") != [f["last_line"], f["lines"]] or not ts(seen.get("since")):
        seen["line"], seen["since"] = [f["last_line"], f["lines"]], now.isoformat()
    elif not f["finished"] and f["lines"] and not working and now - ts(seen["since"]) >= NO_PROGRESS:
        add("no-progress", f"no scheduling progress since {seen['since'][:16]}Z; last: {f['last_line'][:160]}",
            "restart-batch")
    if (f["finished"] and now - ts(seen["since"]) >= PICK_GRACE
            and (f["last_pick"] is None or now - f["last_pick"] >= NO_PICK)):
        add("no-pick", "selection finished but no new pick for over 50 min", "run-keepalive")
    for owner in f["owners"]:
        active = any(word in owner["reaper"] for word in ACTIVE)
        minutes = int(owner["lapsed"].total_seconds() // 60)
        if active and owner["lapsed"] >= RUNAWAY:
            add("runaway-agents", f"{owner['subject']}: agents still active {minutes} min after the lease lapsed",
                subject=owner["subject"])
        elif not active and owner["lapsed"] >= RESET_FAILED:
            add("slot-not-reset", f"{owner['subject']}: lease lapsed {minutes} min, not reset. Last reaper line: "
                f"{owner['reaper'][:200] or 'none'}", subject=owner["subject"])
    for attempt in f["in_flight"]:
        if attempt["age"] >= LONG_REVIEW:
            add("long-review", f"{attempt['subject']} at {attempt['stage']} for "
                f"{int(attempt['age'].total_seconds() // 60)} min", subject=attempt["subject"])
    return found


def inspect(install, now, probe, memory):
    """Problems and failure lines for one install. Not being able to read it (a locked database, a hung probe) is a
    problem of its own, so one bad install never stops the tick for the others."""
    try:
        f = facts(install, now, probe)
        return problems(f, now, memory), f["failures"]
    except Exception as error:
        return [{"key": f"{install.name}:doctor-error:", "install": install.name, "kind": "doctor-error",
                 "detail": f"could not check this store: {type(error).__name__}: {str(error)[:200]}",
                 "fix": None}], []


def fleet_problems(probe):
    try:
        return host_problems(probe)
    except Exception as error:
        return [{"key": "fleet:doctor-error:", "install": "fleet", "kind": "doctor-error",
                 "detail": f"could not check the host: {type(error).__name__}: {str(error)[:200]}", "fix": None}]


def host_problems(probe):
    found = []
    if probe.http_status("http://127.0.0.1:5000/") != "200":
        found.append({"key": "fleet:host-down:", "install": "fleet", "kind": "host-down",
                      "detail": "review host :5000 is not answering 200 (Nova's keep-alive restarts it)", "fix": None})
    if probe.http_status("http://172.17.0.1:5206/api/auth/webhook/ado") != "405":
        found.append({"key": "fleet:auth-down:", "install": "fleet", "kind": "auth-down",
                      "detail": "auth serve :5206 is not answering", "fix": "start-auth"})
    return found


def load_state(path=None):
    path = Path(path or HOME / "data" / "doctor-state.json")
    try:
        state = json.loads(path.read_text())
    except (OSError, ValueError):
        state = {}
    state.setdefault("memory", {})
    state.setdefault("problems", {})
    state.setdefault("signatures", None)
    state.setdefault("lastDigest", "")
    return state


def save_state(state, path=None):
    path = Path(path or HOME / "data" / "doctor-state.json")
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, indent=1, default=str))
    tmp.replace(path)


def record(entry, dry_run=False):
    entry = {"t": datetime.datetime.now(datetime.timezone.utc).isoformat(), "dryRun": dry_run, **entry}
    with open(HOME / "logs" / "doctor.log", "a") as out:
        out.write(json.dumps(entry) + "\n")


def apply_fix(problem, root=None):
    """Runs the one allowed fix for a problem. The shared host is never restarted here."""
    every = installs(root)
    install = next((i for i in every if i.name == problem["install"]), None)
    nova = next((i for i in every if i.name == "NOVA"), None)
    commands = {
        "start-timer": lambda: ["systemctl", "--user", "start", install.timer],
        "run-keepalive": lambda: ["systemctl", "--user", "start", "--no-block", install.service],
        "restart-batch": lambda: [str(install.path / "bin" / "safe-restart-batch.sh")],
        "start-auth": lambda: [str(nova.path / "bin" / "start-auth.sh")],
    }
    if (nova if problem["fix"] == "start-auth" else install) is None:
        return 1, f"no install found for {problem['fix']}"
    try:
        result = subprocess.run(commands[problem["fix"]](), capture_output=True, text=True, timeout=1800,
                                stdin=subprocess.DEVNULL)
    except (OSError, subprocess.TimeoutExpired) as error:
        return 1, str(error)
    return result.returncode, (result.stdout + result.stderr).strip()[-400:]


def notify(title, body):
    """Sends one HITL Notify through the HITL MCP server's stdio protocol, the same server Claude Code starts. Its
    command comes from ~/.claude.json and is never logged. On any failure the alert goes to logs/doctor-alerts.log
    and the caller tries again next tick."""
    try:
        server = json.loads((Path.home() / ".claude.json").read_text())["mcpServers"]["hitl"]
        env = dict(os.environ, **(server.get("env") or {}))
        proc = subprocess.Popen([server["command"], *server.get("args", [])], stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env, text=True)
        try:
            def send(message):
                proc.stdin.write(json.dumps(message) + "\n")
                proc.stdin.flush()

            def reply(want):
                end = time.time() + 30
                while time.time() < end:
                    ready, _, _ = select.select([proc.stdout], [], [], 1)
                    if ready:
                        line = proc.stdout.readline()
                        if not line:
                            return None
                        try:
                            message = json.loads(line)
                        except ValueError:
                            continue
                        if message.get("id") == want:
                            return message
                return None

            send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2025-06-18", "capabilities": {},
                "clientInfo": {"name": "reviewd-doctor", "version": "1"}}})
            if not reply(1):
                raise RuntimeError("HITL server did not initialize")
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "NameSession", "arguments": {
                "name": "Reviewd doctor",
                "objective": "Keep all Reviewd stores reviewing; alert only on what it cannot fix."}}})
            reply(2)
            send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "Notify", "arguments": {
                "title": title, "body": body, "context": "Reviewd doctor (automatic, every 15 min)"}}})
            answer = reply(3)
            if not answer or answer.get("error") or (answer.get("result") or {}).get("isError"):
                raise RuntimeError("Notify was not accepted")
            return True
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
    except Exception as error:  # an alert must never be lost silently
        with open(HOME / "logs" / "doctor-alerts.log", "a") as out:
            out.write(json.dumps({"t": datetime.datetime.now(datetime.timezone.utc).isoformat(), "title": title,
                                  "body": body, "error": str(error)}) + "\n")
        return False


def tick(now, probe, dry_run=False, root=None, state_path=None, notifier=None, fixer=None):
    notifier = notifier or notify
    fixer = fixer or (lambda problem: apply_fix(problem, root))
    state = load_state(state_path)
    found = fleet_problems(probe)
    seen_failures = []
    for install in installs(root):
        install_found, failures = inspect(install, now, probe, state["memory"])
        found += install_found
        seen_failures += [(install.name, signature(reason)) for reason in failures]
    # New kinds of failure: the first tick only learns what exists; later ticks alert once per new signature. A
    # signature is learned only once its alert went out, so a failed Notify is tried again next tick.
    if state["signatures"] is None:
        state["signatures"] = sorted({sig for _, sig in seen_failures})
    else:
        for name, sig in dict.fromkeys(seen_failures):
            if sig not in state["signatures"]:
                found.append({"key": f"{name}:new-failure:{sig[:80]}", "install": name, "kind": "new-failure",
                              "detail": f"new kind of review failure: {sig[:300]}", "fix": None, "signature": sig})
    restarts, restarted, alerts = 0, set(), []
    current = {p["key"] for p in found}
    for key in [k for k in state["problems"] if k not in current]:
        record({"key": key, "result": "cleared"}, dry_run)
        del state["problems"][key]
    for p in found:
        entry = state["problems"].setdefault(p["key"], {"first": now.isoformat(), "attempts": 0, "alerted": None})
        entry["detail"] = p["detail"]
        if p["fix"] and entry["attempts"] < MAX_FIX_ATTEMPTS:
            if p["fix"] == "restart-batch" and (p["install"] in restarted or restarts >= MAX_RESTARTS_PER_TICK):
                continue  # one restart per install per tick covers every problem it fixes; the rest wait a tick
            code, output = (0, "dry run") if dry_run else fixer(p)
            busy = p["fix"] == "restart-batch" and code == RESTART_BUSY
            entry["attempts"] += 0 if busy else 1
            if p["fix"] == "restart-batch":
                restarts += 1
                restarted.add(p["install"])
            record({"key": p["key"], "fix": p["fix"], "exit": code, "output": output, "busy": busy}, dry_run)
            continue
        age = now - (ts(entry["first"]) or now)
        due = (p["kind"] == "new-failure" or (p["fix"] is None and age >= ALERT_AFTER)
               or entry["attempts"] >= MAX_FIX_ATTEMPTS)
        if due and (not ts(entry["alerted"]) or now - ts(entry["alerted"]) >= REALERT):
            alerts.append((p, entry))
    if alerts:
        body = "\n".join(f"- **{p['install']}** {p['kind']}: {p['detail']}" for p, _ in alerts)
        body += "\n\nThe doctor already tried its safe fix where it has one. These need a person."
        sent = True if dry_run else notifier(f"Reviewd: {len(alerts)} problem(s) need you", body)
        for p, entry in alerts:
            record({"key": p["key"], "alert": sent}, dry_run)
            if sent and not dry_run:
                entry["alerted"] = now.isoformat()
    for p in found:
        if p.get("signature") and state["problems"][p["key"]]["alerted"] and p["signature"] not in state["signatures"]:
            state["signatures"].append(p["signature"])
    today = now.strftime("%Y-%m-%d")
    if now.hour >= DIGEST_HOUR and state["lastDigest"] != today:
        text = digest(now, probe, root)
        if dry_run:
            print(text)
        elif notifier("Reviewd daily digest", text):
            state["lastDigest"] = today
    if not dry_run:
        save_state(state, state_path)
    for p in found:
        print(f"{p['install']:34} {p['kind']:16} {p['detail']}")
    return 0


def digest(now, probe, root=None):
    since = now - datetime.timedelta(hours=24)
    lines, total, every, quarantined = [], 0, installs(root), False
    listed_after = now - QUARANTINE_LISTED
    for install in every:
        try:
            rows, best = read_db(install), {}
        except Exception as error:
            lines.append(f"- **{install.name}**: could not read its database ({type(error).__name__})")
            continue
        for key, row in rows:
            if key.startswith(("reviewd/review-run/", "reviewd/review-attempt/", "reviewd/attempt-history/")) \
                    and row.get("startedAt"):
                k = (row.get("repository"), str(row.get("prId")), row.get("claimToken") or row.get("startedAt"))
                rank = (bool(row.get("finishedAt")), row.get("cleanupState") == "released")
                if k not in best or rank > best[k][0]:
                    best[k] = (rank, row)
        attempts = [row for _, row in best.values()]
        posted = sum(1 for r in attempts if r.get("stage") == "completed-verified"
                     and (ts(r.get("finishedAt")) or since) > since)
        failed = sum(1 for r in attempts if r.get("stage") == "failed-released"
                     and (ts(r.get("startedAt")) or since) > since)
        skipped = [f"{key.split('/')[-2]}#{key.split('/')[-1]} (head {str(row.get('head'))[:8]})"
                   for key, row in rows if key.startswith("reviewd/failure/")
                   and (row.get("count") or 0) >= QUARANTINE_AFTER
                   and (ts(row.get("at")) or listed_after) > listed_after]
        quarantined = quarantined or bool(skipped)
        total += posted
        if posted or failed or skipped:
            line = f"- **{install.name}**: {posted} posted"
            line += f", {failed} failed" if failed else ""
            line += f", skipped until next push: {', '.join(skipped)}" if skipped else ""
            lines.append(line)
    actions = []
    log = HOME / "logs" / "doctor.log"
    if log.exists():
        for raw in log.read_text().splitlines():
            try:
                entry = json.loads(raw)
            except ValueError:
                continue
            if (entry.get("fix") and not entry.get("dryRun") and not entry.get("busy")
                    and (ts(entry.get("t")) or since) > since):
                actions.append(f"{entry['key']} → {entry['fix']} (exit {entry.get('exit')})")
    text = f"**{total} reviews posted in the last 24 h** across {len(every)} stores.\n\n"
    text += "\n".join(lines) or "- no activity"
    if quarantined:
        text += ("\n\nA PR skipped until its next push failed the same way 3 times. To review it again anyway: "
                 "`doctor.py unquarantine <store> <PR id>`.")
    if actions:
        text += "\n\n**Fixes the doctor made:**\n" + "\n".join(f"- {a}" for a in actions[-10:])
    return text


def unquarantine(store, pr, root=None):
    """Deletes the store's failure records for one PR, so its next pick admits it again. Returns the deleted keys."""
    install = next((i for i in installs(root) if store in (i.name, i.path.name)), None)
    if install is None:
        raise ValueError(f"no install named {store}")
    db = sqlite3.connect(install.db, timeout=30)
    try:
        with db:
            keys = [key for (key,) in db.execute("select key from cache where key like 'reviewd/failure/%'")
                    if key.rsplit("/", 1)[-1] == pr]
            db.executemany("delete from cache where key = ?", [(key,) for key in keys])
    finally:
        db.close()
    return keys


def main(argv):
    command = argv[1] if len(argv) > 1 else "check"
    now = datetime.datetime.now(datetime.timezone.utc)
    probe = Probe()
    if command == "unquarantine" and len(argv) == 4:
        keys = unquarantine(argv[2], argv[3])
        for key in keys:
            print(f"deleted {key}")
        print(f"{len(keys)} failure record(s) deleted" + ("; the next pick includes the PR again" if keys else ""))
        return 0 if keys else 1
    if command == "check":
        memory = load_state()["memory"]
        found = fleet_problems(probe) + [p for i in installs() for p in inspect(i, now, probe, memory)[0]]
        for p in found:
            print(f"{p['install']:34} {p['kind']:16} {p['detail']}")
        print(f"problems: {len(found)}")
        return 1 if found else 0
    if command == "tick":
        return tick(now, probe, dry_run="--dry-run" in argv)
    if command == "digest":
        text = digest(now, probe)
        print(text)
        if "--dry-run" not in argv:
            notify("Reviewd daily digest", text)
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
