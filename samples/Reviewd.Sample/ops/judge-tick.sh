#!/usr/bin/env bash
# Hourly judge tick: rebuilds the targets from each install's env.sh, runs `judge batch` once over all of them, keeps
# the STOPPED latch, saves the summary and sends alerts and the daily summary through doctor.py's notify().
# Never installed by deploy.sh (that relinks `cli`, and doctor then restarts every review batch). See the README
# "judge batch" section. Enable the timer only with a JUDGE_CLI copy built from this source, which takes the host-wide
# judge lock: a build that holds only a per-database judge lease must not run a judge on this host while it is on.
#
# $JUDGE_HOME/judge.env: JUDGE_INSTALLS (install dirs, e.g. "reviewd reviewd-viva-pulse"), JUDGE_CLI (pinned
# Reviewd.Sample.dll), JUDGE_EXECUTE (1 = execute, else preview), JUDGE_PROVIDER, JUDGE_EFFORT, JUDGE_MAX_USD,
# JUDGE_MAX_USD_PER_DAY, JUDGE_AVG_USD_TARGET, JUDGE_MAX_AGE_DAYS, JUDGE_LIMIT (optional), JUDGE_SUMMARY_HOUR.
# Stop gracefully: touch $JUDGE_HOME/STOPPED (resume-only: running judges finish, none start). Recover: rm it.
#
# Units in ~/.config/systemd/user/ (systemd never starts a second tick while one runs):
#   reviewd-judge.timer:   [Timer] OnCalendar=hourly RandomizedDelaySec=5min   [Install] WantedBy=timers.target
#   reviewd-judge.service: [Service] Type=oneshot TimeoutStartSec=infinity
#                          ExecStart=%h/.local/share/lmdotnettools-review-daemon/judge/judge-tick.sh
set -uo pipefail
ROOT=${REVIEWD_ROOT:-$HOME/.local/share/lmdotnettools-review-daemon}
JUDGE_HOME=${JUDGE_HOME:-$ROOT/judge}
NOVA=$ROOT/reviewd
STOP=$JUDGE_HOME/STOPPED
PAUSED=$JUDGE_HOME/PAUSED-ALERTED
cd "$JUDGE_HOME" || exit 1

# One HITL Notify through doctor.py; on failure doctor logs it to its alerts log and this returns non-zero.
notify() {
    python3 -I - "${JUDGE_DOCTOR_DIR:-$NOVA/bin}" "$1" "$2" <<'PY'
import sys
sys.path.insert(0, sys.argv[1])
import doctor
sys.exit(0 if doctor.notify(sys.argv[2], sys.argv[3]) else 1)
PY
}

# fit <text>: notify bodies stay under 3500 bytes; a longer text is cut at a line, and says so.
fit() {
    if [ "$(printf %s "$1" | wc -c)" -le 3500 ]; then
        printf '%s' "$1"
    else
        LC_ALL=C awk '(n += length($0) + 1) > 3400 { exit } { print }' <<< "$1"
        printf '%s' "… (cut to fit; the full summary is in summary.md on the host)"
    fi
}

# batch_json <expression>: evaluates a Python expression over the batch's JSON line (as `batch`) and prints the result;
# exits 1 when summary.md has no JSON line (the batch did not finish).
batch_json() {
    tail -n 1 summary.md | python3 -I -c '
import json, sys
try:
    batch = json.loads(sys.stdin.read())
except ValueError:
    sys.exit(1)
print(eval(sys.argv[1]))' "$1"
}

# Every target runs with Nova's credentials, so an install whose env files differ is skipped.
same_credentials() {
    local file
    for file in review-host.env review-daemon.env gateway-app-credentials.env; do
        cmp -s "$NOVA/env/$file" "$1/env/$file" || return 1
    done
}

# A skipped install becomes a "name<TAB>reason" targets line: the batch lists it under Needs you and starts nothing
# new while it is skipped, so a typo in JUDGE_INSTALLS is loud, not silent.
skip() {
    printf '%s\t%s\n' "$1" "$2" >> targets.tsv.new
    echo "skip $1: $2" >&2
}

source "$NOVA/bin/env.sh" >/dev/null || exit 1
source "$JUDGE_HOME/judge.env" || exit 1
: "${JUDGE_CLI:?set JUDGE_CLI in judge.env}" "${JUDGE_INSTALLS:?set JUDGE_INSTALLS in judge.env}"
: > targets.tsv.new
targets=0
for name in $JUDGE_INSTALLS; do
    label=${name#reviewd-}
    [ "$name" = reviewd ] && label=nova
    if [ ! -d "$ROOT/$name" ]; then
        echo "no install at $ROOT/$name" >&2
        skip "$label" "its folder is missing (check JUDGE_INSTALLS)"
    elif ! same_credentials "$ROOT/$name"; then
        skip "$label" "its settings differ from Nova's (other credentials)"
    elif line=$(source "$ROOT/$name/bin/env.sh" >/dev/null && printf '%s\t%s\t%s\t%s' "$label" "$DB" "$SESSION" "$WS"); then
        printf '%s\n' "$line" >> targets.tsv.new
        targets=$((targets + 1))
    else
        skip "$label" "its setup script (bin/env.sh) failed"
    fi
done
mv targets.tsv.new targets.tsv
echo "targets: $targets of $(wc -w <<< "$JUDGE_INSTALLS") installs" >&2

args=(judge batch --config "$CFG" --targets "$JUDGE_HOME/targets.tsv" --stop-file "$STOP"
    --provider "$JUDGE_PROVIDER" --reasoning-effort "$JUDGE_EFFORT" --max-usd "$JUDGE_MAX_USD"
    --max-usd-per-day "$JUDGE_MAX_USD_PER_DAY" --avg-usd-target "$JUDGE_AVG_USD_TARGET"
    --max-age-days "$JUDGE_MAX_AGE_DAYS")
[ -n "${JUDGE_LIMIT:-}" ] && args+=(--limit "$JUDGE_LIMIT")
[ "${JUDGE_EXECUTE:-0}" = 1 ] && args+=(--execute)
cause='' why='' fix=''
if [ -f "$JUDGE_CLI" ]; then
    (cd "$NOVA" && dotnet "$JUDGE_CLI" "${args[@]}") > summary.md.new
    rc=$?
    mv summary.md.new summary.md
    body=$(sed '$d' summary.md)
    [ -n "$body" ] || body="The judge batch printed no summary. On the host: journalctl --user -u reviewd-judge"
else
    # A missing CLI reads in words, not as dotnet's own text.
    cause="Reviewd CLI not found" why="Reviewd CLI not found at $JUDGE_CLI"
    # An rm before the fix only latches again on the next tick.
    fix="fix JUDGE_CLI in $JUDGE_HOME/judge.env"
    printf '%s (JUDGE_CLI in %s)\n' "$why" "$JUDGE_HOME/judge.env" > summary.md
    body=$(cat summary.md)
    rc=1
fi

# Any failure latches resume-only (paused) and alerts once; an ADO change alerts every time. STOPPED says when and why
# in words (the batch status shows it); the alert title names the cause, and the body gives the command that undoes it.
if [ "$rc" != 0 ]; then
    echo "judge batch exit $rc" >&2
    [ -n "$why" ] || why=$(batch_json 'batch["status"]') || why="the judge batch did not finish"
    [ -n "$cause" ] || cause=$(batch_json 'batch.get("cause") or "the judge batch failed"') \
        || cause="the judge batch did not finish"
    why=${why#paused: }
    why=${why%% · *}
    if [ ! -e "$STOP" ]; then
        printf '%s: %s\n' "$(date -u +%FT%RZ)" "$why" > "$STOP"
        notify "Judge paused: $cause" "$(fit "Why: $why
Judges already running still finish. Nothing new starts until you ${fix:+$fix, then }run: rm $STOP

$body")"
    elif [ "$rc" = 3 ]; then
        notify "Judge: a PR changed on ADO while it was judged" "$(fit "$body")"
    fi
fi

# An unusable install (or a store the batch cannot read) stops every new start. Alert whenever an install
# becomes unusable, stops being unusable, or is unusable for a new reason (PAUSED-ALERTED holds the lines
# last alerted, compared in any order). No JSON line (the batch did not finish), or one with no unusable list (busy:
# another judge held the lock, so the batch scanned nothing), leaves the state as it was. Like the paused alert, the
# alert that none is left changes PAUSED-ALERTED only once it is sent, so a failed send is tried again next tick.
names() { sed 's/^- \([^:]*\): .*/\1/' | sort; }
if unusable=$(batch_json '("\n".join("- " + item["name"] + ": " + item["cause"] for item in batch["unusable"] or [])
    if "unusable" in batch else sys.exit(1))'); then
    fixed=$(comm -13 <(names <<< "$unusable") <(names 2>/dev/null < "$PAUSED") | paste -sd , - | sed 's/,/, /g')
    if [ -z "$unusable" ]; then
        if [ -s "$PAUSED" ]; then
            # While STOPPED exists nothing new starts anyway, so the alert says that instead of "resumed".
            title="Judge resumed" next="New judges can start again."
            [ -e "$STOP" ] && title="Judge: no install unusable" next="Nothing new starts until you run: rm $STOP"
            notify "$title" "$(fit "Fixed or removed since the last alert: $fixed.
No install is unusable now. $next")" && rm -f "$PAUSED"
        fi
    elif [ "$(sort <<< "$unusable")" != "$(sort 2>/dev/null < "$PAUSED")" ]; then
        count=$(grep -c . <<< "$unusable")
        [ "$count" = 1 ] && installs="1 install" || installs="$count installs"
        # An install alerted before whose line changed is unusable for a new reason.
        changed=$(comm -12 <(comm -23 <(sort <<< "$unusable") <(sort 2>/dev/null < "$PAUSED") | names) \
            <(names 2>/dev/null < "$PAUSED") | paste -sd , - | sed 's/,/, /g')
        notify "Judge paused: $installs unusable" "$(fit "${fixed:+Fixed or removed since the last alert: $fixed.
}${changed:+New reason for: $changed.
}No new judges start until each install below is fixed or removed from JUDGE_INSTALLS. Judges already running still finish.
$unusable")" && printf '%s\n' "$unusable" > "$PAUSED"
    fi
fi

today=$(date +%F)
if [ "$(date +%H)" -ge "${JUDGE_SUMMARY_HOUR:-8}" ] && [ ! -e "summary-$today.md" ]; then
    cp summary.md "summary-$today.md"
    notify "Judge daily $today" "$(fit "$body")" || rm -f "summary-$today.md"
fi
exit "$rc"
