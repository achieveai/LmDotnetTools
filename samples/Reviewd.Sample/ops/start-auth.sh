#!/usr/bin/env bash
# Starts the OAuth webhook (`auth serve`) on 172.17.0.1:5206 in the background. Without it the
# sandbox cannot reach Azure DevOps (`502 policy_evaluation_failed`).
set -euo pipefail
source "$(dirname "$(readlink -f "$0")")/env.sh"
if pgrep -f '^dotnet .*Reviewd.Sample.dll auth serve' >/dev/null; then
    echo "auth serve is already running"
    exit 1
fi
cd "$REVIEWD_HOME"
echo "[$(date -Is)] start auth serve" >> "$LOGS/auth-serve.log"
nohup setsid $R auth serve --listen http://172.17.0.1:5206 --callback http://host.docker.internal:5206 \
    --config "$CFG" >> "$LOGS/auth-serve.log" 2>&1 < /dev/null &
echo "started auth serve pid $! (log $LOGS/auth-serve.log)"
