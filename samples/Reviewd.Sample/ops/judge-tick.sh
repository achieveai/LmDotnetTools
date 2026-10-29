#!/usr/bin/env bash
# Hourly judge tick: rebuilds the targets from each install's env.sh, runs `judge batch` once over all of them, keeps
# the STOPPED latch, closes each newly judged PR (JUDGE_CLOSE=1), saves the summary and sends alerts and the daily
# summary through doctor.py's notify().
# Never installed by deploy.sh (that relinks `cli`, and doctor then restarts every review batch). See the README
# "judge batch" section. Enable the timer only with a JUDGE_CLI copy built from this source, which takes the host-wide
# judge lock: a build that holds only a per-database judge lease must not run a judge on this host while it is on.
#
# $JUDGE_HOME/judge.env: JUDGE_INSTALLS (install dirs, e.g. "reviewd reviewd-viva-pulse"), JUDGE_CLI (pinned
# Reviewd.Sample.dll), JUDGE_EXECUTE (1 = execute, else preview), JUDGE_PROVIDER, JUDGE_EFFORT, JUDGE_MAX_USD,
# JUDGE_MAX_USD_PER_DAY, JUDGE_AVG_USD_TARGET, JUDGE_MAX_AGE_DAYS, JUDGE_LIMIT (optional), JUDGE_SUMMARY_HOUR;
# for a judge lane (one of several ticks judging side by side, each over its own installs):
# JUDGE_LANE (a name for its alerts) and JUDGE_LOCK (its own judge lock file; unset: the host-wide judge.lock); to
# close: JUDGE_CLOSE (1 = close), JUDGE_STORES (the folder holding each install's store, <label>-reviews) and
# JUDGE_CLOSE_LIMIT (closes per tick, default 30).
# Stop gracefully: touch $JUDGE_HOME/STOPPED (resume-only: running judges finish, none start, nothing closes).
# Recover: rm it.
#
# Units in ~/.config/systemd/user/ (systemd never starts a second tick while one runs):
#   reviewd-judge.timer:   [Timer] OnCalendar=hourly RandomizedDelaySec=5min   [Install] WantedBy=timers.target
#   reviewd-judge.service: [Service] Type=oneshot TimeoutStartSec=infinity
#                          ExecStart=%h/.local/share/lmdotnettools-review-daemon/judge/judge-tick.sh
# Judge lanes use template units instead, one instance per lane (JUDGE_HOME judge-<lane>, whose judge.env sets JUDGE_LOCK
# and installs no other lane has):
#   reviewd-judge@.timer:   [Timer] OnActiveSec=1min OnUnitInactiveSec=5min   [Install] WantedBy=timers.target
#   reviewd-judge@.service: [Service] Type=oneshot TimeoutStartSec=infinity
#                           Environment=JUDGE_HOME=%h/.local/share/lmdotnettools-review-daemon/judge-%i
#                           ExecStart=%h/.local/share/lmdotnettools-review-daemon/judge/judge-tick.sh
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
[ "${JUDGE_CLOSE:-0}" = 1 ] && : "${JUDGE_STORES:?set JUDGE_STORES in judge.env when JUDGE_CLOSE=1}"
[ -n "${JUDGE_LOCK:-}" ] && export REVIEWD_JUDGE_LOCK=$JUDGE_LOCK
J="Judge${JUDGE_LANE:+ $JUDGE_LANE}"
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
    [ -n "$body" ] || body="The judge batch printed no summary. On the host: journalctl --user -u 'reviewd-judge*'"
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
        notify "$J paused: $cause" "$(fit "Why: $why
Judges already running still finish. Nothing new starts until you ${fix:+$fix, then }run: rm $STOP

$body")"
    elif [ "$rc" = 3 ]; then
        notify "$J: a PR changed on ADO while it was judged" "$(fit "$body")"
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
            title="$J resumed" next="New judges can start again."
            [ -e "$STOP" ] && title="$J: no install unusable" next="Nothing new starts until you run: rm $STOP"
            notify "$title" "$(fit "Fixed or removed since the last alert: $fixed.
No install is unusable now. $next")" && rm -f "$PAUSED"
        fi
    elif [ "$(sort <<< "$unusable")" != "$(sort 2>/dev/null < "$PAUSED")" ]; then
        count=$(grep -c . <<< "$unusable")
        [ "$count" = 1 ] && installs="1 install" || installs="$count installs"
        # An install alerted before whose line changed is unusable for a new reason.
        changed=$(comm -12 <(comm -23 <(sort <<< "$unusable") <(sort 2>/dev/null < "$PAUSED") | names) \
            <(names 2>/dev/null < "$PAUSED") | paste -sd , - | sed 's/,/, /g')
        notify "$J paused: $installs unusable" "$(fit "${fixed:+Fixed or removed since the last alert: $fixed.
}${changed:+New reason for: $changed.
}No new judges start until each install below is fixed or removed from JUDGE_INSTALLS. Judges already running still finish.
$unusable")" && printf '%s\n' "$unusable" > "$PAUSED"
    fi
fi

# Close: for each PR whose newest judgment is a completed final one, judge close --execute files it on the store's main
# and deletes its review branches. closed.tsv (label, repository, PR, judgment, when) lists what closed. A close that
# fails, except "main kept moving" (exit 6, tried again next tick), goes to close-failed.tsv with its reason and alerts
# once; it is not tried again until its line is removed. close-alerted.tsv holds the failures already alerted, so a
# failed send is tried again next tick without running the close again. Nothing closes while STOPPED exists, including a
# STOPPED made while a close runs: the rest wait.
# pending: one "label<TAB>db<TAB>repository<TAB>PR<TAB>judgment" line per PR to close, read-only from each database.
pending() {
    python3 -I - targets.tsv closed.tsv close-failed.tsv <<'PY'
import json, sqlite3, sys, urllib.request
done = {tuple(line.rstrip("\n").split("\t")[:4]) for name in sys.argv[2:] for line in open(name) if line.strip()}
for line in open(sys.argv[1]):
    target = line.rstrip("\n").split("\t")
    if len(target) != 4:
        continue
    label, db = target[:2]
    try:
        rows = sqlite3.connect(f"file:{urllib.request.pathname2url(db)}?mode=ro", uri=True).execute(
            "SELECT key, value FROM cache WHERE substr(key, 1, 18) = 'reviewd/judge-run/'").fetchall()
    except sqlite3.Error as failure:
        print(f"close: cannot read the {label} database: {failure}", file=sys.stderr)
        continue
    newest = {}
    for key, value in rows:
        pr, _, n = key.rpartition("/")
        if n.isdigit() and int(n) > newest.get(pr, (0, ""))[0]:
            newest[pr] = (int(n), value)
    for n, value in (newest[pr] for pr in sorted(newest)):
        try:
            receipt = json.loads(value)
        except ValueError:
            continue
        if receipt.get("stage") == "completed" and receipt.get("outputStage") == "final":
            item = (label, str(receipt.get("repository")), str(receipt.get("prId")), str(n))
            if item not in done:
                print("\t".join((label, db) + item[1:]))
PY
}
closes=''
if [ "${JUDGE_CLOSE:-0}" = 1 ]; then
    touch closed.tsv close-failed.tsv close-alerted.tsv
    # A line removed from close-failed.tsv (to try that close again) leaves close-alerted.tsv before the closes run, so
    # a new failure of that close alerts again.
    grep -x -F -f close-failed.tsv close-alerted.tsv > close-alerted.tsv.new
    mv close-alerted.tsv.new close-alerted.tsv
fi
if [ "${JUDGE_CLOSE:-0}" = 1 ] && [ ! -e "$STOP" ] && [ -f "$JUDGE_CLI" ]; then
    closed=0 retry=0 failures=0 left=0
    while IFS=$'\t' read -r label db repo pr n; do
        if [ -e "$STOP" ] || [ $((closed + retry + failures)) -ge "${JUDGE_CLOSE_LIMIT:-30}" ]; then
            left=$((left + 1))
            continue
        fi
        (cd "$NOVA" && timeout 1800 dotnet "$JUDGE_CLI" judge close --config "$CFG" --repo "$repo" --pr "$pr" --db "$db" \
            --store "$JUDGE_STORES/$label-reviews" --judgment "$n" --execute) > close.out 2> close.err < /dev/null
        crc=$?
        cat close.err >&2
        if [ "$crc" = 0 ]; then
            printf '%s\t%s\t%s\t%s\t%s\n' "$label" "$repo" "$pr" "$n" "$(date -u +%FT%RZ)" >> closed.tsv
            closed=$((closed + 1))
        elif [ "$crc" = 6 ]; then
            retry=$((retry + 1))
        else
            # The reason: close's last stderr line (its refusal or failure), never a commit body.
            why=$(grep . close.err | tail -n 1 | cut -c1-200)
            printf '%s\t%s\t%s\t%s\t%s\n' "$label" "$repo" "$pr" "$n" "exit $crc: ${why:-no error output}" >> close-failed.tsv
            failures=$((failures + 1))
        fi
    done < <(pending)
    rm -f close.out close.err
    since=$(date -u -d '24 hours ago' +%FT%RZ)
    closes="Closed in the last 24 h: $(awk -F'\t' -v since="$since" '$5 >= since' closed.tsv | grep -c .) PRs ($(grep -c . closed.tsv) in all). This tick: $closed closed, $retry to retry (main kept moving), $left waiting for the next tick. Close failures waiting for you: $(grep -c . close-failed.tsv) (close-failed.tsv)."
    echo "$closes" >&2
fi
if [ "${JUDGE_CLOSE:-0}" = 1 ]; then
    unalerted=$(grep -v -x -F -f close-alerted.tsv close-failed.tsv)
    if [ -n "$unalerted" ]; then
        notify "$J: $(grep -c . <<< "$unalerted") close(s) failed" "$(fit "Each is skipped until you look at it. To try one again, remove its line from $JUDGE_HOME/close-failed.tsv.
$(awk -F'\t' '{ print "- " $2 " !" $3 " (judgment " $4 "): " $5 }' <<< "$unalerted")")" && cp close-failed.tsv close-alerted.tsv
    fi
fi

today=$(date +%F)
if [ "$(date +%H)" -ge "${JUDGE_SUMMARY_HOUR:-8}" ] && [ ! -e "summary-$today.md" ]; then
    cp summary.md "summary-$today.md"
    notify "$J daily $today" "$(fit "$body${closes:+

$closes}")" || rm -f "summary-$today.md"
fi
exit "$rc"
