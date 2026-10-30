#!/usr/bin/env bash
# Checks ops/judge-tick.sh in a throwaway root with a stand-in batch (dotnet) and a stand-in doctor.notify: unusable
# installs reach the batch as targets lines, a failure latches STOPPED and alerts once with its cause, alert bodies are
# cut at a line, the daily summary goes out once, a missing CLI reads in words, the last unusable install fixed alerts
# once, a lane passes its own judge lock and names itself, closes run once per newly judged PR, a STOPPED made during a
# close stops the rest, a close alert that was not sent is sent next tick, and a shard lane passes --shard and closes
# only its shard's PRs. Nothing outside the temp dir is read or written. Needs python3.
#   ops/test/judge-tick.test.sh
set -uo pipefail
OPS=$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
failures=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; failures=$((failures + 1)); fi; }

R=$T/root
# install <name> <credential text>
install() {
    mkdir -p "$R/$1/bin" "$R/$1/env"
    for file in review-host.env review-daemon.env gateway-app-credentials.env; do echo "$2" > "$R/$1/env/$file"; done
    printf 'DB=%s/%s.db SESSION=s-%s WS=w-%s CFG=%s/cfg.json\n' "$R" "$1" "$1" "$1" "$R" > "$R/$1/bin/env.sh"
}
install reviewd same
install reviewd-other different
install reviewd-broken same
echo 'return 1' > "$R/reviewd-broken/bin/env.sh"
mkdir -p "$R/judge" "$T/bin" "$T/doctor"
cat > "$R/judge/judge.env" <<EOF
JUDGE_INSTALLS="reviewd reviewd-other reviewd-broken reviewd-typo"
JUDGE_CLI=$T/judge.dll JUDGE_PROVIDER=p JUDGE_EFFORT=xhigh JUDGE_MAX_USD=15 JUDGE_MAX_USD_PER_DAY=30
JUDGE_AVG_USD_TARGET=4 JUDGE_MAX_AGE_DAYS=7 JUDGE_SUMMARY_HOUR=0
EOF
touch "$T/judge.dll"
# The batch stand-in writes its arguments to $ARGS_LOG, one per line, prints a heading and $FAKE_LINES lines of 79 bytes,
# then its JSON line (status $FAKE_STATUS, cause $FAKE_CAUSE), and exits $FAKE_RC. Like the batch, its JSON line lists
# each name<TAB>cause line of the targets file under "unusable", except when busy ($FAKE_BUSY: another judge held the
# lock, nothing was scanned). Like dotnet, a missing DLL prints dotnet's own text and exits 1. A `judge close` appends
# its arguments to $CLOSE_LOG as one line and exits 1 for a PR in $FAKE_CLOSE_REFUSE, 6 for one in $FAKE_CLOSE_MOVED,
# else 0; for a PR in $FAKE_CLOSE_STOPS it first makes the STOPPED latch, as a batch or a person can while it runs.
cat > "$T/bin/dotnet" <<'SH'
#!/usr/bin/env bash
if [ "$2 $3" = "judge close" ]; then
    printf '%s\n' "$*" >> "$CLOSE_LOG"
    pr=$(printf '%s\n' "$@" | grep -A1 -x -- --pr | sed -n 2p)
    case " ${FAKE_CLOSE_REFUSE:-} " in *" $pr "*) echo "the packet changed since the judge" >&2; exit 1 ;; esac
    case " ${FAKE_CLOSE_MOVED:-} " in *" $pr "*) exit 6 ;; esac
    case " ${FAKE_CLOSE_STOPS:-} " in *" $pr "*) touch "$REVIEWD_ROOT/judge/STOPPED" ;; esac
    exit 0
fi
printf '%s\n' "$@" "lock=${REVIEWD_JUDGE_LOCK:-}" > "$ARGS_LOG"
if [ ! -f "$1" ]; then
    echo "Could not execute because the specified command or file was not found."
    echo "Possible reasons for this include:"
    exit 1
fi
while [ "$1" != --targets ]; do shift; done
echo "# Judge batch"
for i in $(seq 1 "${FAKE_LINES:-3}"); do printf -- '- line %03d %067d\n' "$i" 0; done
python3 -I -c '
import json, sys
unusable = [{"name": n, "cause": c} for n, c in (l.rstrip("\n").split("\t") for l in open(sys.argv[1])
            if l.count("\t") == 1)]
batch = {"schema": 1, "status": sys.argv[2], "cause": sys.argv[3] or None}
print(json.dumps(batch if sys.argv[4] else {**batch, "unusable": unusable}))' \
    "$2" "${FAKE_STATUS:-ok}" "${FAKE_CAUSE:-}" "${FAKE_BUSY:-}"
exit "${FAKE_RC:-0}"
SH
chmod +x "$T/bin/dotnet"
# The doctor stand-in logs each alert to $NOTIFY_LOG; with $NOTIFY_FAIL set it sends nothing and returns False.
cat > "$T/doctor/doctor.py" <<'PY'
import os
def notify(title, body):
    if os.environ.get("NOTIFY_FAIL"):
        return False
    with open(os.environ["NOTIFY_LOG"], "a") as log:
        log.write(f"=== {title} | {len(body.encode())}\n{body}\n")
    return True
PY
: > "$T/notify.log"
# tick [VAR=value ...]: one tick; its stderr goes to tick.err.
tick() {
    env -i HOME="$T" PATH="$T/bin:/usr/bin:/bin" REVIEWD_ROOT="$R" JUDGE_DOCTOR_DIR="$T/doctor" \
        NOTIFY_LOG="$T/notify.log" ARGS_LOG="$T/args" CLOSE_LOG="$T/closes" "$@" bash "$OPS/judge-tick.sh" 2> "$T/tick.err"
}
alerts() { grep -c "^=== $1" "$T/notify.log"; }
# arg <flag>: the value the last tick passed the batch after <flag>.
arg() { grep -A1 -x -- "$1" "$T/args" | sed -n 2p; }

tick FAKE_RC=0
code=$?
check "a clean tick exits 0" '[ $code -eq 0 ] && [ ! -e "$R/judge/STOPPED" ]'
check "the batch gets the stop file and the budget, and only previews unless JUDGE_EXECUTE=1" \
    '[ "$(arg --stop-file)" = "$R/judge/STOPPED" ] && [ "$(arg --max-usd)" = 15 ] \
        && [ "$(arg --max-usd-per-day)" = 30 ] && [ "$(arg --avg-usd-target)" = 4 ] && ! grep -qx -- --execute "$T/args"'
check "without JUDGE_LOCK the batch takes the host-wide lock, and nothing closes without JUDGE_CLOSE=1" \
    'grep -qx "lock=" "$T/args" && [ ! -e "$T/closes" ]'
check "a usable install becomes a full targets line" \
    '[ "$(grep -c "^nova	$R/reviewd.db	s-reviewd	w-reviewd$" "$R/judge/targets.tsv")" -eq 1 ]'
check "each unusable install becomes a name<TAB>reason line, with its cause" \
    'grep -q "^other	its settings differ from Nova'"'"'s (other credentials)$" "$R/judge/targets.tsv" \
        && grep -q "^broken	its setup script (bin/env.sh) failed$" "$R/judge/targets.tsv" \
        && grep -q "^typo	its folder is missing (check JUDGE_INSTALLS)$" "$R/judge/targets.tsv" \
        && grep -q "no install at $R/reviewd-typo" "$T/tick.err" && grep -q "targets: 1 of 4 installs" "$T/tick.err"'
check "the daily summary goes out once, without the JSON line" \
    '[ "$(alerts "Judge daily")" -eq 1 ] && ! grep -q "schema" "$T/notify.log"'
check "unusable installs alert once, naming each install and its cause" \
    '[ "$(alerts "Judge paused: 3 installs unusable")" -eq 1 ] \
        && grep -q "^- other: its settings differ from Nova'"'"'s (other credentials)$" "$T/notify.log" \
        && grep -q "^- broken: its setup script (bin/env.sh) failed$" "$T/notify.log" \
        && grep -q "^- typo: its folder is missing (check JUDGE_INSTALLS)$" "$T/notify.log"'
tick FAKE_RC=0
check "a second tick that day sends no second summary" '[ "$(alerts "Judge daily")" -eq 1 ]'
check "a second tick still unusable does not alert again" '[ "$(alerts "Judge paused")" -eq 1 ]'
tick FAKE_RC=0 FAKE_BUSY=1 FAKE_STATUS=busy
check "a busy batch (it scanned nothing) leaves the alerted installs as they were" \
    '[ "$(grep -c . "$R/judge/PAUSED-ALERTED")" -eq 3 ] && [ "$(alerts "Judge paused")" -eq 1 ]'
tick FAKE_RC=0
check "the same unusable installs after a busy tick do not alert again" '[ "$(alerts "Judge paused")" -eq 1 ]'

# The status carries the unusable installs after " · "; the reason for the pause is the part before it.
tick FAKE_RC=5 FAKE_LINES=100 FAKE_STATUS="paused: 2 judgments in a row failed · new judges paused: 3 installs unusable" \
    FAKE_CAUSE="2 judgments in a row failed"
code=$?
check "a failure exits with its code and latches STOPPED with when and why, not an exit code" \
    '[ $code -eq 5 ] && grep -qx "20[0-9-]*T[0-9:]*Z: 2 judgments in a row failed" "$R/judge/STOPPED"'
body=$(awk '/^=== Judge paused: 2 judgments in a row failed \|/ { on = 1; next } /^===/ { on = 0 } on' "$T/notify.log")
check "it alerts once, naming the cause in the title, saying why and the rm command, cut under 3500 bytes" \
    '[ "$(alerts "Judge paused: 2 judgments in a row failed |")" -eq 1 ] && ! grep -q "^===.*exit" "$T/notify.log" \
        && [ "$(head -1 <<< "$body")" = "Why: 2 judgments in a row failed" ] \
        && [ "$(sed -n 2p <<< "$body")" = "Judges already running still finish. Nothing new starts until you run: rm $R/judge/STOPPED" ] \
        && [ "$(grep "^=== Judge paused: 2 judgments" "$T/notify.log" | cut -d"|" -f2)" -le 3500 ] \
        && [ "$(tail -1 <<< "$body")" = "… (cut to fit; the full summary is in summary.md on the host)" ] \
        && [ -z "$(sed "1,/^# Judge batch$/d;\$d" <<< "$body" | grep -v "^- line [0-9]\{3\} 0\{67\}$")" ]'
tick FAKE_RC=5 FAKE_CAUSE="2 judgments in a row failed"
check "a failure while STOPPED does not alert again" '[ "$(alerts "Judge paused: 2 judgments")" -eq 1 ]'
tick FAKE_RC=3
check "an ADO change alerts every time" '[ "$(alerts "Judge: a PR changed on ADO")" -eq 1 ]'

sed -i 's/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS="reviewd"/' "$R/judge/judge.env"
tick FAKE_RC=0
body=$(awk '/^=== Judge: no install unusable \|/ { on = 1; next } /^===/ { on = 0 } on' "$T/notify.log")
check "once every install is usable while STOPPED holds, it alerts once, naming them, that STOPPED still holds new judges" \
    '[ "$(alerts "Judge paused: .*unusable")" -eq 1 ] && [ "$(alerts "Judge: no install unusable |")" -eq 1 ] \
        && [ "$body" = "Fixed or removed since the last alert: broken, other, typo.
No install is unusable now. Nothing new starts until you run: rm $R/judge/STOPPED" ] \
        && [ ! -e "$R/judge/PAUSED-ALERTED" ]'
sed -i 's/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS="reviewd reviewd-typo"/' "$R/judge/judge.env"
tick FAKE_RC=0
check "an install unusable again alerts again" '[ "$(alerts "Judge paused: 1 install unusable")" -eq 1 ]'

# The tick alerts whenever the set of unusable installs changes, not only after all were usable.
sed -i 's/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS="reviewd reviewd-broken"/' "$R/judge/judge.env"
tick FAKE_RC=0
check "a different unusable install alerts (one fixed, another broken in the same hour)" \
    '[ "$(alerts "Judge paused: 1 install unusable")" -eq 2 ] \
        && [ "$(cat "$R/judge/PAUSED-ALERTED")" = "- broken: its setup script (bin/env.sh) failed" ]'
sed -i 's/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS="reviewd reviewd-broken reviewd-typo"/' "$R/judge/judge.env"
tick FAKE_RC=0
check "one more unusable install alerts, listing all of them" '[ "$(alerts "Judge paused: 2 installs unusable")" -eq 1 ]'
sed -i 's/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS="reviewd-typo reviewd reviewd-broken"/' "$R/judge/judge.env"
tick FAKE_RC=0
check "the same unusable installs in another order do not alert" '[ "$(alerts "Judge paused: .*unusable")" -eq 4 ]'

# An install still unusable, but for a new reason, alerts with the new reason.
install reviewd-typo different
tick FAKE_RC=0
check "an install unusable for a new reason alerts again, with that reason" \
    '[ "$(alerts "Judge paused: 2 installs unusable")" -eq 2 ] \
        && grep -qx -- "- typo: its settings differ from Nova'"'"'s (other credentials)" "$R/judge/PAUSED-ALERTED"'
body=$(awk '/^=== Judge paused: 2 installs unusable \|/ { body = ""; on = 1; next } /^===/ { on = 0 } on { body = body $0 "\n" }
    END { printf "%s", body }' "$T/notify.log")
check "it first names the install whose reason changed" '[ "$(head -1 <<< "$body")" = "New reason for: typo." ]'
# A shorter list alerts and first says which install is no longer unusable.
sed -i 's/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS="reviewd reviewd-broken"/' "$R/judge/judge.env"
tick FAKE_RC=0
body=$(awk '/^=== Judge paused: 1 install unusable \|/ { body = ""; on = 1; next } /^===/ { on = 0 } on { body = body $0 "\n" }
    END { printf "%s", body }' "$T/notify.log")
check "a shorter list alerts, first saying which install was fixed or removed" \
    '[ "$(alerts "Judge paused: 1 install unusable")" -eq 3 ] \
        && [ "$(head -1 <<< "$body")" = "Fixed or removed since the last alert: typo." ]'

# A missing CLI pauses with a short reason, not dotnet's own text. STOPPED is this test's own (latched above).
rm -f "$R/judge/STOPPED"
sed -i "s|^JUDGE_CLI=[^ ]*|JUDGE_CLI=$T/missing.dll|" "$R/judge/judge.env"
tick FAKE_RC=0
code=$?
body=$(awk '/^=== Judge paused: Reviewd CLI not found \|/ { on = 1; next } /^===/ { on = 0 } on' "$T/notify.log")
check "a missing CLI pauses, saying in words where the CLI was expected, and to fix it before rm" \
    '[ $code -eq 1 ] && [ "$(alerts "Judge paused: Reviewd CLI not found |")" -eq 1 ] \
        && [ "$(head -1 <<< "$body")" = "Why: Reviewd CLI not found at $T/missing.dll" ] \
        && [ "$(sed -n 2p <<< "$body")" = "Judges already running still finish. Nothing new starts until you fix JUDGE_CLI in $R/judge/judge.env, then run: rm $R/judge/STOPPED" ] \
        && grep -qx "20[0-9-]*T[0-9:]*Z: Reviewd CLI not found at $T/missing.dll" "$R/judge/STOPPED" \
        && ! grep -q "Possible reasons" "$T/notify.log"'

# The last unusable install fixed, STOPPED gone: one "Judge resumed" alert. PAUSED-ALERTED (still "broken": the missing
# CLI tick above scanned nothing) goes only once the alert is sent, so a failed send is tried again on the next tick.
rm -f "$R/judge/STOPPED"
sed -i "s|^JUDGE_CLI=[^ ]*|JUDGE_CLI=$T/judge.dll|; s/^JUDGE_INSTALLS=.*/JUDGE_INSTALLS=\"reviewd\"/" "$R/judge/judge.env"
tick FAKE_RC=0 NOTIFY_FAIL=1
check "a resumed alert that fails to send keeps PAUSED-ALERTED, so the next tick sends it" \
    '[ "$(alerts "Judge resumed")" -eq 0 ] && [ -e "$R/judge/PAUSED-ALERTED" ]'
tick FAKE_RC=0
body=$(awk '/^=== Judge resumed \|/ { on = 1; next } /^===/ { on = 0 } on' "$T/notify.log")
check "once every install is fixed or removed, it alerts that judging resumed, naming them" \
    '[ "$(alerts "Judge resumed |")" -eq 1 ] && [ "$body" = "Fixed or removed since the last alert: broken.
No install is unusable now. New judges can start again." ] && [ ! -e "$R/judge/PAUSED-ALERTED" ]'
tick FAKE_RC=0
check "a second clean tick does not alert resumed again" '[ "$(alerts "Judge resumed")" -eq 1 ]'

# Closes. A judge receipt is a row of the install's database: "reviewd/judge-run/<repo key>/<PR>/<n>".
python3 -I - "$R/reviewd.db" <<'PY'
import json, sqlite3, sys
db = sqlite3.connect(sys.argv[1])
db.execute("CREATE TABLE cache (key TEXT PRIMARY KEY, value TEXT)")
def receipt(pr, n, stage, output=None):
    db.execute("INSERT INTO cache VALUES (?, ?)", (f"reviewd/judge-run/org/p/repo/{pr}/{n}", json.dumps(
        {"repository": "Org/P Q/Repo", "prId": str(pr), "n": n, "stage": stage, "outputStage": output})))
receipt(101, 1, "completed", "final")
receipt(102, 1, "completed", "final"); receipt(102, 2, "sent")
receipt(103, 1, "capped", "draft")
receipt(104, 1, "completed", "final"); receipt(104, 2, "completed", "final")
receipt(105, 1, "completed", "final")
receipt(106, 1, "completed", "final")
# Newest by number, not by row or key order: judgment 10 is stored first and sorts before 2.
receipt(108, 10, "sent"); receipt(108, 2, "completed", "final")
db.execute("INSERT INTO cache VALUES ('reviewd/pr-snapshot/org/p/repo/107', '{}')")
db.commit()
PY
cat >> "$R/judge/judge.env" <<EOF
JUDGE_CLOSE=1 JUDGE_STORES=$T/stores JUDGE_LANE=b JUDGE_LOCK=$T/lane-b.lock
EOF
rm -f "$R"/judge/summary-*.md
tick FAKE_RC=0 FAKE_CLOSE_REFUSE=104 FAKE_CLOSE_MOVED=105
closes=$(sed "s/ --config [^ ]* / /; s/ --db [^ ]* --store [^ ]* / /" "$T/closes")
check "a lane passes its own judge lock to the batch and names itself in its alerts" \
    'grep -qx "lock=$T/lane-b.lock" "$T/args" && [ "$(alerts "Judge b daily")" -eq 1 ]'
check "each PR whose newest judgment is completed and final closes once, with that judgment, on its store" \
    '[ "$closes" = "$T/judge.dll judge close --repo Org/P Q/Repo --pr 101 --judgment 1 --execute
$T/judge.dll judge close --repo Org/P Q/Repo --pr 104 --judgment 2 --execute
$T/judge.dll judge close --repo Org/P Q/Repo --pr 105 --judgment 1 --execute
$T/judge.dll judge close --repo Org/P Q/Repo --pr 106 --judgment 1 --execute" ] \
        && [ "$(grep -c -- " --db $R/reviewd.db --store $T/stores/nova-reviews " "$T/closes")" -eq 4 ]'
check "a close that lands is listed in closed.tsv; one that fails goes to close-failed.tsv with its reason" \
    '[ "$(cut -f1-4 "$R/judge/closed.tsv")" = "nova	Org/P Q/Repo	101	1
nova	Org/P Q/Repo	106	1" ] && [ "$(cat "$R/judge/close-failed.tsv")" = "nova	Org/P Q/Repo	104	2	exit 1: the packet changed since the judge" ]'
body=$(awk '/^=== Judge b: 1 close\(s\) failed \|/ { on = 1; next } /^===/ { on = 0 } on' "$T/notify.log")
check "a failed close alerts once, naming the PR, its reason and how to try it again" \
    '[ "$(alerts "Judge b: 1 close")" -eq 1 ] && [ "$body" = "Each is skipped until you look at it. To try one again, remove its line from $R/judge/close-failed.tsv.
- Org/P Q/Repo !104 (judgment 2): exit 1: the packet changed since the judge" ]'
check "the daily summary counts the closes" \
    'grep -q "^Closed in the last 24 h: 2 PRs (2 in all). This tick: 2 closed, 1 to retry (main kept moving), 0 waiting for the next tick. Close failures waiting for you: 1 (close-failed.tsv).$" "$T/notify.log"'
: > "$T/closes"
tick FAKE_RC=0 FAKE_CLOSE_REFUSE=104
check "the next tick tries again only the close whose main kept moving, and alerts nothing new" \
    '[ "$(grep -c . "$T/closes")" -eq 1 ] && grep -q -- "--pr 105 " "$T/closes" && [ "$(alerts "Judge b: 1 close")" -eq 1 ] \
        && [ "$(grep -c . "$R/judge/closed.tsv")" -eq 3 ]'
sed -i '/	104	/d' "$R/judge/close-failed.tsv"
: > "$T/closes"
touch "$R/judge/STOPPED"
tick FAKE_RC=0
check "nothing closes while STOPPED exists" '[ ! -s "$T/closes" ]'
rm -f "$R/judge/STOPPED"
tick FAKE_RC=0 FAKE_CLOSE_MOVED=104
check "a failed close whose line was removed is tried again" \
    '[ "$(grep -c . "$T/closes")" -eq 1 ] && grep -q -- "--pr 104 " "$T/closes"'
echo JUDGE_CLOSE_LIMIT=0 >> "$R/judge/judge.env"
: > "$T/closes"
tick FAKE_RC=0
check "JUDGE_CLOSE_LIMIT caps closes per tick; the rest wait for the next tick" \
    '[ ! -s "$T/closes" ] && grep -q "0 closed, 0 to retry (main kept moving), 1 waiting for the next tick" "$T/tick.err"'
python3 -I - "$R/reviewd.db" <<'PY2'
import json, sqlite3, sys
db = sqlite3.connect(sys.argv[1])
for pr in (109, 110):
    db.execute("INSERT INTO cache VALUES (?, ?)", (f"reviewd/judge-run/org/p/repo/{pr}/1", json.dumps(
        {"repository": "Org/P Q/Repo", "prId": str(pr), "n": 1, "stage": "completed", "outputStage": "final"})))
db.commit()
PY2
echo JUDGE_CLOSE_LIMIT=30 >> "$R/judge/judge.env"
tick FAKE_RC=0 FAKE_CLOSE_STOPS=104
check "a STOPPED latch made while a close runs stops the rest of that tick's closes" \
    '[ "$(grep -c . "$T/closes")" -eq 1 ] && grep -q -- "--pr 104 " "$T/closes" \
        && grep -q "1 closed, 0 to retry (main kept moving), 2 waiting for the next tick" "$T/tick.err"'
rm -f "$R/judge/STOPPED"
: > "$T/closes"
tick FAKE_RC=0 FAKE_CLOSE_REFUSE=109 NOTIFY_FAIL=1
check "a close failure whose alert was not sent is not alerted as sent" \
    '[ "$(grep -c . "$T/closes")" -eq 2 ] && grep -q "	109	1	exit 1: " "$R/judge/close-failed.tsv" \
        && [ "$(alerts "Judge b: 1 close")" -eq 1 ]'
: > "$T/closes"
tick FAKE_RC=0
body=$(awk '/^=== Judge b: 1 close\(s\) failed \|/ { n++ } n == 2 && !/^===/' "$T/notify.log")
check "the next tick sends that alert without running the failed close again" \
    '[ ! -s "$T/closes" ] && [ "$(alerts "Judge b: 1 close")" -eq 2 ] \
        && [ "$body" = "Each is skipped until you look at it. To try one again, remove its line from $R/judge/close-failed.tsv.
- Org/P Q/Repo !109 (judgment 1): exit 1: the packet changed since the judge" ]'
tick FAKE_RC=0
check "an alert that was sent is not sent again" '[ "$(alerts "Judge b: 1 close")" -eq 2 ]'
sed -i '/	109	/d' "$R/judge/close-failed.tsv"
tick FAKE_RC=0 FAKE_CLOSE_REFUSE=109
check "a close tried again after its line was removed alerts again when it fails the same way" \
    '[ "$(alerts "Judge b: 1 close")" -eq 3 ]'

# Shards: lanes that share an install each set JUDGE_SHARD=i/n. The tick passes it to the batch as --shard and closes
# only that shard's PRs; a PR id that is not a number is shard 0's.
check "without JUDGE_SHARD the batch gets no --shard" '! grep -qx -- --shard "$T/args"'
python3 -I - "$R/reviewd.db" <<'PY3'
import json, sqlite3, sys
db = sqlite3.connect(sys.argv[1])
for pr in ("111", "112", "abc"):
    db.execute("INSERT INTO cache VALUES (?, ?)", (f"reviewd/judge-run/org/p/repo/{pr}/1", json.dumps(
        {"repository": "Org/P Q/Repo", "prId": pr, "n": 1, "stage": "completed", "outputStage": "final"})))
db.commit()
PY3
echo JUDGE_SHARD=1/2 >> "$R/judge/judge.env"
: > "$T/closes"
tick FAKE_RC=0
check "a shard lane passes its shard to the batch and closes only its own PRs" \
    '[ "$(arg --shard)" = 1/2 ] && [ "$(grep -c . "$T/closes")" -eq 1 ] && grep -q -- "--pr 111 " "$T/closes"'
sed -i 's|^JUDGE_SHARD=.*|JUDGE_SHARD=0/2|' "$R/judge/judge.env"
: > "$T/closes"
tick FAKE_RC=0
check "the other shard closes the rest, the PR id that is not a number included" \
    '[ "$(arg --shard)" = 0/2 ] && [ "$(grep -c . "$T/closes")" -eq 2 ] && grep -q -- "--pr 112 " "$T/closes" \
        && grep -q -- "--pr abc " "$T/closes"'

echo "$failures failure(s)"
[ "$failures" -eq 0 ]
