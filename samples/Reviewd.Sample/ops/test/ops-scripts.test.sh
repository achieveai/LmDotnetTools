#!/usr/bin/env bash
# Checks the ops scripts against fake installs on Linux (or WSL): a restart signals only its own
# install's batch, never leaves it stopped, fails closed when two batches share one database, and a
# start refuses an empty root while the old worktree still holds state. Needs python3 and setsid.
#   ops/test/ops-scripts.test.sh
set -uo pipefail
OPS=$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)
T=$(mktemp -d)
trap 'pkill -KILL -f "$T/fakebin/dotnet" 2>/dev/null; rm -rf "$T"' EXIT
failures=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; failures=$((failures + 1)); fi; }

# A dotnet stand-in that runs until killed and records every SIGCONT it gets beside its --db.
mkdir -p "$T/fakebin"
PYTHON=$(command -v python3)
{ echo "#!$PYTHON"; cat; } > "$T/fakebin/dotnet" <<'PY'
import os, signal, sys, time
db = sys.argv[sys.argv.index("--db") + 1]
log = os.path.join(os.path.dirname(os.path.abspath(db)), "signals.log")
signal.signal(signal.SIGCONT, lambda *_: open(log, "a").write(f"{os.getpid()} CONT\n"))
while True:
    time.sleep(1)
PY
# A python3 in front of the real one: when the restart checks attempts, it first records the state
# of the batch on that database (T = stopped) beside the database.
{ echo "#!/usr/bin/env bash"; echo "PYTHON=$PYTHON"; cat; } > "$T/fakebin/python3" <<'SH'
script=$(cat)
if [[ $script == *'"review-running", "confirmation-running"'* ]]; then
    db=${!#}
    for pid in $(pgrep -f "[f]akebin/dotnet .*--db $db"); do
        ps -o state= -p "$pid" >> "$(dirname "$db")/states.log"
    done
fi
exec "$PYTHON" "$@" <<<"$script"
SH
chmod +x "$T/fakebin/dotnet" "$T/fakebin/python3"
export PATH="$T/fakebin:$PATH" REVIEWD_LEGACY_RUN_DIR="$T/no-legacy"
export RESTART_POLLS=3 RESTART_POLL_SECONDS=0.3 RESTART_START_SECONDS=1

install() {
    mkdir -p "$T/$1/env" "$T/$1/cli" "$T/$1/data" "$T/$1/logs"
    touch "$T/$1/env/review-host.env" "$T/$1/env/review-daemon.env" "$T/$1/env/gateway-app-credentials.env"
    touch "$T/$1/cli/Reviewd.Sample.dll"
    printf 'SESSION=s\nWS=w\nPROVIDER=p\nEFFORT=low\nMAX_AGE_HOURS=1\n' > "$T/$1/env/batch.env"
}
# seed <db> <cleanupState> <stage>: one attempt row in a review database.
seed() {
    python3 -I - "$@" <<'PY'
import json, sqlite3, sys
db = sqlite3.connect(sys.argv[1])
db.execute("create table if not exists cache (key text primary key, value text)")
attempt = {"cleanupState": sys.argv[2], "stage": sys.argv[3]}
db.execute("insert or replace into cache values ('reviewd/review-attempt/x/1', ?)", (json.dumps(attempt),))
db.commit()
PY
}
pids() { REVIEWD_HOME="$T/$1" bash -c 'source "$0/env.sh"; install_batch_pids' "$OPS"; }
start() {
    REVIEWD_HOME="$T/$1" setsid -f "$OPS/start-batch.sh" > /dev/null 2>&1 < /dev/null
    for _ in $(seq 1 50); do [ -n "$(pids "$1")" ] && return; sleep 0.1; done
}
restart() { REVIEWD_HOME="$T/$1" "$OPS/safe-restart-batch.sh" touch "$T/$1/command-ran" > "$T/$1/restart.out" 2>&1; }
alive_not_stopped() { [ -n "$1" ] && [ -d "/proc/$1" ] && ! grep -q '^T' <<<"$(ps -o state= -p "$1")"; }

install A
install B
seed "$T/A/data/reviews.db" owned review-send-pending
seed "$T/B/data/reviews.db" owned review-send-pending
start A
start B
a=$(pids A) b=$(pids B)
check "each install sees only its own batch" '[ -n "$a" ] && [ -n "$b" ] && [ "$a" != "$b" ]'

restart A
code=$?
check "a non-resumable batch is not killed" '[ $code -eq 1 ] && [ "$(pids A)" = "$a" ]'
check "the batch is left running, not stopped" 'alive_not_stopped "$a"'
check "every check ran on a stopped batch, then thawed it" \
    '[ "$(sort -u "$T/A/data/states.log")" = T ] && [ "$(wc -l < "$T/A/data/states.log")" -eq 3 ] \
        && [ "$(grep -c CONT "$T/A/data/signals.log")" -eq 3 ]'
check "the command does not run" '[ ! -e "$T/A/command-ran" ]'
check "the other install is never signalled" 'alive_not_stopped "$b" && [ ! -e "$T/B/data/signals.log" ]'

rm -f "$T/A/data/signals.log"
(cd "$T/A" && setsid -f dotnet "$T/A/cli/Reviewd.Sample.dll" review batch --db data/reviews.db > /dev/null 2>&1 < /dev/null)
for _ in $(seq 1 50); do [ "$(pids A | wc -l)" -eq 2 ] && break; sleep 0.1; done
extra=$(pids A | grep -vx "$a")
check "a relative --db resolves to the same install" '[ -n "$extra" ]'
seed "$T/A/data/reviews.db" released review-completed
restart A
code=$?
check "two batches on one database fail closed" '[ $code -eq 1 ] && grep -q "more than one batch" "$T/A/restart.out"'
check "neither is signalled" 'alive_not_stopped "$a" && alive_not_stopped "$extra" && [ ! -e "$T/A/data/signals.log" ]'
kill -KILL "$extra"
for _ in $(seq 1 50); do [ "$(pids A)" = "$a" ] && break; sleep 0.1; done

restart A
code=$?
new=$(pids A)
check "a resumable batch is killed, the command runs and the batch restarts" \
    '[ $code -eq 0 ] && [ -e "$T/A/command-ran" ] && [ ! -d "/proc/$a" ] && [ -n "$new" ] && [ "$new" != "$a" ]'
check "the other install keeps running untouched" '[ "$(pids B)" = "$b" ] && alive_not_stopped "$b" && [ ! -e "$T/B/data/signals.log" ]'

install C
mkdir -p "$T/legacy"
seed "$T/legacy/reviews.db" owned review-running
export REVIEWD_LEGACY_RUN_DIR="$T/legacy"
refused() {
    REVIEWD_HOME="$T/C" timeout 10 "$OPS/start-batch.sh" > "$T/C/start.out" 2>&1
    [ $? -eq 1 ] && grep -q "Old review state exists" "$T/C/start.out" && [ -z "$(pids C)" ]
}
check "a start on an empty root refuses while old state exists" refused
echo "not a database" > "$T/C/data/reviews.db"
check "a start on an unreadable database refuses too" refused
rm "$T/C/data/reviews.db"
REVIEWD_HOME="$T/C" "$OPS/start-host.sh" > "$T/C/host.out" 2>&1
code=$?
check "the host refuses too" '[ $code -eq 1 ] && grep -q "Old review state exists" "$T/C/host.out"'
REVIEWD_LEGACY_STATE=abandoned start C
check "an explicit acknowledgement starts fresh" '[ -n "$(pids C)" ]'
pkill -KILL -f "$T/C/cli" 2>/dev/null
for _ in $(seq 1 50); do [ -z "$(pids C)" ] && break; sleep 0.1; done
seed "$T/C/data/reviews.db" owned review-running
start C
check "a migrated root starts without acknowledgement" '[ -n "$(pids C)" ]'

echo "$failures failure(s)"
[ "$failures" -eq 0 ]
