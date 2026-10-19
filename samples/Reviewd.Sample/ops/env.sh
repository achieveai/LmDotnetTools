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
# Where the state lived before it moved to REVIEWD_HOME (RUNBOOK "Move over from the old worktree").
: "${REVIEWD_LEGACY_RUN_DIR:=$HOME/repos/LmDotnetTools/.claude/worktrees/reviewd-sample/.run/reviewd-local}"

# PIDs of this install's review batch: a `Reviewd.Sample.dll review batch` whose --db resolves to $DB.
# A batch of another install never matches, so nothing here can signal it.
install_batch_pids() {
    python3 -I - "$DB" <<'PY'
import os, sys
db = os.path.realpath(sys.argv[1])
for pid in filter(str.isdigit, os.listdir("/proc")):
    try:
        argv = open(f"/proc/{pid}/cmdline", "rb").read().decode(errors="replace").split("\0")
        cwd = os.readlink(f"/proc/{pid}/cwd")
    except OSError:
        continue
    for i, token in enumerate(argv[:-2]):
        if token.endswith("Reviewd.Sample.dll") and argv[i + 1 : i + 3] == ["review", "batch"]:
            dbs = [argv[j + 1] for j in range(i + 3, len(argv) - 1) if argv[j] == "--db"]
            if any(os.path.realpath(os.path.join(cwd, d)) == db for d in dbs):
                print(pid)
            break
PY
}

# Fails when the old worktree still holds review state and $DB has none: starting on the empty root
# would orphan the old slot claims and host history. Migrate first, or set
# REVIEWD_LEGACY_STATE=abandoned in batch.env to start fresh on purpose.
refuse_unmigrated_state() {
    [ "${REVIEWD_LEGACY_STATE:-}" = abandoned ] && return 0
    [ -e "$REVIEWD_LEGACY_RUN_DIR/reviews.db" ] || return 0
    local rows
    rows=$(python3 -I - "$DB" 2>/dev/null <<'PY'
import sqlite3, sys
db = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
print(db.execute("select count(*) from cache where key like 'reviewd/%'").fetchone()[0])
PY
    )
    # Only a readable database with review rows passes. Missing, tableless or unreadable fails closed.
    [ "${rows:-0}" -gt 0 ] 2>/dev/null && return 0
    echo "Old review state exists in $REVIEWD_LEGACY_RUN_DIR but $DB is empty or unreadable." >&2
    echo "Move it over first (RUNBOOK: Move over from the old worktree), or set" >&2
    echo "REVIEWD_LEGACY_STATE=abandoned in $REVIEWD_HOME/env/batch.env to start fresh." >&2
    return 1
}
