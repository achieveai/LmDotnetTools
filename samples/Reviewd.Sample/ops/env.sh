# Source this in every terminal and from every ops script: `source <ops dir>/env.sh`.
# Everything the reviews need lives under REVIEWD_HOME, outside any git checkout, so cleaning a
# worktree or rebooting loses nothing. Keys come from owner-only files: source them, never print them.
: "${REVIEWD_HOME:=$HOME/.local/share/lmdotnettools-review-daemon/reviewd}"
set -a
source "$REVIEWD_HOME/env/review-host.env"
source "$REVIEWD_HOME/env/review-daemon.env"
source "$REVIEWD_HOME/env/gateway-app-credentials.env"
# SESSION, WS, PROVIDER, EFFORT, MAX_AGE_HOURS and REPOS for this install. See RUNBOOK.md.
source "$REVIEWD_HOME/env/batch.env"
set +a
export REVIEWD_HOME
export ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production
export Auth__TokenStoreDir="$REVIEWD_HOME/oauth-tokens"
R="dotnet $REVIEWD_HOME/cli/Reviewd.Sample.dll"
CFG=$REVIEWD_HOME/env/appsettings.nova.json
DB=$REVIEWD_HOME/data/reviews.db
SEL=$REVIEWD_HOME/data/selection.json
LOGS=$REVIEWD_HOME/logs
