#!/usr/bin/env bash
# Restarts this install's batch at a moment when every owned attempt can resume: waits up to 15
# minutes, kills the batch, runs the optional command (for example a deploy.sh cli ...), then starts
# the batch again. Only a batch whose --db is this install's $DB is ever signalled.
#   safe-restart-batch.sh [command args...]
set -uo pipefail
OPS=$(dirname "$(readlink -f "$0")")
source "$OPS/env.sh"
POLLS=${RESTART_POLLS:-90}
POLL_SECONDS=${RESTART_POLL_SECONDS:-10}
# Prints 1 only when every attempt is released or waiting on a review it already sent. Anything it
# cannot read, including a database the frozen batch holds locked, prints 0.
resumable() {
    python3 -I - "$DB" <<'PY'
import json, sqlite3, sys
try:
    db = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True, timeout=1)
    ok = True
    for _, value in db.execute("select key, value from cache where key like 'reviewd/review-attempt/%'"):
        attempt = json.loads(value)
        running = attempt["stage"] in ("review-running", "confirmation-running") and attempt.get("reviewInputId")
        if attempt.get("cleanupState") != "released" and not running:
            ok = False
    print("1" if ok else "0")
except Exception:
    print("0")
PY
}
# One pid, nothing when no batch runs, and a failure when more than one batch uses $DB.
batch_pid() {
    local pids
    pids=$(install_batch_pids) || return 2
    case $(grep -c . <<<"$pids") in
        0) return 1 ;;
        1) echo "$pids" ;;
        *) echo "more than one batch uses $DB ($(echo $pids)); stop the extra one by hand" >&2; return 2 ;;
    esac
}
# A batch frozen below must never be left stopped, whatever ends this script.
frozen=""
trap '[ -n "$frozen" ] && kill -CONT "$frozen" 2>/dev/null' EXIT
trap 'exit 130' INT TERM HUP
for _ in $(seq 1 "$POLLS"); do
    pid=$(batch_pid)
    case $? in
        1) echo "no batch running"; break ;;
        2) exit 1 ;;
    esac
    # Freeze the batch, then check: a stopped process cannot move an attempt to a stage that cannot
    # resume between the check and the kill, and SIGKILL rolls back any transaction it left open.
    kill -STOP "$pid" 2>/dev/null || continue
    frozen=$pid
    if [ "$(resumable)" = 1 ]; then
        kill -KILL "$pid"
        frozen=""
        for _ in $(seq 1 50); do
            install_batch_pids | grep -qx "$pid" || break
            sleep 0.2
        done
        echo "killed $pid at $(date -u +%T)"
        break
    fi
    kill -CONT "$pid"
    frozen=""
    sleep "$POLL_SECONDS"
done
if [ -n "$(install_batch_pids)" ]; then
    echo "no resumable moment in $POLLS polls; batch left running, command not run"
    exit 1
fi
if [ $# -gt 0 ]; then
    "$@" || echo "command failed; restarting the batch unchanged"
fi
nohup setsid "$OPS/start-batch.sh" >/dev/null 2>&1 < /dev/null &
sleep "${RESTART_START_SECONDS:-5}"
[ -n "$(install_batch_pids)" ] && echo "batch restarted" || echo "batch did not start; see $LOGS/review-batch.log"
