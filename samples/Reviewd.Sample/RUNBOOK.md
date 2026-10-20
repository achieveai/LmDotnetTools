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
2. Copy the databases. SQLite's backup also takes in what their `-wal` files still hold, so never
   copy a `.db` file without its sidecars:

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

Run step 5 again in any terminal. You do not restart the batch.

- The batch checks `selection.json` every 5 seconds, but starts a new list only after its current
  list is done. When the list is done it logs `Batch scheduling finished. OAuth callback remains
  running.` and waits for the next new `selection.json`. That line means "waiting", not "stuck".
- A list is not done while a lane waits on a held slot, for up to 2 hours (the reaper frees it
  sooner). New PRs wait until then. `safe-restart-batch.sh` (step 9) cuts the wait short.
- A file with the same content as the last one is not run again. `pick` writes a new `requestId`
  each time, so a fresh pick always counts as new.
- A PR whose review send failed before the host accepted it (for example a 503 from a cold gateway)
  needs no manual steps. Once its slot is released, the next pick offers it again and the batch
  reviews it on its existing, empty conversation. A PR whose review input the host did accept is
  never sent again, so its comments are not posted twice.

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

## Known gaps

- Nothing restarts by itself after a crash or reboot. Running host and batch as systemd user
  services is planned.

- New PRs wait until the batch finishes its current list (up to 2 hours when a lane waits on a held
  slot; see step 8).
- If an author pushes while their PR is being reviewed, that PR is skipped until the next selection.
- PRs with merge conflicts are skipped after 5 tries.
- Some reviews post their comments but fail to save their record to NOVA_reviews ("Changes belong to
  another repository or PR"). Their slot frees up through the 5-minute sweep.
