#!/usr/bin/env bash
# Deploys a published Reviewd CLI and moves every batch on this machine onto it, one install at a time, through each
# install's own safe-restart-batch.sh (it stops a batch only at a moment every attempt can resume). An install with no
# safe moment yet is left running and retried, up to 4 rounds 15 minutes apart; the doctor keeps flagging any batch
# still on an older release after that. Every store's cli links to this install's cli, so one deploy covers them all.
#   rollout.sh <publish dir> <release name, e.g. 3d0b4224-reapself>
# Rollback: ln -sfn releases/cli/<older name> "$REVIEWD_HOME/cli", then run rollout.sh again with the older name.
set -uo pipefail
OPS=$(dirname "$(readlink -f "$0")")
source "$OPS/env.sh"
publish=${1:?publish dir} release=${2:?release name}
ROOT=$(dirname "$REVIEWD_HOME")
LOG=$LOGS/rollout.log
log() { echo "[$(date -Is)] $*" | tee -a "$LOG"; }
on_release() {
    local pid
    pid=$( (source "$1/bin/env.sh" >/dev/null 2>&1; install_batch_pids) | head -1)
    [ -n "$pid" ] && grep -q "releases/cli/$release/" "/proc/$pid/maps" 2>/dev/null
}
if [ "$(basename "$(readlink "$REVIEWD_HOME/cli")")" != "$release" ]; then
    "$OPS/deploy.sh" cli "$publish" "$release" >> "$LOG" 2>&1 || { log "deploy of $release failed"; exit 1; }
fi
log "rollout $release: cli -> $(readlink "$REVIEWD_HOME/cli")"
for round in 1 2 3 4; do
    pending=0
    for d in "$ROOT"/reviewd-* "$ROOT"/reviewd; do
        [ -f "$d/bin/env.sh" ] || continue
        on_release "$d" && continue
        log "${d##*/}: $("$d/bin/safe-restart-batch.sh" 2>&1 | tr '\n' ' ')"
        sleep 10
        on_release "$d" || pending=$((pending + 1))
    done
    if [ "$pending" = 0 ]; then
        log "rollout $release done: every batch runs it"
        exit 0
    fi
    log "rollout $release round $round: $pending install(s) not on it yet"
    [ "$round" -lt 4 ] && sleep "${ROLLOUT_RETRY_SECONDS:-900}"
done
log "rollout $release: $pending install(s) still on an older release"
exit 1
