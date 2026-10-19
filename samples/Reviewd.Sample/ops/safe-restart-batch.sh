#!/usr/bin/env bash
# Restarts the batch at a moment when every owned attempt can resume: waits up to 15 minutes, kills
# the batch, runs the optional command (for example a deploy.sh cli ...), then starts the batch again.
#   safe-restart-batch.sh [command args...]
set -uo pipefail
OPS=$(dirname "$(readlink -f "$0")")
source "$OPS/env.sh"
resumable() {
    python3 -I - "$DB" <<'PY'
import json, sqlite3, sys
db = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
ok = True
for _, value in db.execute("select key, value from cache where key like 'reviewd/review-attempt/%'"):
    attempt = json.loads(value)
    running = attempt["stage"] in ("review-running", "confirmation-running") and attempt.get("reviewInputId")
    if attempt.get("cleanupState") != "released" and not running:
        ok = False
print("1" if ok else "0")
PY
}
batch_pid() { pgrep -f '^dotnet .*Reviewd.Sample.dll review batch'; }
for _ in $(seq 1 90); do
    pid=$(batch_pid) || { echo "no batch running"; break; }
    if [ "$(resumable)" = 1 ]; then
        kill -KILL $pid
        sleep 2
        echo "killed $pid at $(date -u +%T)"
        break
    fi
    sleep 10
done
if batch_pid >/dev/null; then
    echo "no resumable moment in 15 minutes; batch left running, command not run"
    exit 1
fi
if [ $# -gt 0 ]; then
    "$@" || echo "command failed; restarting the batch unchanged"
fi
nohup setsid "$OPS/start-batch.sh" >/dev/null 2>&1 < /dev/null &
sleep 5
batch_pid >/dev/null && echo "batch restarted" || echo "batch did not start; see $LOGS/review-batch.log"
