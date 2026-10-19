#!/usr/bin/env bash
# Starts the review host on :5000 in the background from $REVIEWD_HOME/host. Never prints keys.
# The host keeps logs, workflow-index, chat-modes, workspaces and appsettings*.json beside its
# binaries; deploy.sh links those to $REVIEWD_HOME/data/host, so a new build keeps the data.
set -euo pipefail
source "$(dirname "$(readlink -f "$0")")/env.sh"
if pgrep -f '^dotnet .*LmStreaming.Sample.dll --urls http://0.0.0.0:5000' >/dev/null; then
    echo "a host is already running"
    exit 1
fi
refuse_unmigrated_state || exit 1
H=$(readlink -f "$REVIEWD_HOME/host")
D=$REVIEWD_HOME/data/host
export DOTNET_hostBuilder__reloadConfigOnChange=false
export SandboxGateway__AppKey="$DAEMON_GATEWAY_APP_KEY"
# The agency MCP bridge (agency.internal:8765) needs `Authorization: Bearer <token>`. The token lives
# only in agency-bridge.env. A sandbox gets this rule when it is created; a running one keeps its policy.
if [ -s "$REVIEWD_HOME/env/agency-bridge.env" ]; then
    source "$REVIEWD_HOME/env/agency-bridge.env"
    export SandboxGateway__Network__Rules__agency_proxy__Enabled=true
    export SandboxGateway__AuthProviders__agency_proxy__Enabled=true
    export SandboxGateway__AuthProviders__agency_proxy__Headers__Authorization__Value="Bearer $AGENCY_BRIDGE_TOKEN"
fi
cd "$D"
echo "[$(date -Is)] start host from $H" >> "$LOGS/host-console.log"
nohup setsid dotnet "$H/LmStreaming.Sample.dll" \
    --urls http://0.0.0.0:5000 --contentRoot "$H" --webroot "$H/wwwroot" \
    --LmStreaming:ConversationDbPath "$D/conversations.db" \
    --Identity:DatabasePath "$D/identity.db" --LmStreaming:NotifyWaitDbPath "$D/notify-waits.db" \
    --SandboxGateway:BaseUrl http://127.0.0.1:3000 --SandboxGateway:AppId codereview-daemon-nova \
    --SandboxGateway:AutoSpawn false --Auth:Webhook:PublicBaseUrl http://127.0.0.1:5000 \
    >> "$LOGS/host-console.log" 2>&1 < /dev/null &
echo "started host pid $! (log $LOGS/host-console.log)"
