"""Windows runner ops scripts (ops/github, ops/ado). All state is synthetic and temporary; no live install is read.

Run: python samples/Reviewd.Sample/ops/test/test_windows_runner_ops.py -v
The alarm tests run anywhere. The PowerShell tests need Windows, pwsh and the .NET Framework csc.exe (for a stand-in
CLI that prints its arguments).
"""

import datetime
import importlib.util
import json
import os
import re
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

OPS = Path(__file__).resolve().parents[1]
CSC = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
WINDOWS = os.name == "nt" and shutil.which("pwsh") is not None
NOW = datetime.datetime(2026, 11, 10, 12, 0, tzinfo=datetime.timezone.utc)


def load_alarm():
    spec = importlib.util.spec_from_file_location("reviewd_alarm", OPS / "github" / "alarm.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def make_install(root, name="Reviewd", runner=None):
    install = Path(root) / name
    config = install / "state" / "config"
    config.mkdir(parents=True)
    (install / "state" / "logs").mkdir()
    (config / "runner.json").write_text(json.dumps(runner or {"maxAgeHours": 168, "repos": ["o/r"],
                                                              "workspace": "w"}), encoding="utf-8")
    with sqlite3.connect(install / "state" / "reviews.db") as db:
        db.execute("create table cache (key text primary key, value text)")
    return install


def quote(path):
    return "'" + str(path).replace("'", "''") + "'"


def completed(args, stdout="", returncode=0):
    return subprocess.CompletedProcess(args, returncode, stdout=stdout, stderr="")


class AlarmTests(unittest.TestCase):
    def setUp(self):
        self.alarm = load_alarm()
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)
        self.install = make_install(self.root)

    def test_owned_slot_without_lease_expiry_counts_as_working(self):
        slots = json.dumps({"state": "owned", "slot": 1, "prId": "7"}) + "\n" + json.dumps({"state": "idle", "slot": 2})
        with mock.patch.object(self.alarm.subprocess, "run", return_value=completed([], slots)):
            self.assertTrue(self.alarm.working(self.install, NOW))

    def test_owned_slot_with_lapsed_lease_counts_as_idle(self):
        lapsed = json.dumps({"state": "owned", "slot": 1, "leaseExpiresAt": (NOW - datetime.timedelta(minutes=1)).isoformat()})
        with mock.patch.object(self.alarm.subprocess, "run", return_value=completed([], lapsed)):
            self.assertFalse(self.alarm.working(self.install, NOW))

    def test_stall_restart_never_kills_while_an_undated_owner_holds_a_slot(self):
        calls = []

        def run(args, **_):
            calls.append(args)
            return completed(args, json.dumps({"state": "owned", "slot": 0}))

        with mock.patch.object(self.alarm.subprocess, "run", side_effect=run), \
                mock.patch.object(self.alarm, "batch_processes", return_value=[(11, 10, NOW - datetime.timedelta(hours=5))]):
            line = self.alarm.restart_stalled(self.install, "GitHub", {}, [datetime.timedelta(hours=3)], NOW, {}, False)
        self.assertIsNone(line)
        self.assertFalse([c for c in calls if c and c[0] == "taskkill"], calls)

    def test_failed_kill_raises_and_records_no_restart(self):
        memory = {}

        def run(args, **_):
            if args[0] == "taskkill":
                return subprocess.CompletedProcess(args, 1, stdout="", stderr="ERROR: Access is denied.")
            return completed(args, "pwsh -File B:\\x\\reviewd.ps1")

        with mock.patch.object(self.alarm, "working", return_value=False), \
                mock.patch.object(self.alarm, "batch_processes", return_value=[(11, 10, NOW - datetime.timedelta(hours=5))]), \
                mock.patch.object(self.alarm.subprocess, "run", side_effect=run):
            with self.assertRaisesRegex(RuntimeError, "Access is denied"):
                self.alarm.restart_stalled(self.install, "GitHub", {}, [datetime.timedelta(hours=3)], NOW, memory, False)
        self.assertNotIn("GitHub", memory.get("restarted", {}))

    def test_failed_restart_keeps_one_key_across_runs(self):
        keys = set()
        with mock.patch.object(self.alarm, "restart_stalled", side_effect=RuntimeError("kill failed")), \
                mock.patch.object(self.alarm, "LOG", Path(self.root) / "alarm.log"):
            for minutes in (0, 15, 30):
                found = self.alarm.install_problems(self.install, NOW + datetime.timedelta(minutes=minutes), {})
                keys |= {key for key, *_ in found if "restart" in key}
        self.assertEqual({"GitHub:restart-failed"}, keys)

    HELD_HEAD = "a" * 40
    HELD_OWNER = {"state": "owned", "claimToken": "t1", "repository": "o/r", "prId": "100", "slot": 2,
                  "leaseExpiresAt": (NOW - datetime.timedelta(minutes=45)).isoformat()}
    HELD_ATTEMPT = {"claimToken": "t1", "stage": "commit-pending", "headSha": HELD_HEAD,
                    "verifiedPublication": {"comments": 3}}

    def seed_held(self, owner, attempt, snapshot_head=HELD_HEAD):
        """One owned slot, its attempt and the PR's snapshot, which still waits for a review of its head."""
        rows = {"reviewd/slot-owner/abc": owner, "reviewd/review-attempt/o/r/100": attempt,
                "reviewd/pr-snapshot/o/r": {"100": {
                    "Lifecycle": "Open", "IsDraft": False, "HeadSha": snapshot_head,
                    "CreatedAt": (NOW - datetime.timedelta(days=1)).isoformat(),
                    "LastChangedAt": (NOW - datetime.timedelta(hours=3)).isoformat()}}}
        with sqlite3.connect(self.install / "state" / "reviews.db") as db:
            db.execute("delete from cache")
            db.executemany("insert into cache values (?, ?)", [(k, json.dumps(v)) for k, v in rows.items()])

    def held_problems(self, owner=HELD_OWNER, attempt=HELD_ATTEMPT, snapshot_head=HELD_HEAD):
        self.seed_held(owner, attempt, snapshot_head)
        with mock.patch.object(self.alarm, "restart_stalled", return_value=None):
            found = self.alarm.install_problems(self.install, NOW, {})
        return [(key, kind, detail) for key, _, kind, detail in found if kind in ("held", "stuck", "unreviewed")]

    def test_held_verified_review_is_told_once_as_held_whatever_the_head(self):
        # The reaper never resets a verified review whose archive commit has not completed (SlotReaper
        # .HoldsUnarchivedReview). The alarm tells that slot as held on every run, keyed by slot and claim, so a push
        # that moves the head does not hide it, and the PR is not told again as stuck or unreviewed.
        owner, attempt, problems = self.HELD_OWNER, self.HELD_ATTEMPT, self.held_problems

        held = problems()
        self.assertEqual([("GitHub:held:abc:t1", "held")], [(key, kind) for key, kind, _ in held])
        self.assertIn("o/r #100 slot 2", held[0][2])
        self.assertIn("commit-pending", held[0][2])
        self.assertEqual(held, problems(snapshot_head="b" * 40), "a new push does not hide the held slot")
        # verify-completed is saved before the verify result, so it counts as verified on its own.
        self.assertEqual(["held"], [kind for _, kind, _ in problems(attempt={"claimToken": "t1",
                                                                             "stage": "verify-completed"})])
        # Not held: never verified (the reaper resets it), a live lease, archived, or a slot already released.
        for name, changed_owner, changed_attempt in [
                ("unverified", owner, {"claimToken": "t1", "stage": "verify-pending"}),
                ("live lease", dict(owner, leaseExpiresAt=(NOW + datetime.timedelta(minutes=5)).isoformat()), attempt),
                ("archived", owner, dict(attempt, stage="completed-verified")),
                ("released", dict(owner, state="released"), attempt)]:
            with self.subTest(name):
                self.assertNotIn("held", [kind for _, kind, _ in problems(changed_owner, changed_attempt)])

    def test_each_verified_signal_alone_holds_the_slot_once(self):
        # Either guard alone is a verified review: a verified publication at a stage outside the verified set (here
        # verify-pending, a commit whose response was lost), or a verified stage with no publication recorded. Each
        # yields exactly one held alert, and the PR is not also told as stuck or unreviewed.
        for name, attempt in [
                ("publication only", {"claimToken": "t1", "stage": "verify-pending", "verifiedPublication": {"c": 1}}),
                ("commit-pending only", {"claimToken": "t1", "stage": "commit-pending"}),
                ("commit-completed only", {"claimToken": "t1", "stage": "commit-completed"})]:
            with self.subTest(name):
                self.assertEqual([("GitHub:held:abc:t1", "held")],
                                 [(key, kind) for key, kind, _ in self.held_problems(attempt=attempt)])

    def test_held_edges_match_the_reaper(self):
        # The reaper reclaims an owner whose lease expiry is at or before now (SlotRelease.IsLeaseExpired), and one with
        # no expiry only under --include-undated, so neither the alarm. An empty stage is no verified stage. A new
        # attempt head under the same claim keeps the same key, so it is not a new alert.
        def kinds(**change):
            return [kind for _, kind, _ in self.held_problems(**change)]

        self.assertEqual(["held"], kinds(owner=dict(self.HELD_OWNER, leaseExpiresAt=NOW.isoformat())))
        undated = {k: v for k, v in self.HELD_OWNER.items() if k != "leaseExpiresAt"}
        self.assertNotIn("held", kinds(owner=undated))
        self.assertNotIn("held", kinds(attempt={"claimToken": "t1", "stage": ""}))
        moved = self.held_problems(attempt=dict(self.HELD_ATTEMPT, headSha="c" * 40))
        self.assertEqual(["GitHub:held:abc:t1"], [key for key, _, _ in moved])

    def test_held_slot_is_told_again_only_after_24_hours_across_alarm_processes(self):
        # Each alarm run is a fresh process that knows what it already told only from its state file. A held slot is
        # not told again within REALERT and is told again after it (a stuck archive is told once).
        self.seed_held(self.HELD_OWNER, self.HELD_ATTEMPT)
        state_dir = Path(self.root) / "alarm-state"
        state_dir.mkdir()
        sent = []

        def held_told_at(now):
            alarm = load_alarm()

            class Clock(datetime.datetime):
                @classmethod
                def now(cls, tz=None):
                    return now

            clock = mock.Mock(wraps=datetime)
            clock.datetime = Clock
            told = len(sent)
            with mock.patch.object(alarm, "INSTALLS", [self.install]), \
                    mock.patch.object(alarm, "STATE", state_dir / "alarm-state.json"), \
                    mock.patch.object(alarm, "LOG", state_dir / "alarm.log"), \
                    mock.patch.object(alarm, "restart_stalled", return_value=None), \
                    mock.patch.object(alarm, "datetime", clock), \
                    mock.patch.object(alarm, "notify", side_effect=lambda title, body: sent.append(body) or True):
                self.assertEqual(0, alarm.main([]))
            return any("held:" in body for body in sent[told:])

        self.assertTrue(held_told_at(NOW))
        self.assertFalse(held_told_at(NOW + datetime.timedelta(hours=1)), "told again within 24 h")
        self.assertTrue(held_told_at(NOW + datetime.timedelta(hours=25)), "not told again after 24 h")

    def test_batch_that_fails_at_every_start_alerts_down_after_15_minutes(self):
        # "started" is what the watchdog wrote before the fix; false is what it writes now. Both must alert.
        for written in ("started", False):
            with self.subTest(batch=written):
                memory, found = {}, []
                for minutes in (0, 5, 10, 15):
                    now = NOW + datetime.timedelta(minutes=minutes)
                    (self.install / "state" / "status.json").write_text(json.dumps(
                        {"checkedAt": now.isoformat(), "paused": False, "gateway": True, "host": True,
                         "webhook": True, "batch": written}), encoding="utf-8")
                    found = self.alarm.install_problems(self.install, now, memory)
                self.assertIn("GitHub:down:batch", [key for key, *_ in found])

    def test_unreadable_runner_config_names_the_config_file(self):
        (self.install / "state" / "config" / "runner.json").write_text("{", encoding="utf-8")
        found = self.alarm.install_problems(self.install, NOW, {})
        detail = [d for key, _, _, d in found if key == "GitHub:unreadable-config"]
        self.assertTrue(detail and "runner.json" in detail[0], found)

    def test_run_exits_nonzero_when_a_due_alert_is_not_sent(self):
        state_dir = Path(self.root) / "alarm-state"
        with mock.patch.object(self.alarm, "INSTALLS", [self.install]), \
                mock.patch.object(self.alarm, "STATE", state_dir / "alarm-state.json"), \
                mock.patch.object(self.alarm, "LOG", state_dir / "alarm.log"), \
                mock.patch.object(self.alarm, "notify", side_effect=OSError("no HITL server")):
            state_dir.mkdir()
            self.assertEqual(1, self.alarm.main([]))  # no status.json: the watchdog alert is due
            self.assertTrue((state_dir / "alarm-state.json").exists())


@unittest.skipUnless(WINDOWS and CSC.exists(), "Requires Windows, PowerShell 7 and the .NET Framework csc.exe")
class PowerShellTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tools = Path(tempfile.mkdtemp())
        source = cls.tools / "echo.cs"
        source.write_text("class P { static int Main(string[] a) { foreach (var x in a) System.Console.WriteLine(x); "
                          "return 0; } }", encoding="utf-8")
        cls.echo = cls.tools / "Reviewd.Sample.exe"
        subprocess.run([str(CSC), "/nologo", f"/out:{cls.echo}", str(source)], check=True, capture_output=True)
        # A python3 that fails like an unreadable database, for judge-tick's pending-close read.
        (cls.tools / "python3.cmd").write_text("@echo database is locked 1>&2\r\n@exit /b 3\r\n", encoding="ascii")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tools, True)

    def setUp(self):
        # A space in the root: every path argument a script passes on must survive it.
        # resolve(): the long path, as $PSScriptRoot reports it, not the 8.3 name TEMP may hold.
        self.root = Path(tempfile.mkdtemp()).resolve() / "runner root"
        self.addCleanup(shutil.rmtree, self.root.parent, True)

    def install(self, flavor, runner, cli=False, name=None):
        root = self.root / (name or flavor)
        (root / "state" / "config").mkdir(parents=True)
        for script in (OPS / flavor).iterdir():
            if script.is_file():
                shutil.copy(script, root / script.name)
        if runner is not None:
            (root / "state" / "config" / "runner.json").write_text(json.dumps(runner), encoding="utf-8")
        if cli:
            (root / "cli").mkdir()
            shutil.copy(self.echo, root / "cli" / "Reviewd.Sample.exe")
            (root / "state" / "config" / "cli.env").write_text("", encoding="utf-8")
        return root

    def pwsh(self, command, env=None):
        return subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", command], capture_output=True,
                              text=True, timeout=120, env=env)

    def test_child_config_after_separator_keeps_the_runner_config(self):
        for flavor in ("github", "ado"):
            with self.subTest(flavor=flavor):
                root = self.install(flavor, None, cli=True)
                script = str(root / "reviewd.ps1").replace("'", "''")
                result = self.pwsh(f"& '{script}' sandbox exec --repo o/r '--' python3 --config child.json")
                ours = str(root / "state" / "config" / "appsettings.reviewd.json")
                self.assertEqual(["sandbox", "exec", "--config", ours, "--repo", "o/r", "--", "python3", "--config",
                                  "child.json"], result.stdout.splitlines(), result.stderr)

    def test_own_config_before_separator_is_not_doubled(self):
        root = self.install("github", None, cli=True)
        script = str(root / "reviewd.ps1").replace("'", "''")
        result = self.pwsh(f"& '{script}' pr check --config mine.json --repos o/r")
        self.assertEqual(["pr", "check", "--config", "mine.json", "--repos", "o/r"], result.stdout.splitlines())

    def test_missing_or_bad_runner_config_is_logged_and_fails(self):
        # One pwsh for every case: a script's exit returns to the caller with $LASTEXITCODE set.
        cases = [("ensure-reviewd.ps1", "ensure.log", None, "runner.json"),
                 ("discover.ps1", "discover.log", None, "runner.json"),
                 ("discover.ps1", "discover.log", {"lanesPerRepo": 7, "repos": ["o/r"]}, "from 1 to 6")]
        runs = [(flavor, i, *case) for flavor in ("github", "ado") for i, case in enumerate(cases)]
        roots = [self.install(flavor, case[2], name=f"{flavor}-{i}") for flavor, i, *case in runs]
        command = "; ".join(f"try {{ & {quote(root / case[0])} *> $null; \"rc=$LASTEXITCODE\" }} catch {{ \"rc=threw\" }}"
                            for root, (_, _, *case) in zip(roots, runs))
        codes = self.pwsh(command).stdout.split()
        self.assertEqual(len(runs), len(codes), codes)
        for root, (flavor, _, script, log, _, expected), code in zip(roots, runs, codes):
            with self.subTest(flavor=flavor, script=script, expected=expected):
                self.assertEqual("rc=1", code)
                text = (root / "state" / "logs" / log).read_text(encoding="utf-8")
                self.assertIn("FAILED", text)
                self.assertIn(expected, text)
                self.assertFalse((root / "state" / "selection.json").exists())

    def test_batch_that_fails_at_every_start_is_never_reported_up(self):
        listeners = []
        for _ in range(3):
            listener = socket.socket()
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            listeners.append(listener)
            self.addCleanup(listener.close)
        gateway, host, webhook = (s.getsockname()[1] for s in listeners)
        roots = []
        for flavor in ("github", "ado"):
            # No cli\: every batch launch fails at once, as a broken deploy would.
            root = self.install(flavor, {"gatewayPort": gateway, "hostPort": host, "webhookPort": webhook,
                                         "batchPort": 1, "session": "s", "workspace": "w", "provider": "p",
                                         "mode": "m", "reasoningEffort": "low", "maxAgeHours": 168})
            (root / "state" / "selection.json").write_text("{}", encoding="utf-8")
            roots.append(root)
        # Two watchdog runs per install, one pwsh per install, both installs at once: each run spends seconds in
        # its port and process queries. The failed launch holds batch.*.log until it exits, and the next run
        # rotates those logs, so wait for it between runs.
        watchdogs = [subprocess.Popen(
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command",
             f"$root = {quote(root)}; foreach ($run in 1, 2) {{ "
             "& (Join-Path $root 'ensure-reviewd.ps1') *> $null; $rc = $LASTEXITCODE; "
             "$status = Get-Content -Raw (Join-Path $root 'state\\status.json') | ConvertFrom-Json; "
             "$log = Get-Content -Raw (Join-Path $root 'state\\logs\\ensure.log'); "
             "$child = [regex]::Matches($log, 'started batch \\(pid (\\d+)\\)') | Select-Object -Last 1; "
             "if ($child) { Wait-Process -Id $child.Groups[1].Value -Timeout 60 -ErrorAction SilentlyContinue }; "
             "[ordered]@{ rc = $rc; batch = $status.batch; startedAt = [bool]$status.batchStartedAt; "
             "starts = [regex]::Matches($log, 'started batch').Count } | ConvertTo-Json -Compress }"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) for root in roots]
        for flavor, watchdog in zip(("github", "ado"), watchdogs):
            out, err = watchdog.communicate(timeout=180)
            rows = [json.loads(line) for line in out.splitlines() if line.startswith("{")]
            with self.subTest(flavor=flavor):
                self.assertEqual([{"rc": 0, "batch": False, "startedAt": True, "starts": run} for run in (1, 2)], rows,
                                 out + err)
    def test_failed_pending_close_read_is_logged_and_fails_the_tick(self):
        root = self.install("ado", {"session": "s", "workspace": "w"}, cli=True)
        judge = root / "state" / "judge"
        judge.mkdir()
        (judge / "judge.json").write_text(json.dumps({
            "execute": False, "provider": "p", "effort": "low", "maxUsd": 1, "maxUsdPerDay": 1, "avgUsdTarget": 1,
            "maxAgeDays": 1, "close": True, "store": str(self.root / "store"), "closeLimit": 1}), encoding="utf-8")
        env = dict(os.environ, PATH=f"{self.tools}{os.pathsep}{os.environ['PATH']}")
        result = self.pwsh(f"& '{str(root / 'judge-tick.ps1').replace(chr(39), chr(39) * 2)}'; exit $LASTEXITCODE", env)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("pending-close read FAILED (exit 3)", (judge / "judge-tick.log").read_text(encoding="utf-8"))
        self.assertIn("database is locked", (judge / "pending-closes.err.log").read_text(encoding="utf-8", errors="replace"))
        self.assertFalse((judge / "close.log").exists())


if __name__ == "__main__":
    sys.exit(unittest.main())
