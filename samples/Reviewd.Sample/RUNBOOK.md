# Run the NOVA reviews yourself

One batch reviews all 5 repos at once: 6 slots each, **30 reviews in parallel**. No Python. Every
command below is the `Reviewd.Sample` CLI, `dotnet`, or plain shell.

## What runs, and what you keep alive

| Piece | Port | Comes back after a reboot? |
| --- | --- | --- |
| Gateway (Docker) | 3000 | Yes, Docker restarts it. |
| Review host (`LmStreaming.Sample`) | 5000 | **No.** Step 2. |
| Webhook (`auth serve`) | 5206 | **No.** Step 3. |
| Batch (`review batch`) | 5235 | **No.** Step 6. |

The batch runs until you stop it. It frees slots that died by itself every 5 minutes. It does **not**
find new PRs by itself: step 5 does that.

## Where things are

Everything lives in one folder outside any git checkout, so cleaning a worktree or rebooting loses
nothing:

`REVIEWD_HOME=~/.local/share/lmdotnettools-review-daemon/reviewd`

| Path under `REVIEWD_HOME` | What |
| --- | --- |
| `bin/` | The scripts below. Source: `samples/Reviewd.Sample/ops/`. Copy them here after a change. |
| `env/` | Keys and config (owner-only; source them, never print them). `batch.env` holds the session, workspace, provider and repo list. `agency-bridge.env` holds the agency bridge token. |
| `oauth-tokens/` | Sign-in tokens shared by the host, the CLI and `auth serve`. |
| `data/reviews.db` | **The one database.** Always use this one. Separate databases do not see each other's slots, which is what jammed lanes before. |
| `data/selection.json` | The PR list the batch works on. Older ones: `data/selections/`. |
| `data/host/` | Host data: conversations, identity, notify waits, its JSON logs, workflow index, workspaces, and its deployed `appsettings.json`. |
| `cli`, `host` | Links to the running builds in `releases/cli/<commit>` and `releases/host/<commit>`. |
| `logs/` | `review-batch.log`, `host-console.log`, `auth-serve.log`, `pr-check.*`. Rotated daily. |

- Gateway: `/home/gautamb/Gateway/docker-compose.yml`
- Scripts the reviews run inside the sandbox (you never run these):
  `/workspace/.claude/skills/review-setup/scripts/`. Their source is
  `samples/CodeReviewDaemon.Sample/review-host-kit/skills/review-setup/scripts/`.

## 0. Every new terminal

```bash
source ~/.local/share/lmdotnettools-review-daemon/reviewd/bin/env.sh
cd $REVIEWD_HOME
```

This loads the keys and sets `$R` (the CLI), `$CFG`, `$DB`, `$SEL`, `$SESSION`, `$WS`, `$REPOS` and `$LOGS`.

## 1. Check what is up

```bash
ss -ltn | grep -E ':(3000|5000|5206|5235) '
ps -eo pid,etime,cmd | grep -E '^ *[0-9]+ +[0-9:-]+ dotnet .*(LmStreaming|Reviewd)\.Sample'
```

Gateway down? `cd /home/gautamb/Gateway && docker compose up -d`, then come back.

## 2. Start the host (only if 5000 is down)

```bash
$REVIEWD_HOME/bin/start-host.sh
```

- Healthy: `curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5000/` prints `200`.
- It runs in the background. Log: `$LOGS/host-console.log`. JSON log: `data/host/logs/`.
- It uses the matching gateway app ID and key. A wrong pair makes every review fail with 503.
- If `env/agency-bridge.env` has a token, the host sends `Authorization: Bearer <token>` to
  `agency.internal:8765`. Only sandboxes created after the host starts get this. A sandbox that is
  already running keeps the policy it was created with.
- Stop: `kill <pid>` (it takes about 40 seconds to exit).

## 3. Start the webhook (only if 5206 is down)

```bash
$REVIEWD_HOME/bin/start-auth.sh
```

- Healthy: `curl -s -o /dev/null -w '%{http_code}\n' http://172.17.0.1:5206/api/auth/webhook/ado`
  prints `405`.
- If this is down, the sandbox cannot reach Azure DevOps (`502 policy_evaluation_failed`).

## 4. First time only: move over from the old worktree

The state used to live in the old worktree at `$REVIEWD_LEGACY_RUN_DIR` (default
`~/repos/LmDotnetTools/.claude/worktrees/reviewd-sample/.run/reviewd-local`). While that folder
still has `reviews.db` and `data/reviews.db` is empty, `start-batch.sh` and `start-host.sh` refuse
to start. Starting on the empty database would hide every slot the old reviews hold.

1. Stop the batch, `auth serve` and the host. Step 1 must show none of them.
2. Copy the databases. SQLite's logical backup also takes in what their `-wal` files still hold,
   so never copy a `.db` file alone when WAL may contain committed data. The command below opens
   the originals and can create or change SQLite sidecars, even with a read-only connection.
   It is a legacy migration recipe, **not an unchanged-source evidence capture**. For Windows
   maintenance with a strict source-file guard, use [offline capture](#windows-offline-state-capture).

   ```bash
   OLD=$REVIEWD_LEGACY_RUN_DIR
   backup() { python3 -c 'import sqlite3,sys; sqlite3.connect(sys.argv[1]).backup(sqlite3.connect(sys.argv[2]))' "$1" "$2"; }
   mkdir -p $REVIEWD_HOME/data/host
   backup $OLD/reviews.db $DB
   for db in conversations identity notify-waits; do
       [ -e $OLD/host/$db.db ] && backup $OLD/host/$db.db $REVIEWD_HOME/data/host/$db.db
   done
   cp $OLD/selection.json $SEL
   for item in logs workflow-index chat-modes workspaces appsettings.json; do
       [ -e $OLD/host/$item ] && cp -a $OLD/host/$item $REVIEWD_HOME/data/host/
   done
   cp -a /home/gautamb/repos/LmDotnetTools/.run/nova/oauth-tokens/. $REVIEWD_HOME/oauth-tokens/
   ```

3. Move the old folder aside (`mv $OLD $OLD.moved`), then start with step 2.

- A host older than the SQLite store kept its history in a `conversations/` folder. That folder is
  not imported.
- To start fresh on purpose instead, set `REVIEWD_LEGACY_STATE=abandoned` in `env/batch.env`. The new
  database then knows nothing of the slots the old reviews hold.

### Windows offline state capture

Use `ops/backup-offline-state.ps1` with PowerShell **7.5+** and Python 3. It copies files first;
SQLite is opened only against separate working copies. It does not stop services, restore files,
change scheduling, or approve deployment. Linux ACL preservation is outside this helper's scope.

Before capture, disable and drain discovery/watchdogs, drain admitted reviews, then stop the host,
both runners and their authentication/launcher processes. Review shutdown logs: exit code0 and an
empty queue do not prove final writes succeeded. Investigate unknown writers before proceeding.
Keep those producers stopped throughout capture and verification.

Write an explicit scope JSON outside the source trees. `databases` must include `conversations`,
`identity`, `github` and `ado`; add other databases, such as a separate `notify-waits.db`, when used.
Every `entries` path is required and copied recursively, including empty directories. List all host
auxiliary state, root configuration JSON, runner config, selection/control snapshots and any shared
token state required by your recovery plan. An omitted path is **not backed up**. Do not list a file
again inside a selected directory. The helper rejects overlaps, missing paths and reparse points.

```json
{
  "schema": 1,
  "databases": [
    { "name": "conversations", "path": "C:\\Reviewd\\state\\host\\conversations.db" },
    { "name": "identity", "path": "C:\\Reviewd\\state\\host\\identity.db" },
    { "name": "github", "path": "C:\\Reviewd\\state\\reviews.db" },
    { "name": "ado", "path": "C:\\ADO-Reviewd\\state\\reviews.db" }
  ],
  "entries": [
    { "name": "chat-modes", "path": "C:\\Reviewd\\state\\host\\chat-modes" },
    { "name": "legacy-conversations", "path": "C:\\Reviewd\\state\\host\\conversations" },
    { "name": "oauth-tokens", "path": "C:\\Reviewd\\state\\host\\oauth-tokens" },
    { "name": "workflow-index", "path": "C:\\Reviewd\\state\\host\\workflow-index" },
    { "name": "workspaces", "path": "C:\\Reviewd\\state\\host\\workspaces" },
    { "name": "host-config", "path": "C:\\Reviewd\\state\\host\\appsettings.json" },
    { "name": "hierarchy-index", "path": "C:\\Reviewd\\state\\host\\conversations.parent-child-index.json" },
    { "name": "github-config", "path": "C:\\Reviewd\\state\\config" },
    { "name": "ado-config", "path": "C:\\ADO-Reviewd\\state\\config" }
  ],
  "runtime": {
    "processPaths": ["C:\\Reviewd\\host\\LmStreaming.Sample.exe", "C:\\Reviewd\\cli\\Reviewd.Sample.exe", "C:\\ADO-Reviewd\\cli\\Reviewd.Sample.exe"],
    "commandPaths": ["C:\\Reviewd\\start-host.ps1", "C:\\Reviewd\\ensure-reviewd.ps1", "C:\\ADO-Reviewd\\ensure-reviewd.ps1"],
    "tasks": ["Reviewd Discovery", "Reviewd Watchdog", "ADO Reviewd Discovery", "ADO Reviewd Watchdog"],
    "ports": [5310, 5311, 5312, 5321, 5322]
  }
}
```

Adapt every path and runtime list to the inspected installation; this example is not discovery.
Inventory the host's root JSON files while preparing the scope; include each actual file rather
than assuming `appsettings.json` is the only configuration/index file. Freeze and review that list.
For `dotnet app.dll` and PowerShell wrappers, include the DLL/script path in `commandPaths` too.
Task names must identify one task each. The helper checks these declared processes, command paths,
listeners and disabled/drained tasks. It cannot certify that an omitted or unreadable unrelated
process is harmless. Reviewed quiescence evidence must address the complete producer/writer scope.

Record the evidence as JSON, with real UTC observation time and SHA256 of the **exact scope bytes**:

```json
{
  "schema": 1,
  "observedAtUtc": "2026-10-29T17:00:00Z",
  "scopeSha256": "<64 hexadecimal characters>",
  "producersDisabled": true,
  "writersStopped": true,
  "shutdownDurabilityPassed": true,
  "unknownWriters": false,
  "owners": 0,
  "unreleasedAttempts": 0
}
```

These are operator-reviewed claims, not values the script discovers or authenticates. Do not fill
them from process liveness alone. The receipt must be no more than ten minutes old and not future
dated. Pin its SHA256 after review, then invoke from a file/terminal without putting credentials in
arguments. The destination parent must already exist; the destination itself must be new.
`shutdownDurabilityPassed` must be a boolean. Set it to `false` when final writes failed: capture
can still preserve the valid saved state, and records that failure without approving an upgrade.

```powershell
pwsh -NoProfile -File ./samples/Reviewd.Sample/ops/backup-offline-state.ps1 `
  -ScopePath C:\maintenance\scope.json -QuiescencePath C:\maintenance\quiescence.json `
  -ReviewedQuiescenceSha256 <reviewed-sha256> -Destination D:\recovery\capture-unique-name
```

The helper compares source file sets, bytes and owner/group/access rules before and after capture.
Read handles deny writes while files are copied. Each destination receives checked, protected
owner/group/access rules before private bytes; Windows audit rules (SACLs) are not copied. Raw main,
WAL and rollback-journal files are retained; SHM is a transient index rebuilt only in working copies.
Missing optional sidecars are recorded in `source-inventory.json`. Never delete or overwrite a
failed attempt to retry: inspect it, resolve the cause, review fresh evidence and use a new path.

Success produces `complete.json`, raw file hashes and per-database integrity/schema results. A
failure after destination creation retains partial files and `failed.json`, when access permits;
absence of `complete.json` means incomplete. Working-copy recovery may finalize WAL/journal state;
the raw capture must remain byte-identical. Integrity proves saved-state validity, not that failed
memory-only writes were saved, nor that schema compatibility or a later deployment is safe.

For recovery, keep writers stopped, verify the complete receipt/raw hashes and restore only into
new reviewed paths. Use a coherent database bundle (main plus saved WAL/journal) or a separately
validated finalized working copy; do not overlay an old main onto unrelated live sidecars. Preserve
the existing installation/state until recovery is independently checked. This helper intentionally
has no restore or deletion action.

## 5. Find PRs and write the selection (about 8 minutes)

```bash
$REVIEWD_HOME/bin/pick.sh
```

- It prints `Picked N eligible PR(s)` and `Wrote batch selection`. The old selection goes to
  `data/selections/`.
- If `check` says `…snapshot was not saved` for a repo, run `check` again for that repo alone
  (`--repos "<that repo>"`).
- The count can include PRs that were already reviewed with an older database. The batch skips them
  in seconds: it finds their review on the host. That prevents duplicate PR comments.

## 6. Start the batch

```bash
nohup setsid $REVIEWD_HOME/bin/start-batch.sh >/dev/null 2>&1 < /dev/null &
```

- Or run `$REVIEWD_HOME/bin/start-batch.sh` in a terminal you leave open.
- Log: `$LOGS/review-batch.log`. Provider, effort and age limit come from `env/batch.env`.
- Changing the provider needs a fresh selection (step 5), and only once no review is in flight. A
  started review resumes only on the provider it began with.

## 7. Watch it

- Progress lines: `<repo> #<pr>: prepare | review | confirmation | verify | commit`.
- A finished review: a JSON line containing `"stage":"completed-verified"`.
  Count them: `grep -c '"stage":"completed-verified"' $LOGS/review-batch.log`
- `held on lane N … Skipped; the lane continues` means one PR was skipped. The lane keeps going.
- Busy slots right now:

  ```bash
  $R sandbox exec --session $SESSION --config $CFG -- \
    sh -c 'cd /workspace/.git/review-setup/reviewd-owners && grep -l "\"owned\"" *.json'
  ```

## 8. Add new PRs (every few hours)

Run step 5 again in any terminal. The running batch picks up the new `selection.json` once it
finishes its current list. You do not restart it.

## 9. Stop, reboot, stuck slot

- **Stop:** Ctrl+C if it runs in a terminal, or `kill <pid>`. Reviews already running finish first.
  Use `kill -KILL` to stop at once.
- **Restart the batch safely** (for example to deploy a CLI): `$REVIEWD_HOME/bin/safe-restart-batch.sh`.
  It waits until every owned review can resume, then restarts. It gives up after 15 minutes.
  It only touches the batch whose `--db` is `$DB`. It pauses that batch for each check, so no review
  can move on between the check and the kill. It stops without touching anything when two batches
  use `$DB`. Its test: `ops/test/ops-scripts.test.sh` (Linux or WSL).
- **After a reboot:** steps 0, 1, 2, 3, 5, 6.
- **A slot stays stuck for hours:** check it first with a dry run, then add `--execute`:

  ```bash
  $R review reclaim-slot --repo "<org/project/repo>" --pr <id> --slot <0-5> \
    --db $DB --workspace $WS --session $SESSION --config $CFG
  ```

## 10. Deploy a new build

Publish the build, stop the matching process, then:

```bash
$REVIEWD_HOME/bin/deploy.sh cli  <publish dir> <commit>    # then step 6
$REVIEWD_HOME/bin/deploy.sh host <publish dir> <commit>    # then step 2
```

- The host keeps its data and deployed `appsettings.json` in `data/host`; `deploy.sh` links them in.
- Rollback: `ln -sfn releases/cli/<older commit> $REVIEWD_HOME/cli` (or `host`), then restart.
  A host older than judge read-only egress ignores it: a new judge stops (no acknowledgement), but one
  already in flight resumes with full ADO egress. Do not resume judgments on a rolled-back host.

## Known gaps

- Nothing restarts by itself after a crash or reboot. Running host and batch as systemd user
  services is planned.

- New PRs wait until the batch finishes its current list. So one review that never ends blocks every
  later selection, for every repo: the batch re-reads `selection.json` only after the current list
  returns. Seen 2026-10-19 (4+ hours, nothing new reviewed). A review now stops waiting on an input that
  stays `NotStarted` for 10 minutes while the host runs nothing for it and every sub-agent has settled.
  It records the input as lost and goes on to its confirmation and verify, which decide the outcome, so
  the lane moves on. A review that keeps a run going forever still blocks. If
  `review-batch.log` shows no progress line for an hour, check the stuck slot (step 9).
- If an author pushes while their PR is being reviewed, that PR is skipped until the next selection.
- PRs with merge conflicts are skipped after 5 tries.
- Some reviews post their comments but fail to save their record to NOVA_reviews ("Changes belong to
  another repository or PR"). Their slot frees up through the 5-minute sweep.
