#!/usr/bin/env bash
# Runs the review batch over the current selection (RUNBOOK step 6), in the foreground.
# Logs to $REVIEWD_HOME/logs/review-batch.log. To run it in the background:
#   nohup setsid <ops dir>/start-batch.sh >/dev/null 2>&1 < /dev/null &
set -uo pipefail
source "$(dirname "$(readlink -f "$0")")/env.sh"
if [ -n "$(install_batch_pids)" ]; then
    echo "a batch is already running on $DB"
    exit 1
fi
refuse_unmigrated_state || exit 1
cd "$REVIEWD_HOME"
echo "[$(date -Is)] batch start" >> "$LOGS/review-batch.log"
exec $R review batch --selection "$SEL" --db "$DB" --session "$SESSION" --workspace "$WS" \
    --provider "$PROVIDER" --mode code-review-daemon --reasoning-effort "$EFFORT" \
    --max-age-hours "$MAX_AGE_HOURS" --listen "${BATCH_LISTEN:-http://127.0.0.1:5235}" --config "$CFG" --execute \
    >> "$LOGS/review-batch.log" 2>&1
