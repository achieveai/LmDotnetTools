"""Reviewd alarm: tells the operator (HITL Notify) when a Reviewd runner on this machine has silently stopped reviewing.

Scheduled every 15 minutes by the "Reviewd Alarm" task. The watchdogs (ensure-reviewd.ps1) fix what they can and this
reports what is still wrong; its one action is stopping a wedged batch (see "restarted"). It reads each install's reviews.db (read-only), its PAUSED file
and the watchdog's status.json. One alert per problem, repeated after 24 h while it lasts.

  alarm.py                  check every install, alert on new problems
  alarm.py --dry-run        print the problems; send nothing, remember nothing

Problems, per install:
  unreviewed    an open, non-draft PR in the review window whose current head has no verified review, 1 h after
                the head was seen (catches a stopped, hung or wedged batch whatever the cause)
  quarantined   a PR whose head failed 3 times the same way: the batch skips it until a new push
  stuck         a review was posted but the attempt froze at commit-pending (archive step failed)
  paused        state\\PAUSED older than 1 h (a deploy that died halfway leaves it behind)
  watchdog      status.json older than 20 min: the watchdog task itself is not running
  down          gateway, host, webhook or batch reported down by the watchdog for 15 min
  restarted     it stopped a wedged batch: a PR waited 2 h, no slot lease was live, the batch was up 2 h, and no
                restart in the last 2 h. The watchdog starts a fresh batch. The one thing this script changes.
                This restart reuses slots and does not run the all-slot reset: an operator-approved exception to
                the restart policy (CLAUDE.md, samples/Reviewd.Sample/README.md). The slot reaper frees lapsed leases.
  restart-failed  that stop failed; nothing is recorded as restarted, so the next run tries again.

Exit code: 0, or 1 when a due alert could not be sent (it is retried next run).
"""
import datetime
import json
import os
import queue
import re
import sqlite3
import subprocess
import sys
import threading
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
# REVIEWD_ALARM_INSTALLS (";"-separated) points it at copies, for fault-injection tests.
INSTALLS = [Path(p) for p in os.environ.get("REVIEWD_ALARM_INSTALLS", r"B:\published\Reviewd;B:\published\ADO-Reviewd")
            .split(";") if p]
STATE = HERE / "state" / "alarm-state.json"
LOG = HERE / "state" / "logs" / "alarm.log"
HEAD_GRACE = datetime.timedelta(hours=1)
PAUSE_LIMIT = datetime.timedelta(hours=1)
WATCHDOG_STALE = datetime.timedelta(minutes=20)
DOWN_LIMIT = datetime.timedelta(minutes=15)
REALERT = datetime.timedelta(hours=24)
# A PR waiting this long with no slot working means the batch is wedged: stop it so the watchdog starts a fresh one.
# At most once per install per STALL_RESTART, and never a batch younger than this.
STALL_RESTART = datetime.timedelta(hours=2)
QUARANTINE_AFTER = 3  # ReviewBatch.QuarantineAfter


def ts(value):
    if not value:
        return None
    text = re.sub(r"\.(\d{6})\d+", r".\1", str(value).replace("Z", "+00:00"))
    try:
        stamp = datetime.datetime.fromisoformat(text)
    except ValueError:
        return None
    return stamp if stamp.tzinfo else stamp.replace(tzinfo=datetime.timezone.utc)


def hours(delta):
    return f"{delta.total_seconds() / 3600:.1f} h"


def read_rows(install):
    db = sqlite3.connect(f"file:{install / 'state' / 'reviews.db'}?mode=ro", uri=True, timeout=10)
    try:
        rows = db.execute("select key, value from cache where key like 'reviewd/pr-snapshot/%' or "
                          "key like 'reviewd/review-attempt/%' or key like 'reviewd/failure/%' or "
                          "key like 'reviewd/slot-owner/%'").fetchall()
    finally:
        db.close()
    out = {}
    for key, value in rows:
        try:
            out[key] = json.loads(value)
        except ValueError:
            pass
    return out


def pr_problems(name, rows, max_age, now):
    found = []
    for key, snapshot in rows.items():
        if not key.startswith("reviewd/pr-snapshot/") or not isinstance(snapshot, dict):
            continue
        repo = key[len("reviewd/pr-snapshot/"):]
        for pr, s in snapshot.items():
            if not isinstance(s, dict) or s.get("Lifecycle") != "Open" or s.get("IsDraft"):
                continue
            updated = ts(s.get("SourceUpdatedAt")) or ts(s.get("CreatedAt"))
            seen = ts(s.get("LastChangedAt"))
            head = s.get("HeadSha")
            if not updated or not seen or not head or now - updated > max_age:
                continue
            attempt = rows.get(f"reviewd/review-attempt/{repo}/{pr}") or {}
            failure = rows.get(f"reviewd/failure/{repo}/{pr}") or {}
            if attempt.get("headSha") == head and attempt.get("stage") == "completed-verified":
                continue
            subject = f"{repo.split('/', 1)[-1]} #{pr}"
            title = (s.get("Title") or "")[:70]
            if failure.get("head") == head and (failure.get("count") or 0) >= QUARANTINE_AFTER:
                reason = (failure.get("reason") or "").splitlines()[0][:160]
                found.append((f"{name}:quarantined:{repo}#{pr}:{head}", name, "quarantined",
                              f"{subject} ({title}) failed {failure['count']}x on this head; skipped until a new "
                              f"push. Last: {reason}"))
            elif attempt.get("headSha") == head and attempt.get("stage") == "commit-pending":
                found.append((f"{name}:stuck:{repo}#{pr}:{head}", name, "stuck",
                              f"{subject} ({title}): review IS posted on the PR; only its archive commit failed "
                              f"(frozen at commit-pending), so the judge cannot use it. Not an outage."))
            elif now - seen >= HEAD_GRACE:
                state = f"last attempt {attempt.get('stage')}" if attempt.get("headSha") == head else "no attempt"
                found.append((f"{name}:unreviewed:{repo}#{pr}:{head}", name, "unreviewed",
                              f"{subject} ({title}): head {head[:8]} seen {hours(now - seen)} ago, {state}",
                              now - seen))
    return found


def working(install, now):
    """Asks Reviewd itself (review slots, read-only) whether any slot is owned under a live lease. A lane renews its
    lease every 5 minutes while it works, even through a long silent review. Unknown counts as working: a batch is
    never stopped on a guess. That includes an owned slot with no readable lease expiry (an owner from before leases):
    only a lease known to have lapsed counts as idle."""
    cfg = json.loads((install / "state" / "config" / "runner.json").read_text(encoding="utf-8-sig"))
    result = subprocess.run(["pwsh", "-NoProfile", "-File", str(install / "reviewd.ps1"), "review", "slots",
                             "--repos", ",".join(cfg["repos"]), "--db", str(install / "state" / "reviews.db"),
                             "--workspace", cfg["workspace"]], capture_output=True, text=True, timeout=300,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    slots = []
    for line in result.stdout.splitlines():
        try:
            slots.append(json.loads(line))
        except ValueError:
            pass
    if result.returncode != 0 or not slots:
        return True
    return any(s.get("state") == "owned" and ((expiry := ts(s.get("leaseExpiresAt"))) is None or expiry > now)
               for s in slots)


def batch_processes(install):
    """(pid, parent pid, started) of this install's Reviewd.Sample.exe review batch."""
    # The install path goes in through the environment, never into the script text: an apostrophe or a wildcard
    # character in it would otherwise change the query.
    script = ("$install = $env:REVIEWD_ALARM_INSTALL + '\\'; "
              "Get-CimInstance Win32_Process -Filter \"Name = 'Reviewd.Sample.exe'\" | Where-Object { "
              "$_.CommandLine -match ' review batch ' -and "
              "$_.CommandLine.IndexOf($install, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | "
              "ForEach-Object { '{0} {1} {2}' -f $_.ProcessId, $_.ParentProcessId, "
              "$_.CreationDate.ToUniversalTime().ToString('o') }")
    out = subprocess.run(["pwsh", "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=120,
                         env=dict(os.environ, REVIEWD_ALARM_INSTALL=str(install)),
                         creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
    return [(int(pid), int(parent), ts(started)) for pid, parent, started in (line.split() for line in out.splitlines()
                                                                             if len(line.split()) == 3)]


TASKKILL_NOT_FOUND = 128  # the process had already exited, which is what the stop wanted


def kill(pid, what):
    """Force-stops one process. Raises when taskkill fails for any reason but "already gone", so a failed stop is
    reported and never recorded as a restart."""
    result = subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True, text=True)
    if result.returncode not in (0, TASKKILL_NOT_FOUND):
        raise RuntimeError(f"taskkill of {what} pid {pid} exited {result.returncode}: "
                           f"{(result.stderr or result.stdout).strip()[:200]}")


def restart_stalled(install, name, rows, waiting, now, memory, dry):
    """Stops a batch that has PRs waiting STALL_RESTART with nothing working, so the watchdog starts a fresh one within
    5 minutes (the same stop ensure-reviewd.ps1 does for a batch that logged "Scheduling stopped."). Returns a line
    for the alert, or None."""
    if not waiting or (install / "state" / "PAUSED").exists() or working(install, now):
        return None
    last = ts(memory.setdefault("restarted", {}).get(name))
    if last and now - last < STALL_RESTART:
        return None
    batches = batch_processes(install)
    if len(batches) != 1 or not batches[0][2] or now - batches[0][2] < STALL_RESTART:
        return None  # none (the watchdog starts one), several (not ours to pick), or too young to judge
    pid, parent, started = batches[0]
    oldest = max(waiting)
    line = (f"stopped batch pid {pid} (up {hours(now - started)}): a PR waited {hours(oldest)} while no slot was "
            f"working. The watchdog starts a fresh batch within 5 min.")
    if dry:
        return "WOULD HAVE " + line
    wrapper = subprocess.run(["pwsh", "-NoProfile", "-Command", f"(Get-CimInstance Win32_Process -Filter 'ProcessId = "
                              f"{parent}').CommandLine"], capture_output=True, text=True, timeout=60,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
    kill(pid, "batch")
    if "reviewd.ps1" in wrapper and str(install) in wrapper:
        kill(parent, "batch wrapper")
    memory["restarted"][name] = now.isoformat()
    log({"restarted": name, "pid": pid, "line": line})
    with open(install / "state" / "logs" / "ensure.log", "a", encoding="utf-8") as out:
        out.write(f"{now.isoformat()} Reviewd Alarm: {line}\n")
    return line


def install_problems(install, now, memory, dry=False):
    name = "GitHub" if install.name == "Reviewd" else install.name.replace("-Reviewd", "")
    found = []
    state = install / "state"
    paused = state / "PAUSED"
    if paused.exists():
        age = now - datetime.datetime.fromtimestamp(paused.stat().st_mtime, datetime.timezone.utc)
        if age >= PAUSE_LIMIT:
            found.append((f"{name}:paused", name, "paused", f"paused for {hours(age)} (state\\PAUSED); no reviews start"))
    try:
        status = json.loads((state / "status.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        status = {}
    checked = ts(status.get("checkedAt"))
    if not checked or now - checked >= WATCHDOG_STALE:
        found.append((f"{name}:watchdog", name, "watchdog",
                      f"watchdog last ran {hours(now - checked) + ' ago' if checked else 'never'}; nothing restarts "
                      f"a crashed runner"))
    elif not status.get("paused"):
        for part in ("gateway", "host", "webhook", "batch"):
            key = f"{name}:down:{part}"
            # Only a part seen up counts as up. An older watchdog wrote "started" for a launch it never saw live,
            # so a batch failing at every start never reached this alert.
            if status.get(part) is not True:
                first = ts(memory.setdefault("down", {}).setdefault(key, now.isoformat()))
                if now - first >= DOWN_LIMIT:
                    found.append((key, name, "down", f"{part} down since {first:%H:%M}Z"
                                  + (" (start Docker / the gateway compose)" if part == "gateway" else "")))
            else:
                memory.setdefault("down", {}).pop(key, None)
    config = state / "config" / "runner.json"
    try:
        cfg = json.loads(config.read_text(encoding="utf-8-sig"))
        max_age = datetime.timedelta(hours=cfg["maxAgeHours"])
    except Exception as error:  # name the file that needs repair, not the database
        return found + [(f"{name}:unreadable-config", name, "unreadable", f"could not read {config}: "
                         f"{type(error).__name__}: {str(error)[:160]}")]
    try:
        rows = read_rows(install)
        prs = pr_problems(name, rows, max_age, now)
    except Exception as error:  # a locked or missing database is a problem of its own, not a crash
        return found + [(f"{name}:unreadable", name, "unreadable", f"could not read reviews.db: "
                         f"{type(error).__name__}: {str(error)[:160]}")]
    found += [p[:4] for p in prs]
    waiting = [p[4] for p in prs if len(p) > 4 and p[4] >= STALL_RESTART]
    try:
        restarted = restart_stalled(install, name, rows, waiting, now, memory, dry)
    except Exception as error:
        if not dry:
            log({"restart_failed": name, "error": traceback.format_exc()})
        # A stable key: a stop that keeps failing alerts once a day (REALERT), not at every run.
        return found + [(f"{name}:restart-failed", name, "restart-failed",
                         f"stall restart FAILED: {type(error).__name__}: {str(error)[:160]} (traceback in alarm.log)")]
    if restarted:
        found.append((f"{name}:restarted:{now:%Y%m%dT%H%M}", name, "restarted", restarted))
    return found


def notify(title, body):
    """One HITL Notify through the HITL MCP server's stdio protocol (the server Claude Code starts, from
    ~/.claude.json). Returns False on any failure so the alert is retried next run."""
    server = json.loads((Path.home() / ".claude.json").read_text(encoding="utf-8"))["mcpServers"]["hitl"]
    env = dict(os.environ, **(server.get("env") or {}))
    proc = subprocess.Popen([server["command"], *server.get("args", [])], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env, text=True,
                            encoding="utf-8", creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    lines = queue.Queue()
    threading.Thread(target=lambda: [lines.put(line) for line in proc.stdout], daemon=True).start()
    try:
        def send(message):
            proc.stdin.write(json.dumps(message) + "\n")
            proc.stdin.flush()

        def reply(want, wait=60):
            end = datetime.datetime.now() + datetime.timedelta(seconds=wait)
            while datetime.datetime.now() < end:
                try:
                    line = lines.get(timeout=1)
                except queue.Empty:
                    continue
                try:
                    message = json.loads(line)
                except ValueError:
                    continue
                if message.get("id") == want:
                    return message
            return None

        send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "reviewd-alarm", "version": "1"}}})
        if not reply(1):
            raise RuntimeError("HITL server did not initialize")
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "NameSession", "arguments": {
            "name": "Reviewd alarm", "objective": "Tell the operator when a Reviewd runner has silently stopped reviewing."}}})
        reply(2)
        send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "Notify", "arguments": {
            "title": title, "body": body, "context": "Reviewd alarm (automatic, every 15 min, B:\\published\\Reviewd)"}}})
        answer = reply(3)
        if not answer or answer.get("error") or (answer.get("result") or {}).get("isError"):
            raise RuntimeError(f"Notify was not accepted: {str(answer)[:200]}")
        return True
    finally:
        proc.kill()  # cmd /c wrapper: kill the tree so the node server does not linger
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)


def log(entry):
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with open(LOG, "a", encoding="utf-8") as out:
        out.write(json.dumps({"t": datetime.datetime.now(datetime.timezone.utc).isoformat(), **entry}) + "\n")


def main(argv):
    dry = "--dry-run" in argv
    now = datetime.datetime.now(datetime.timezone.utc)
    try:
        memory = json.loads(STATE.read_text(encoding="utf-8"))
    except FileNotFoundError:  # the first run
        memory = {}
    except (OSError, ValueError) as error:  # cooldowns and down timers start over: say so
        memory = {}
        if not dry:
            log({"state_unreadable": f"{type(error).__name__}: {str(error)[:300]}",
                 "effect": "alert cooldowns, down timers and restart history start over"})
    alerted = memory.setdefault("alerted", {})
    found = []
    for install in INSTALLS:
        if (install / "state").exists():
            found += install_problems(install, now, memory, dry)
    current = {key for key, *_ in found}
    for key in [k for k in alerted if k not in current]:
        if not dry:
            log({"cleared": key})
        alerted.pop(key)
    # A stuck archive cannot be recovered (the reaper reset the slot that held the review files), so it is told once.
    due = [p for p in found if not ts(alerted.get(p[0]))
           or (p[2] != "stuck" and now - ts(alerted[p[0]]) >= REALERT)]
    if dry:
        for key, name, kind, detail in found:
            print(f"{'DUE ' if (key, name, kind, detail) in due else 'seen'} {name} {kind}: {detail}")
        return 0
    log({"run": True, "problems": len(found), "due": len(due)})
    sent = True
    if due:
        body = "\n".join(f"- **{name}** {kind}: {detail}" for _, name, kind, detail in due)
        body += "\n\nCheck: `py B:\\published\\Reviewd\\alarm.py --dry-run`. Watchdog logs: `state\\logs\\ensure.log`."
        try:
            sent = notify(f"Reviewd: {len(due)} problem(s) need you", body)
        except Exception as error:  # an alert must never be lost silently: log it and retry next run
            sent = False
            log({"notify_failed": f"{type(error).__name__}: {str(error)[:300]}", "body": body})
        if sent:
            for key, *_ in due:
                alerted[key] = now.isoformat()
            log({"alerted": [key for key, *_ in due]})
    # Write beside the state and replace it in one step, so an interrupted run cannot leave a truncated file.
    temporary = STATE.with_suffix(".tmp")
    temporary.write_text(json.dumps(memory, indent=1), encoding="utf-8")
    temporary.replace(STATE)
    return 0 if sent else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
