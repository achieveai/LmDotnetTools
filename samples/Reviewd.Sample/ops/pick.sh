#!/usr/bin/env bash
# Finds PRs and rewrites the selection (RUNBOOK step 5, about 8 minutes). The previous selection is
# kept in data/selections/. Pick for the same provider the batch runs.
set -uo pipefail
source "$(dirname "$(readlink -f "$0")")/env.sh"
cd "$REVIEWD_HOME"
mkdir -p data/selections
echo "[$(date -Is)] pr check"
$R pr check --repos "$REPOS" --db "$DB" --config "$CFG" > "$LOGS/pr-check.jsonl" 2> "$LOGS/pr-check.err"
echo "check exit=$?"
grep -i "not saved" "$LOGS/pr-check.err" | head
if [ -f "$SEL" ]; then
    cp -p "$SEL" "data/selections/selection-$(date +%Y%m%d-%H%M%S).json"
fi
echo "[$(date -Is)] pr pick"
$R pr pick --repos "$REPOS" --db "$DB" --max-age-hours "$MAX_AGE_HOURS" --selection-out "$SEL" 2>&1 >/dev/null | tail -3
echo "pick exit=${PIPESTATUS[0]}"
echo "[$(date -Is)] done"
