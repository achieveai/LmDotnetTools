# Windows runner ops scripts

These are the scripts that run the two live Reviewd installs on the Windows box. The Linux scripts beside this folder are a separate setup.

- `github/` deploys to `B:\published\Reviewd` (GitHub repos). `ado/` deploys to `B:\published\ADO-Reviewd` (Azure DevOps repos).
- `dashboard/build.py` deploys to `B:\published\_dashboard\build.py`.

**Not deployed yet.** These files matched the live copies on 2026-11-09, apart from line endings (`.gitattributes` stores every script as LF). Review fixes then changed them here on purpose. The live copies predate these fixes; deploy after merge with Gautam's OK.

The registered scheduled tasks are the operator's standing request to review the configured repos (`repos` in `runner.json`), including the `--execute` batch and judge runs.

## Scripts
- `ensure-reviewd.ps1`: the watchdog. Starts whatever is down: host (GitHub only), OAuth webhook and review batch. Restarts a batch that logged "Scheduling stopped.", and starts the new one only after the old one has exited. Honors `state\PAUSED`. Writes `state\status.json`; `batch` there is true only for a batch seen running, never for a launch. The ADO copy never starts the host: it shares the GitHub install's host on :5310.
- `discover.ps1`: runs `pr check` and `pr pick`, sets lanes from `lanesPerRepo` (1-6), then swaps in `state\selection.json` for the running batch. The ADO copy also skips `excludedLanes`, and refuses a repo left with no lane.
- Both log any failure, including a missing or unreadable `runner.json`, to `state\logs\ensure.log` or `discover.log` and exit 1: their task actions keep no stderr.
- `register-tasks.ps1`: registers that install's Watchdog and Discovery tasks. Safe to re-run.
- `reviewd.ps1`: runs `cli\Reviewd.Sample.exe` with `state\config\cli.env` and `appsettings.reviewd.json`, unless the CLI's own arguments (before `--`) name a `--config`. Identical in both folders; both are kept because each install deploys on its own.
- `start-host.ps1` (GitHub): starts `host\LmStreaming.Sample.exe` with `state\config\host.env`, then warms the gateway catalog on `-GatewayPort` (the watchdog passes `gatewayPort`).
- `reviewd.cmd` (GitHub): calls the CLI with no config. For quick manual use.
- `alarm.py` (GitHub): checks both installs and sends a HITL Notify when one has stopped reviewing. It can stop a wedged batch. `--dry-run` only prints. Exits 1 when a due alert could not be sent. It reads the `mcpServers.hitl` entry (`command`, `args`, `env`) from `~/.claude.json` of the user the task runs as.
- `judge-tick.ps1` and `judge-pending-closes.py` (ADO): judge merged PRs' reviews within a daily cap, then file the final judgments. `state\judge\judge.json` needs ten keys: `execute`, `provider`, `effort`, `maxUsd`, `maxUsdPerDay`, `avgUsdTarget`, `maxAgeDays`, `close`, `store`, `closeLimit`. A failed close goes to `state\judge\close-failed.tsv` and is skipped until its line is removed; nothing alerts on it.
- `rcpt.py`, `stages.py`, `bstate.py`, `watch-stages.sh` (GitHub): ad-hoc diagnostics. They need the WSL distro `Ubuntu-24.04` with `B:` mounted at `/mnt/b`. Nothing schedules them.
- `dashboard/build.py`: builds `reviewd.html` from both installs' `reviews.db` and `runner.json`, read-only.
- `runner.example.json`: each install's `state\config\runner.json`. Replace `session` and `workspace` with that install's gateway session and workspace ids before use.

The gateway port is 3000. `gatewayPort` in `runner.json`, the gateway URL in `host.env` and `SandboxGateway:BaseUrl` in `appsettings.reviewd.json` must name the same port.

Automatic restarts by the watchdog and the alarm reuse slots and do not run the all-slot reset: an operator-approved exception to the restart policy in `CLAUDE.md` and `../README.md`. The batch's slot reaper frees lapsed leases. Operator-initiated restarts still need the all-slot reset.

## Scheduled tasks
Checked with `Get-ScheduledTask` on 2026-11-09. Each runs as the signed-in user, with no window, and keeps no stderr.

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
2. Copy the new file over the live one. Scripts a task runs fresh (watchdog, discovery, alarm, judge, dashboard) pick it up on their next run.
3. Copying does not change a registered task: re-run `register-tasks.ps1` (or re-register a hand-made task) after changing a task's action or interval.
4. Copying does not reload a running process: the host, webhook and batch keep their launch arguments and `reviewd.ps1` until a controlled restart.
5. Commit the same change here, so this folder keeps matching the live copy.

Not here: `cli\`, `host\` and `kit\` (build output), `state\` (database, logs, selection) and the config files that hold or may hold secrets (`host.env`, `cli.env`, `secrets.env`, `appsettings.reviewd.json`).
