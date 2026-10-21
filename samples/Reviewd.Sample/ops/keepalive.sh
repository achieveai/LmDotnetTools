#!/usr/bin/env bash
# Keep-alive for this install, run by the reviewd-keepalive user timer. Starts a missing host, auth serve or
# batch, and writes a fresh selection (pick.sh) once the current one has nothing launching, queued or retrying.
# It never reclaims slots: the batch's reaper frees a slot once its lease lapses. Log: $LOGS/keepalive.log.
set -uo pipefail
OPS=$(dirname "$(readlink -f "$0")")
source "$OPS/env.sh"
# One run at a time; a pick (about five minutes) can outlast the timer period. Whatever this starts must close
# fd 9 (9>&-): a batch or host that inherits it holds the lock for its whole life and every later run exits here.
# Batches started before that fix still hold keepalive.lock, hence the new name.
exec 9> "$REVIEWD_HOME/data/keepalive.run.lock"
flock -n 9 || exit 0
LOG=$LOGS/keepalive.log
log() { echo "[$(date -Is)] $*" >> "$LOG"; }
status() { curl -s -o /dev/null -m 10 -w '%{http_code}' "$1"; }
PICK_SPACING_MINUTES=${KEEPALIVE_PICK_SPACING_MINUTES:-30}
STAMP=$REVIEWD_HOME/data/keepalive-last-pick

# The host and auth serve are shared by every install; only the install with KEEPALIVE_MANAGES_SHARED=1 (Nova) starts them.
if [ "${KEEPALIVE_MANAGES_SHARED:-1}" = 1 ] && [ "$(status http://127.0.0.1:5000/)" != 200 ]; then
    log "host not answering 200; start-host.sh"
    "$OPS/start-host.sh" >> "$LOG" 2>&1 9>&-
fi
if [ "${KEEPALIVE_MANAGES_SHARED:-1}" = 1 ] && [ "$(status http://172.17.0.1:5206/api/auth/webhook/ado)" != 405 ]; then
    log "auth serve not answering 405; start-auth.sh"
    "$OPS/start-auth.sh" >> "$LOG" 2>&1 9>&-
fi
# safe-restart-batch.sh holds this lock from before it kills the batch until after it starts the new one. Without it,
# this run could see no batch in that gap and start a second one. The batch started here must not inherit it (8>&-).
exec 8> "$REVIEWD_HOME/data/safe-restart.lock"
if ! flock -n 8; then
    log "a safe restart is in progress; it starts the batch"
    exit 0
fi
if [ -z "$(install_batch_pids)" ]; then
    log "no batch on $DB; start-batch.sh"
    nohup setsid "$OPS/start-batch.sh" > /dev/null 2>&1 < /dev/null 8>&- 9>&- &
    exit 0
fi
exec 8>&-

# Prints "finished" only when the batch itself says so and the current selection has nothing left to launch.
# Per-PR statuses alone are not enough: a lane still waits (up to two hours) for the reaper to free a held
# PR's slot, and the batch reads a new selection only after the current one returns. So the batch log's last
# scheduling line must be "Batch scheduling finished" (the newest rotated log is read too, since a daily
# copytruncate empties the live one). A selection the batch has not adopted yet (no batch-state with its PRs)
# counts as running; an empty one as finished. Once that line is last, entries still queued are left over (a
# lane gave up on a held slot), not pending: the batch never returns to them, so they must not block the pick.
state=$(python3 -I - "$DB" "$SEL" "$LOGS" << 'PY'
import glob, json, os, re, sqlite3, sys
try:
    activity = re.compile(r"^(Batch scheduling finished|\S.* #\d+: (prepare|verify|commit|owner cleanup)|PR \S.* (requeued|held on lane)|Lane \S.* held|Review stopped)")
    rotated = sorted((p for p in glob.glob(os.path.join(sys.argv[3], "review-batch.log-*")) if not p.endswith(".gz")), key=os.path.getmtime)
    last = None
    for path in rotated[-1:] + [os.path.join(sys.argv[3], "review-batch.log")]:
        if os.path.exists(path):
            for line in open(path, errors="replace"):
                if activity.match(line):
                    last = line
    if last is None:
        print("unknown: no scheduling line in the batch log")
        sys.exit()
    if not last.startswith("Batch scheduling finished"):
        print("running")
        sys.exit()
    selection = json.load(open(sys.argv[2]))
    wanted = {(entry["prId"], entry["headSha"]) for queue in selection.get("repositories", {}).values() for entry in queue}
    if not wanted:
        print("finished")
        sys.exit()
    db = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True, timeout=5)
    for (value,) in db.execute("select value from cache where key like 'reviewd/batch-state/%'"):
        entries = json.loads(value).get("entries", [])
        if entries and {(entry["pr"], entry["head"]) for entry in entries} == wanted:
            print("finished")
            sys.exit()
    print("running")
except Exception as error:
    print(f"unknown: {error}")
PY
)
case $state in
    finished) ;;
    running) exit 0 ;;
    *) log "selection state $state; no pick"; exit 0 ;;
esac
if [ -e "$STAMP" ] && [ -n "$(find "$STAMP" -mmin -"$PICK_SPACING_MINUTES")" ]; then
    exit 0
fi
touch "$STAMP"
log "selection finished; pick.sh"
"$OPS/pick.sh" >> "$LOG" 2>&1
log "pick.sh exit $?"
