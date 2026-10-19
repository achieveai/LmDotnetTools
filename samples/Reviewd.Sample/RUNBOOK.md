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

- New PRs wait until the batch finishes its current list.
- If an author pushes while their PR is being reviewed, that PR is skipped until the next selection.
- PRs with merge conflicts are skipped after 5 tries.
- Some reviews post their comments but fail to save their record to NOVA_reviews ("Changes belong to
  another repository or PR"). Their slot frees up through the 5-minute sweep.
