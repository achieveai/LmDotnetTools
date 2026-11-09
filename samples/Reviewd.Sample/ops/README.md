# Windows runner ops scripts

These are the scripts that run the two live Reviewd installs on the Windows box. The Linux scripts beside this folder are a separate setup.

- `github/` deploys to `B:\published\Reviewd` (GitHub repos). `ado/` deploys to `B:\published\ADO-Reviewd` (Azure DevOps repos).
- `dashboard/build.py` deploys to `B:\published\_dashboard\build.py`.

The live copies are the source of truth today. These were copied from them byte for byte on 2026-11-09. Git stores `start-host.ps1` and `reviewd.cmd` with LF endings; the live copies use CRLF.

## Scripts
- `ensure-reviewd.ps1`: the watchdog. Starts whatever is down: host (GitHub only), OAuth webhook and review batch. Restarts a batch that logged "Scheduling stopped.". Honors `state\PAUSED`. Writes `state\status.json`. The ADO copy never starts the host: it shares the GitHub install's host on :5310.
- `discover.ps1`: runs `pr check` and `pr pick`, caps lanes at `lanesPerRepo`, then swaps in `state\selection.json` for the running batch. The ADO copy also skips `excludedLanes`.
- `register-tasks.ps1`: registers that install's Watchdog and Discovery tasks. Safe to re-run.
- `reviewd.ps1`: runs `cli\Reviewd.Sample.exe` with `state\config\cli.env` and `appsettings.reviewd.json`. Byte-identical in both folders; both copies are kept because each install deploys on its own.
- `start-host.ps1` (GitHub): starts `host\LmStreaming.Sample.exe` with `state\config\host.env`, then warms the gateway catalog.
- `reviewd.cmd` (GitHub): calls the CLI with no config. For quick manual use.
- `alarm.py` (GitHub): checks both installs and sends a HITL Notify when one has stopped reviewing. It can stop a wedged batch. `--dry-run` only prints.
- `judge-tick.ps1` and `judge-pending-closes.py` (ADO): judge merged PRs' reviews within a daily cap, then file the final judgments. Settings are in `state\judge\judge.json`.
- `rcpt.py`, `stages.py`, `bstate.py`, `watch-stages.sh` (GitHub): ad-hoc diagnostics. They read `reviews.db` through WSL paths. Nothing schedules them.
- `dashboard/build.py`: builds `reviewd.html` from both installs' `reviews.db`, read-only.
- `runner.example.json`: each install's `state\config\runner.json`, with the gateway session and workspace ids replaced.

## Scheduled tasks
Checked with `Get-ScheduledTask` on 2026-11-09. Each runs as the signed-in user, with no window.

| Task | Runs | Every |
| --- | --- | --- |
| Reviewd Watchdog / ADO Reviewd Watchdog | `ensure-reviewd.ps1` | 5 min, and at logon |
| Reviewd Discovery / ADO Reviewd Discovery | `discover.ps1` | 2 min |
| Reviewd Alarm | `github/alarm.py` | 15 min |
| ADO Reviewd Judge | `ado/judge-tick.ps1` | 45 min |
| Reviewd Dashboard | `dashboard/build.py` | 5 min |

`register-tasks.ps1` only creates the Watchdog and Discovery tasks. The Alarm, Judge and Dashboard tasks were registered by hand.
## Deploying a change
1. Back up the live file next to itself as `<name>.prev-<yyyymmdd>`. For example: `discover.ps1.prev-20261109`.
2. Copy the new file over the live one. Tasks pick it up on their next run. Nothing needs a restart.
3. Commit the same change here, so this folder keeps matching the live copy.

Not here: `cli\`, `host\` and `kit\` (build output), `state\` (database, logs, selection) and the config files that hold or may hold secrets (`host.env`, `cli.env`, `secrets.env`, `appsettings.reviewd.json`).
