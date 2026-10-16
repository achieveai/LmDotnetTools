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

- Work from: `/home/gautamb/repos/LmDotnetTools/.claude/worktrees/reviewd-sample`
- CLI: `samples/Reviewd.Sample/bin/Debug/net9.0/Reviewd.Sample.dll`. Rebuild with
  `dotnet build samples/Reviewd.Sample`.
- Host app: `.run/reviewd-local/host/LmStreaming.Sample.dll`
- Config and keys: `.run/reviewd-local/config/` (owner-only files; source them, never print them)
- Sign-in tokens: `/home/gautamb/repos/LmDotnetTools/.run/nova/oauth-tokens`
- **The one database:** `.run/reviewd-local/reviews.db`. Always use this one. Separate databases do not
  see each other's slots, which is what jammed lanes before.
- Selection (the PR list the batch works on): `.run/reviewd-local/selection.json`
- Gateway: `/home/gautamb/Gateway/docker-compose.yml`
- Scripts the reviews run inside the sandbox (you never run these):
  `/workspace/.claude/skills/review-setup/scripts/`. Their source is
  `samples/CodeReviewDaemon.Sample/review-host-kit/skills/review-setup/scripts/`.

## 0. Every new terminal

```bash
cd /home/gautamb/repos/LmDotnetTools/.claude/worktrees/reviewd-sample
set -a
source .run/reviewd-local/config/review-host.env
source .run/reviewd-local/config/review-daemon.env
source .run/reviewd-local/config/gateway-app-credentials.env
set +a
export ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production
export Auth__TokenStoreDir=/home/gautamb/repos/LmDotnetTools/.run/nova/oauth-tokens
R="dotnet samples/Reviewd.Sample/bin/Debug/net9.0/Reviewd.Sample.dll"
CFG=.run/reviewd-local/config/appsettings.nova.json
DB=.run/reviewd-local/reviews.db
SEL=.run/reviewd-local/selection.json
SESSION=3c3455c5-6ae1-4059-b083-239726dc0456
WS=43eb5a86-3df5-4d62-9528-77744fd3d8aa
REPOS="O365Exchange/Weve_DA/Nova,O365Exchange/Weve_DA/NovaClient,O365Exchange/Weve_DA/Astra,O365Exchange/O365 Core/WeveNova,O365Exchange/O365 Core/MODISService"
```

## 1. Check what is up

```bash
ss -ltn | grep -E ':(3000|5000|5206|5235) '
ps -eo pid,etime,cmd | grep -E '[L]mStreaming.Sample|[R]eviewd.Sample.dll'
```

Gateway down? `cd /home/gautamb/Gateway && docker compose up -d`, then come back.

## 2. Start the host (only if 5000 is down). Terminal 1, leave it open

```bash
H=$PWD/.run/reviewd-local/host
DOTNET_hostBuilder__reloadConfigOnChange=false \
SandboxGateway__AppKey="$DAEMON_GATEWAY_APP_KEY" dotnet $H/LmStreaming.Sample.dll \
  --urls http://0.0.0.0:5000 --contentRoot $H --webroot $H/wwwroot \
  --Identity:DatabasePath $H/identity.db --LmStreaming:NotifyWaitDbPath $H/notify-waits.db \
  --SandboxGateway:BaseUrl http://127.0.0.1:3000 --SandboxGateway:AppId codereview-daemon-nova \
  --SandboxGateway:AutoSpawn false --Auth:Webhook:PublicBaseUrl http://127.0.0.1:5000
```

- Healthy: `curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5000/` prints `200`.
- The app ID and key must be the matching pair shown above. A wrong pair makes every review fail with 503.
- `reloadConfigOnChange=false` stops the host from watching every folder under its working directory and content root for config edits. From the repo root that is ~55,000 folders per watcher and ~7% of the host's CPU. Edits to `$H/appsettings.json` now take effect only on the next start.
- A staged build in `.run/reviewd-local/host.next`? With the host stopped, run `.run/reviewd-local/swap-host.sh` first. It keeps data and the deployed `appsettings*.json`.

## 3. Start the webhook (only if 5206 is down). Terminal 2, leave it open

```bash
$R auth serve --listen http://172.17.0.1:5206 --callback http://host.docker.internal:5206 --config $CFG
```

- Healthy: `curl -s -o /dev/null -w '%{http_code}\n' http://172.17.0.1:5206/api/auth/webhook/ado`
  prints `405`.
- If this is down, the sandbox cannot reach Azure DevOps (`502 policy_evaluation_failed`).

## 4. First time only: move over from the old Nova batch

The old batch uses `nova-reboot2-20261015.db`, which records the slots it holds. Stop it, then make
that file the one database:

```bash
ps -eo pid,etime,cmd | grep "[r]eview batch"     # note the PID
kill -KILL <PID>
cp .run/reviewd-local/nova-reboot2-20261015.db $DB
```

- Reviews it had already started keep running on the host and still post their PR comments.
- Their slots free up once their lease lapses (up to 30 minutes) and the review is idle. Until then
  those Nova lanes wait; the other lanes run.

## 5. Find PRs and write the selection (about 8 minutes)

```bash
mkdir -p .logs
$R pr check --repos "$REPOS" --db $DB --config $CFG > .logs/pr-check.jsonl
$R pr pick --repos "$REPOS" --db $DB --max-age-hours 72 --selection-out $SEL > /dev/null
```

- `pick` prints `Picked N eligible PR(s)` and `Wrote batch selection`.
- If `check` says `…snapshot was not saved` for a repo, run `check` again for that repo alone
  (`--repos "<that repo>"`).
- The count can include PRs that were already reviewed with an older database. The batch skips them
  in seconds: it finds their review on the host. That prevents duplicate PR comments.

## 6. Start the batch. Terminal 3, leave it open

```bash
$R review batch --selection $SEL --db $DB --session $SESSION --workspace $WS \
  --provider claude-haiku-5.5 --mode code-review-daemon --reasoning-effort xhigh --max-age-hours 72 \
  --listen http://127.0.0.1:5235 --config $CFG --execute 2>&1 | tee -a .logs/review-batch.log
```

- Changing `--provider` needs a fresh selection (step 5), and only once no review is in flight. A
  started review resumes only on the provider it began with.

## 7. Watch it

- Progress lines: `<repo> #<pr>: prepare | review | confirmation | verify | commit`.
- A finished review: a JSON line containing `"stage":"completed-verified"`.
  Count them: `grep -c '"stage":"completed-verified"' .logs/review-batch.log`
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

- **Stop:** Ctrl+C in terminal 3. Reviews already running finish first. Use `kill -KILL` to stop at once.
- **After a reboot:** steps 0, 1, 2, 3, 5, 6. Skip step 4.
- **A slot stays stuck for hours:** check it first with a dry run, then add `--execute`:

  ```bash
  $R review reclaim-slot --repo "<org/project/repo>" --pr <id> --slot <0-5> \
    --db $DB --workspace $WS --session $SESSION --config $CFG
  ```

## Known gaps

- New PRs wait until the batch finishes its current list.
- If an author pushes while their PR is being reviewed, that PR is skipped until the next selection.
- PRs with merge conflicts are skipped after 5 tries.
- Some reviews post their comments but fail to save their record to NOVA_reviews ("Changes belong to
  another repository or PR"). Their slot frees up through the 5-minute sweep.
