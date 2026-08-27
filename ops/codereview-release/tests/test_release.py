import contextlib
import fcntl
import importlib.util
import json
import os
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release", HERE / "release.py")
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)


def listen(port: int = 0) -> socket.socket:
    """Bind a real listener. SO_REUSEADDR is set but SO_REUSEPORT is not, so a second
    bind while a live listener owns the port fails with EADDRINUSE exactly as in production."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", port))
    server.listen(8)
    return server


PORT_HOLDER = (
    "import socket,sys,time\n"
    "s=socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)\n"
    "s.bind(('127.0.0.1', int(sys.argv[1]))); s.listen(8)\n"
    "sys.stdout.write('bound\\n'); sys.stdout.flush()\n"
    "time.sleep(300)\n"
)


class FakeRunner:
    def __init__(self):
        self.commands = []

    def run(self, args, cwd, env=None, timeout=None):
        self.commands.append(args)
        candidate = cwd.parent
        if args[:2] == ["dotnet", "publish"]:
            output = Path(args[args.index("-o") + 1])
            output.mkdir(parents=True, exist_ok=True)
            executable = "LmStreaming.Sample" if "LmStreaming.Sample" in args[2] else "CodeReviewDaemon.Sample"
            path = output / executable
            path.write_text("#!/bin/sh\nexit 0\n")
            path.chmod(0o755)
        if args[:2] == ["dotnet", "test"]:
            trx = candidate / "trx" / f"{Path(args[2]).stem}.trx"
            trx.write_text(
                '<TestRun><ResultSummary><Counters total="1" executed="1" passed="1" '
                'failed="0" error="0" timeout="0" aborted="0" /></ResultSummary></TestRun>'
            )
        stdout = ""
        if "--verify-release" in args:
            component = "host" if "LmStreaming.Sample" in args[0] else "daemon"
            manifest = json.loads(Path(args[-1]).read_text())
            stdout = json.dumps(
                {
                    "status": "verified",
                    "component": component,
                    "releaseId": manifest["identity"]["releaseId"],
                    "sourceContentSha256": manifest["identity"]["sourceContentSha256"],
                    "manifestFormatVersion": manifest["formatVersion"],
                }
            )
        return {
            "args": args,
            "commandSha256": r.hashlib.sha256(r._json(args)).hexdigest(),
            "startedAtUtc": "start",
            "finishedAtUtc": "finish",
            "exitCode": 0,
            "stdoutSha256": r.hashlib.sha256(stdout.encode()).hexdigest(),
            "stderrSha256": r.hashlib.sha256(b"").hexdigest(),
            "stdout": stdout,
            "stderr": "",
        }


class RecordingAdapter(r.ActivationAdapter):
    def __init__(self):
        self.calls = []
        self.health_rows = {}

    def backup_database(self, release_id): self.calls.append("backup"); return "backup.db"
    def migration_copy_gate(self, release_id): self.calls.append("migration-copy")
    def drain(self, previous): self.calls.append("drain")
    def stop_incumbent(self, previous): self.calls.append("stop-incumbent")
    def start_host(self, release): self.calls.append("host-start")
    def canary_host(self, identity): self.calls.append("host-canary")
    def start_daemon_held(self, release): self.calls.append("daemon-held")
    def handshake(self, identity): self.calls.append("handshake")
    def enable_admission(self, release_id): self.calls.append("activate")
    def stabilize(self, release_id): self.calls.append("stabilize")
    def rollback(self, previous, backup): self.calls.append("rollback")
    def health(self, component): return self.health_rows.get(component, {})


class FakeUnitSupervisor:
    """A systemd-shaped incumbent. It owns both listener ports until deliberately stopped,
    and a supervised (re)start always comes up admitting because it is a fresh process."""

    def __init__(self):
        self.listeners = {component: listen() for component in ("host", "daemon")}
        self.ports = {component: server.getsockname()[1] for component, server in self.listeners.items()}
        self.admission = "active"
        self.transitions = []

    def stop(self):
        for server in self.listeners.values():
            server.close()
        self.listeners.clear()
        self.transitions.append("stop")

    def start(self):
        for component in ("host", "daemon"):
            if component not in self.listeners:
                self.listeners[component] = listen(self.ports[component])
        self.admission = "active"
        self.transitions.append("start")

    def owns(self, component):
        return component in self.listeners

    def close(self):
        self.stop()


class SupervisedAdapter(r.ActivationAdapter):
    """Activation against a supervised incumbent. The candidate binds the very ports the
    incumbent owns, so a missing ownership transfer fails the way production fails."""

    def __init__(self, supervisor):
        self.supervisor = supervisor
        self.calls = []
        self.candidate = {}
        self.incumbent_owned_at_start = {}
        self.health_rows = {}

    def backup_database(self, release_id): self.calls.append("backup"); return "backup.db"
    def migration_copy_gate(self, release_id): self.calls.append("migration-copy")

    def drain(self, previous):
        self.calls.append("drain")
        if previous:
            self.supervisor.admission = "drained"

    def stop_incumbent(self, previous):
        self.calls.append("stop-incumbent")
        self.supervisor.stop()

    def _bind_candidate(self, component):
        self.incumbent_owned_at_start[component] = self.supervisor.owns(component)
        self.candidate[component] = listen(self.supervisor.ports[component])

    def start_host(self, release): self.calls.append("host-start"); self._bind_candidate("host")
    def canary_host(self, identity): self.calls.append("host-canary")

    def start_daemon_held(self, release):
        self.calls.append("daemon-held")
        self._bind_candidate("daemon")
        self.supervisor.admission = "held"

    def handshake(self, identity): self.calls.append("handshake")

    def enable_admission(self, release_id):
        self.calls.append("activate")
        self.supervisor.admission = "active"

    def stabilize(self, release_id): self.calls.append("stabilize")

    def rollback(self, previous, backup):
        """The contract activate() must be able to rely on: give the ports back and re-admit."""
        self.calls.append("rollback")
        for server in self.candidate.values():
            server.close()
        self.candidate.clear()
        if previous:
            self.supervisor.start()

    def health(self, component): return self.health_rows.get(component, {})

    def close(self):
        for server in self.candidate.values():
            server.close()
        self.candidate.clear()


class FakeSystemctl:
    """Command boundary for LocalActivationAdapter: models `systemctl start/stop <unit>`."""

    def __init__(self, supervisor):
        self.supervisor = supervisor
        self.commands = []

    def run(self, args, cwd, env=None, timeout=None):
        self.commands.append(args)
        if args and args[0] == "systemctl":
            verb = args[-2]
            if verb == "stop":
                self.supervisor.stop()
            elif verb == "start":
                self.supervisor.start()
        return {"args": args, "exitCode": 0, "stdout": "", "stderr": ""}

    def verbs(self):
        return [args[-2] for args in self.commands if args and args[0] == "systemctl"]

    def units(self):
        return [args[-1] for args in self.commands if args and args[0] == "systemctl"]


class ControlServer:
    """Stand-in for the daemon control socket; records the admission commands it receives."""

    def __init__(self, path, supervisor=None):
        self.commands = []
        self.supervisor = supervisor
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.server.bind(str(path))
        self.server.listen(8)
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self):
        while True:
            try:
                connection, _ = self.server.accept()
            except OSError:
                return
            with connection:
                command = connection.recv(128).decode().strip()
                if command:
                    self.commands.append(command)
                    if self.supervisor is not None:
                        self.supervisor.admission = {"drain": "drained", "activate": "active"}.get(
                            command, self.supervisor.admission
                        )
                connection.sendall(b"ok\n")

    def close(self):
        self.server.close()


class ReleaseTests(unittest.TestCase):
    def git_repo(self):
        directory = Path(tempfile.mkdtemp())
        subprocess.run(["git", "init", "-q", directory], check=True)
        subprocess.run(["git", "-C", directory, "config", "user.email", "test@example.test"], check=True)
        subprocess.run(["git", "-C", directory, "config", "user.name", "Test"], check=True)
        (directory / "a.txt").write_text("one\n")
        subprocess.run(["git", "-C", directory, "add", "."], check=True)
        subprocess.run(["git", "-C", directory, "commit", "-qm", "one"], check=True)
        return directory

    def test_clean_snapshot_is_deterministic_and_symlinks_are_rejected(self):
        repo = self.git_repo()
        a, b = Path(tempfile.mkdtemp()) / "a", Path(tempfile.mkdtemp()) / "b"
        self.assertEqual(r.snapshot(repo, a, False)["sourceContentSha256"], r.snapshot(repo, b, False)["sourceContentSha256"])
        (repo / "link").symlink_to("a.txt")
        with self.assertRaisesRegex(RuntimeError, "symlink rejected"):
            r.snapshot(repo, Path(tempfile.mkdtemp()) / "bad", True)

    def test_dirty_snapshot_tracks_bytes_deletion_mode_and_untracked(self):
        repo = self.git_repo()
        hashes = []
        for mutate in [lambda: (repo / "a.txt").write_text("two"), lambda: (repo / "a.txt").unlink(), lambda: (repo / "new").write_text("x")]:
            subprocess.run(["git", "-C", repo, "reset", "--hard", "-q"], check=True)
            mutate()
            hashes.append(r.snapshot(repo, Path(tempfile.mkdtemp()) / "s", True)["sourceContentSha256"])
        subprocess.run(["git", "-C", repo, "reset", "--hard", "-q"], check=True)
        os.chmod(repo / "a.txt", 0o755)
        hashes.append(r.snapshot(repo, Path(tempfile.mkdtemp()) / "s", True)["sourceContentSha256"])
        self.assertEqual(len(hashes), len(set(hashes)))

    def verified_candidate(self):
        repo = self.git_repo()
        (repo / "LmDotnetTools.sln").write_text("")
        subprocess.run(["git", "-C", repo, "add", "."], check=True)
        subprocess.run(["git", "-C", repo, "commit", "-qm", "fixture"], check=True)
        candidate = Path(tempfile.mkdtemp()) / "candidate"
        r.prepare(repo, candidate, False)
        runner = FakeRunner()
        evidence = r.verify(candidate, HERE / "verification-policy.json", runner)
        return candidate, runner, evidence

    def test_policy_builds_all_tests_before_no_build_tests_and_smokes_published_bits(self):
        candidate, runner, evidence = self.verified_candidate()
        builds = [i for i, command in enumerate(runner.commands) if command[:2] == ["dotnet", "build"]]
        tests = [i for i, command in enumerate(runner.commands) if command[:2] == ["dotnet", "test"]]
        publishes = [i for i, command in enumerate(runner.commands) if command[:2] == ["dotnet", "publish"]]
        smokes = [command for command in runner.commands if "--verify-release" in command]
        self.assertEqual(3, len(builds)); self.assertEqual(3, len(tests)); self.assertLess(max(builds), min(tests))
        self.assertEqual(2, len(publishes)); self.assertEqual(2, len(smokes)); self.assertEqual(3, len(evidence["trx"]))
        self.assertTrue(all("--no-build" in runner.commands[i] for i in tests))
        self.assertTrue(all(Path(command[0]).is_relative_to(candidate / "stage") for command in smokes))

    def test_manifest_contract_rejects_version_field_casing_and_hash(self):
        state = {
            "releaseId": "release",
            "source": {
                "sourceContentSha256": "a" * 64,
                "baseCommit": "b" * 40,
                "sourceKind": "dirty-snapshot",
            },
        }
        stage = Path(tempfile.mkdtemp())
        (stage / "daemon").mkdir()
        (stage / "daemon" / "app").write_text("x")
        manifest = r._provisional_manifest(state, stage, "c" * 64)
        self.assertEqual("release", r.validate_manifest_contract(manifest)["releaseId"])
        mutations = []
        wrong_version = json.loads(json.dumps(manifest)); wrong_version["formatVersion"] = 1; mutations.append(wrong_version)
        wrong_field = json.loads(json.dumps(manifest)); wrong_field["identity"]["extra"] = 1; mutations.append(wrong_field)
        wrong_casing = json.loads(json.dumps(manifest)); wrong_casing["Identity"] = wrong_casing.pop("identity"); mutations.append(wrong_casing)
        wrong_hash = json.loads(json.dumps(manifest)); wrong_hash["artifacts"][0]["sha256"] = "A" * 64; mutations.append(wrong_hash)
        for mutated in mutations:
            with self.subTest(mutated=mutated):
                with self.assertRaises(RuntimeError):
                    r.validate_manifest_contract(mutated)

    def test_synthetic_passed_verification_is_refused(self):
        candidate, _, _ = self.verified_candidate()
        (candidate / "verification.json").write_text('{"status":"passed"}')
        with self.assertRaises(RuntimeError):
            r.publish_candidate(candidate, Path(tempfile.mkdtemp()))

    def test_stage_mutation_after_verify_is_refused(self):
        candidate, _, _ = self.verified_candidate()
        executable = candidate / "stage" / "host" / "LmStreaming.Sample"
        executable.chmod(0o755)
        executable.write_text("replacement")
        executable.chmod(0o555)
        with self.assertRaisesRegex(RuntimeError, "modified"):
            r.publish_candidate(candidate, Path(tempfile.mkdtemp()))

    def published(self):
        candidate, _, _ = self.verified_candidate()
        root = Path(tempfile.mkdtemp())
        final = r.publish_candidate(candidate, root)
        return root, final, final.name

    def test_publish_seals_entire_release_and_tested_bits_equal_published_hashes(self):
        candidate, _, _ = self.verified_candidate()
        stage_hashes = {
            p.relative_to(candidate / "stage").as_posix(): r.sha256(p)
            for p in r.validate_tree(candidate / "stage")
            if p.name != "manifest.json"
        }
        root = Path(tempfile.mkdtemp())
        final = r.publish_candidate(candidate, root)
        manifest = r.read_verified(root, final.name)
        published = {item["path"]: item["sha256"] for item in manifest["artifacts"]}
        self.assertEqual(stage_hashes, published)
        self.assertEqual(0, final.lstat().st_mode & 0o222)
        self.assertTrue(all(path.lstat().st_mode & 0o222 == 0 for path in final.rglob("*")))

    def test_verification_rejects_traversal_absolute_noncanonical_collisions_symlink_and_writable(self):
        root, final, release_id = self.published()
        os.chmod(final, 0o755); os.chmod(final / "manifest.json", 0o644)
        manifest = json.loads((final / "manifest.json").read_text())
        original = manifest["artifacts"][0]["path"]
        for bad in ["../outside", "/tmp/outside", "host/./app", "host\\app"]:
            with self.assertRaises(RuntimeError): r._canonical_relative(bad)
        manifest["artifacts"].append(dict(manifest["artifacts"][0]))
        (final / "manifest.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(RuntimeError, "writable|seal"):
            r.read_verified(root, release_id)
        manifest["artifacts"].pop(); (final / "manifest.json").write_text(json.dumps(manifest))
        target = final / original
        target.parent.chmod(0o755)
        target.unlink(); target.symlink_to("/etc/passwd")
        with self.assertRaisesRegex(RuntimeError, "symlink"):
            r.validate_tree(final)

    def test_interrupted_publish_exposes_no_release_or_pointer(self):
        candidate, _, _ = self.verified_candidate(); root = Path(tempfile.mkdtemp())
        with self.assertRaises(RuntimeError): r.publish_candidate(candidate, root, True)
        self.assertFalse((root / "pointers" / "latest-verified").exists())
        self.assertEqual([], [p for p in (root / "releases").iterdir() if not p.name.startswith(".")])

    def test_activation_runs_exact_order_and_updates_pointer_only_after_handshake(self):
        root, _, release_id = self.published(); adapter = RecordingAdapter()
        r.activate(root, release_id, adapter)
        self.assertEqual(["backup", "migration-copy", "drain", "stop-incumbent", "host-start", "host-canary", "daemon-held", "handshake", "activate", "stabilize"], adapter.calls)
        self.assertEqual(release_id, (root / "pointers" / "active").read_text().strip())
        events = [row["event"] for row in r._events(root)]
        self.assertLess(events.index("handshake"), events.index("pointers-updated"))
        self.assertLess(events.index("drained"), events.index("incumbent-stopped"))

    def test_every_activation_crash_point_rolls_back_and_replay_is_clean(self):
        steps = ["database-gated", "drained", "incumbent-stopped", "host-canary", "daemon-held", "handshake", "pointers-updated", "admission-active", "stabilized"]
        for step in steps:
            with self.subTest(step=step):
                root, _, release_id = self.published(); (root / "pointers" / "active").write_text("old\n")
                adapter = RecordingAdapter()
                with self.assertRaises(RuntimeError): r.activate(root, release_id, adapter, step)
                self.assertEqual("old", (root / "pointers" / "active").read_text().strip())
                self.assertEqual("clean", r.recover(root, adapter))

    def test_incomplete_journal_is_replayed_and_concurrent_activation_rejected(self):
        root, _, release_id = self.published(); (root / "pointers" / "active").write_text(release_id + "\n")
        r.journal(root, "begin", intended=release_id, previous="old")
        adapter = RecordingAdapter(); self.assertEqual("rolled_back", r.recover(root, adapter)); self.assertEqual("old", (root / "pointers" / "active").read_text().strip())
        lock = os.open(root / "activation.lock", os.O_RDWR | os.O_CREAT, 0o600)
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            with self.assertRaisesRegex(RuntimeError, "already in progress"): r.activate(root, release_id, adapter)
            with self.assertRaisesRegex(RuntimeError, "already in progress"): r.recover(root, adapter)
            with self.assertRaisesRegex(RuntimeError, "already in progress"): r.watchdog(root, adapter)
        finally: os.close(lock)

    def test_subprocess_sigkill_matrix_recovers_lock_pointer_journal_and_adapter_boundaries(self):
        boundaries = [
            "adapter:backup-database:before",
            "adapter:backup-database:after",
            "journal:database-backup-created",
            "adapter:migration-copy-gate:before",
            "adapter:migration-copy-gate:after",
            "journal:database-gated",
            "adapter:drain:before",
            "adapter:drain:after",
            "adapter:stop-incumbent:before",
            "adapter:stop-incumbent:after",
            "adapter:start-host:before",
            "adapter:start-host:after",
            "adapter:canary-host:before",
            "adapter:canary-host:after",
            "adapter:start-daemon-held:before",
            "adapter:start-daemon-held:after",
            "adapter:handshake:before",
            "adapter:handshake:after",
            "pointer:previous",
            "pointer:active",
            "adapter:enable-admission:before",
            "adapter:enable-admission:after",
            "adapter:stabilize:before",
            "adapter:stabilize:after",
        ]
        child = r'''import importlib.util, pathlib, sys
spec=importlib.util.spec_from_file_location("release_under_crash", sys.argv[1]); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
root=pathlib.Path(sys.argv[2]); release_id=sys.argv[3]
class A(m.ActivationAdapter):
 def backup_database(self, release_id):
  p=root/"backups"/(release_id+"-child.sqlite"); p.parent.mkdir(parents=True,exist_ok=True); p.write_bytes(b"backup"); return str(p)
 def migration_copy_gate(self, release_id): pass
 def drain(self, previous): pass
 def stop_incumbent(self, previous): pass
 def start_host(self, release): pass
 def canary_host(self, identity): pass
 def start_daemon_held(self, release): pass
 def handshake(self, identity): pass
 def enable_admission(self, release_id): pass
 def stabilize(self, release_id): pass
 def rollback(self, previous, backup):
  (root/"rollback-evidence").write_text(previous+"\n"+backup)
m.activate(root, release_id, A())'''
        for boundary in boundaries:
            with self.subTest(boundary=boundary):
                root, _, release_id = self.published()
                (root / "pointers" / "active").write_text("old\n")
                env = {**os.environ, "CODEREVIEW_RELEASE_TEST_SIGKILL": boundary}
                process = subprocess.run(
                    [sys.executable, "-c", child, str(HERE / "release.py"), str(root), release_id],
                    env=env,
                    timeout=15,
                    check=False,
                )
                self.assertEqual(-9, process.returncode)
                adapter = RecordingAdapter()
                outcome = r.recover(root, adapter)
                self.assertIn(outcome, ("clean", "rolled_back"))
                self.assertEqual("old", (root / "pointers" / "active").read_text().strip())
                self.assertEqual("clean", r.recover(root, adapter), "resume must be idempotent")
                with r.activation_lock(root):
                    pass

    def test_journal_allows_one_torn_tail_and_rejects_interior_corruption(self):
        root = Path(tempfile.mkdtemp())
        r.journal(root, "begin", intended="new", previous="old")
        with (root / "activation-journal.jsonl").open("ab") as stream:
            stream.write(b'{"event":"torn"')
        self.assertEqual(1, len(r._events(root)))
        with (root / "activation-journal.jsonl").open("ab") as stream:
            stream.write(b'\n{"event":"valid"}\n')
        with self.assertRaisesRegex(RuntimeError, "corrupt"):
            r._events(root)

    def test_watchdog_validates_both_health_identities_and_can_activate(self):
        root, _, release_id = self.published(); adapter = RecordingAdapter()
        adapter.health_rows = {"host": {"releaseId": "wrong", "ready": True}, "daemon": {"releaseId": "wrong", "ready": True}}
        self.assertEqual("running_manifest_mismatch", r.watchdog(root, adapter))
        adapter.health_rows = {"host": {}, "daemon": {}}
        self.assertEqual("activated", r.watchdog(root, adapter, True))
        self.assertIn("host-start", adapter.calls)

    def test_systemd_templates_use_atomic_pointer_distinct_ports_profile_and_no_bypass(self):
        systemd = HERE / "systemd"
        host = (systemd / "codereview-host.service").read_text(); daemon = (systemd / "codereview-daemon.service").read_text()
        for text in (host, daemon):
            self.assertIn("pointers/active", text); self.assertIn("Production", text); self.assertIn("CODEREVIEW_DEVELOPMENT_IDENTITY=", text)
            self.assertNotIn("releases/current", text)
        self.assertNotIn("CODEREVIEW_DAEMON_ADMISSION", daemon)
        self.assertIn("ControlSocketPath", daemon)
        self.assertIn("127.0.0.1:5080", host); self.assertIn("127.0.0.1:5081", daemon); self.assertIn("--review achieveai", daemon)

    # --- port ownership between the supervisor and the activator -------------------------

    def supervised_world(self):
        supervisor = FakeUnitSupervisor()
        self.addCleanup(supervisor.close)
        root = Path(tempfile.mkdtemp())
        (root / "review.db").write_bytes(b"live")
        control_path = root / "control.sock"
        control = ControlServer(control_path, supervisor)
        self.addCleanup(control.close)
        return supervisor, root, control_path, control

    def local_adapter(self, supervisor, root, control_path):
        runner = FakeSystemctl(supervisor)
        adapter = r.LocalActivationAdapter(
            root,
            root / "review.db",
            f"http://127.0.0.1:{supervisor.ports['host']}",
            f"http://127.0.0.1:{supervisor.ports['daemon']}",
            control_path,
            "achieveai",
            runner=runner,
        )
        return adapter, runner

    def holder_script(self, root):
        script = root / "port-holder"
        script.write_text("#!/usr/bin/env python3\n" + PORT_HOLDER)
        script.chmod(0o755)
        return script

    def start_candidate(self, adapter, component, script, port):
        adapter._start(component, script, [str(port)])
        process = adapter.processes[component]
        self.addCleanup(self.reap, process)
        self.wait_port(port, True)
        return process.pid

    def reap(self, process):
        with contextlib.suppress(ProcessLookupError, PermissionError):
            os.killpg(process.pid, 9)
        with contextlib.suppress(subprocess.TimeoutExpired):
            process.wait(timeout=5)

    def wait_port(self, port, occupied, timeout=15):
        deadline = time.monotonic() + timeout
        busy = None
        while time.monotonic() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    pass
                busy = True
            except OSError:
                busy = False
            if busy == occupied:
                return
            time.sleep(0.05)
        self.fail(f"port {port} busy={busy}, expected busy={occupied}")

    def assert_stopped(self, pid):
        try:
            state = (Path("/proc") / str(pid) / "stat").read_text().rsplit(") ", 1)[1].split()[0]
        except FileNotFoundError:
            return
        self.assertEqual("Z", state, f"candidate pid {pid} is still running")

    def test_activation_takes_port_ownership_from_the_supervised_incumbent_before_starting(self):
        root, _, release_id = self.published()
        (root / "pointers" / "active").write_text("old\n")
        supervisor = FakeUnitSupervisor(); self.addCleanup(supervisor.close)
        adapter = SupervisedAdapter(supervisor); self.addCleanup(adapter.close)
        r.activate(root, release_id, adapter)
        self.assertEqual(
            ["backup", "migration-copy", "drain", "stop-incumbent", "host-start", "host-canary", "daemon-held", "handshake", "activate", "stabilize"],
            adapter.calls,
        )
        self.assertEqual({"host": False, "daemon": False}, adapter.incumbent_owned_at_start)
        self.assertEqual(["stop"], supervisor.transitions)
        self.assertEqual("active", supervisor.admission)
        self.assertEqual(release_id, (root / "pointers" / "active").read_text().strip())
        events = [row["event"] for row in r._events(root)]
        self.assertLess(events.index("drained"), events.index("incumbent-stopped"))
        self.assertLess(events.index("incumbent-stopped"), events.index("host-canary"))

    def test_failure_after_drain_or_candidate_start_restores_previous_release_and_admission(self):
        for step in ["drained", "incumbent-stopped", "host-canary", "daemon-held", "handshake", "pointers-updated", "admission-active", "stabilized"]:
            with self.subTest(step=step):
                root, _, release_id = self.published()
                (root / "pointers" / "active").write_text("old\n")
                supervisor = FakeUnitSupervisor(); self.addCleanup(supervisor.close)
                adapter = SupervisedAdapter(supervisor); self.addCleanup(adapter.close)
                with self.assertRaises(RuntimeError) as raised:
                    r.activate(root, release_id, adapter, step)
                self.assertEqual(f"injected fault after {step}", str(raised.exception))
                self.assertEqual("old", (root / "pointers" / "active").read_text().strip())
                self.assertEqual("rollback", adapter.calls[-1])
                self.assertTrue(supervisor.owns("host"), "previous release must own the host port again")
                self.assertTrue(supervisor.owns("daemon"), "previous release must own the daemon port again")
                self.assertEqual("active", supervisor.admission, "rollback left admission drained")
                self.assertEqual({}, adapter.candidate)
                self.assertEqual("clean", r.recover(root, adapter))

    def test_stop_incumbent_releases_ports_from_the_supervisor_and_from_orphaned_candidates(self):
        supervisor, root, control_path, _ = self.supervised_world()
        adapter, runner = self.local_adapter(supervisor, root, control_path)
        script = self.holder_script(root)
        adapter.drain("old")
        adapter.stop_incumbent("old")
        self.assertEqual(["stop"], runner.verbs())
        self.assertEqual([r.SUPERVISOR_UNIT], runner.units())
        self.assertFalse(supervisor.owns("host")); self.assertFalse(supervisor.owns("daemon"))
        self.wait_port(supervisor.ports["host"], False)
        self.assertIn("incumbent-released", [row["event"] for row in r._events(root)])

        # A previous activation's unsupervised candidate is the other possible port owner.
        pid = self.start_candidate(adapter, "host", script, supervisor.ports["host"])
        adapter.processes.clear()
        next_adapter, next_runner = self.local_adapter(supervisor, root, control_path)
        next_adapter.stop_incumbent("old")
        self.assert_stopped(pid)
        self.wait_port(supervisor.ports["host"], False)
        self.assertEqual(["stop"], next_runner.verbs())

    def test_rollback_readmits_a_still_running_incumbent_that_was_only_drained(self):
        supervisor, root, control_path, control = self.supervised_world()
        adapter, runner = self.local_adapter(supervisor, root, control_path)
        backup = root / "backup.sqlite"; backup.write_bytes(b"previous")
        adapter.drain("old")
        self.assertEqual("drained", supervisor.admission)
        adapter.rollback("old", str(backup))
        self.assertEqual("active", supervisor.admission, "a drained incumbent was never re-admitted")
        self.assertEqual(["drain", "activate"], control.commands)
        self.assertTrue(supervisor.owns("host")); self.assertTrue(supervisor.owns("daemon"))
        self.assertEqual(b"previous", (root / "review.db").read_bytes())

    def test_rollback_stops_the_candidate_and_restarts_the_supervised_previous_release(self):
        supervisor, root, control_path, control = self.supervised_world()
        adapter, runner = self.local_adapter(supervisor, root, control_path)
        script = self.holder_script(root)
        adapter.drain("old")
        adapter.stop_incumbent("old")
        pid = self.start_candidate(adapter, "daemon", script, supervisor.ports["daemon"])
        adapter.rollback("old", "")
        self.assert_stopped(pid)
        self.assertEqual(["stop", "start"], runner.verbs())
        self.assertTrue(supervisor.owns("host")); self.assertTrue(supervisor.owns("daemon"))
        self.assertEqual("active", supervisor.admission)
        self.assertIn("activate", control.commands)

    def test_rollback_without_a_previous_release_starts_nothing(self):
        supervisor, root, control_path, control = self.supervised_world()
        adapter, runner = self.local_adapter(supervisor, root, control_path)
        adapter.rollback("", "")
        self.assertEqual([], runner.verbs())
        self.assertEqual([], control.commands)

    def test_watchdog_recovery_uses_the_same_ownership_and_admission_protocol(self):
        supervisor, root, control_path, control = self.supervised_world()
        crashed, _ = self.local_adapter(supervisor, root, control_path)
        script = self.holder_script(root)
        r.journal(root, "begin", intended="new", previous="old")
        crashed.drain("old")
        crashed.stop_incumbent("old")
        host_pid = self.start_candidate(crashed, "host", script, supervisor.ports["host"])
        daemon_pid = self.start_candidate(crashed, "daemon", script, supervisor.ports["daemon"])
        crashed.processes.clear()  # the activator died; nothing in-process knows these pids
        atomic = root / "pointers" / "active"
        atomic.parent.mkdir(parents=True, exist_ok=True)
        atomic.write_text("new\n")

        recovered, runner = self.local_adapter(supervisor, root, control_path)
        self.assertEqual("rolled_back", r.recover(root, recovered))
        self.assertEqual("old", atomic.read_text().strip())
        self.assert_stopped(host_pid); self.assert_stopped(daemon_pid)
        # The stop half was done by the activator that then died; recovery supplies the start half.
        self.assertEqual(["start"], runner.verbs())
        self.assertEqual([r.SUPERVISOR_UNIT], runner.units())
        self.assertTrue(supervisor.owns("host")); self.assertTrue(supervisor.owns("daemon"))
        self.assertEqual("active", supervisor.admission)
        self.assertIn("activate", control.commands)
        self.assertEqual("clean", r.recover(root, recovered))

    def short_handover_timeout(self, seconds=0.5):
        original = r.PORT_HANDOVER_TIMEOUT_SECONDS
        r.PORT_HANDOVER_TIMEOUT_SECONDS = seconds
        self.addCleanup(setattr, r, "PORT_HANDOVER_TIMEOUT_SECONDS", original)

    def test_port_handover_gate_requires_a_real_bind_not_a_refused_connect(self):
        supervisor, root, control_path, _ = self.supervised_world()
        adapter, runner = self.local_adapter(supervisor, root, control_path)
        supervisor.stop()
        host, port = adapter._address("host")

        # A socket that is bound but has not called listen() — a server mid-startup. It refuses
        # connections, so a connect-based gate calls the port free, yet the candidate's bind fails.
        squatter = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        squatter.bind((host, port))
        self.addCleanup(squatter.close)
        with self.assertRaises(OSError):
            socket.create_connection((host, port), timeout=1).close()
        with self.assertRaises(OSError) as bind_failure:
            blocked = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            blocked.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                blocked.bind((host, port))
            finally:
                blocked.close()
        self.assertEqual(98, bind_failure.exception.errno)

        self.short_handover_timeout()
        with self.assertRaisesRegex(RuntimeError, "still owned"):
            adapter._wait_port_bindable("host")
        with self.assertRaisesRegex(RuntimeError, "still owned"):
            adapter.stop_incumbent("old")

        # Positive control: once the address is genuinely released the same gate passes, and it
        # leaves the port bindable rather than holding it open itself.
        squatter.close()
        adapter._wait_port_bindable("host")
        released = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        released.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            released.bind((host, port))
            released.listen(1)
        finally:
            released.close()

    def test_stop_candidates_spares_an_unrelated_pid_and_clears_the_stale_record(self):
        supervisor, root, control_path, _ = self.supervised_world()
        adapter, _ = self.local_adapter(supervisor, root, control_path)
        bystander = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(300)"], start_new_session=True
        )
        self.addCleanup(self.reap, bystander)

        # The recorded pid was reused by an unrelated process, so the cmdline no longer matches.
        r._record_candidate(root, "daemon", bystander.pid, "/gone/releases/old/daemon/CodeReviewDaemon.Sample")
        record = root / r.CANDIDATE_PROCESSES
        self.assertTrue(record.exists())
        adapter._stop_candidates()
        time.sleep(0.5)
        self.assertIsNone(bystander.poll(), "an unrelated live process was signalled")
        self.assertFalse(record.exists(), "stale candidate metadata was not cleared")

        # Positive control: the same pid recorded with the executable it is actually running
        # must still be stopped, so the sparing above is discrimination and not inaction.
        r._record_candidate(root, "daemon", bystander.pid, sys.executable)
        adapter._stop_candidates()
        self.assert_stopped(bystander.pid)
        self.assertFalse(record.exists())

    def test_systemd_pair_target_is_the_ownership_handle_used_by_the_release_tool(self):
        systemd = HERE / "systemd"
        host = (systemd / "codereview-host.service").read_text()
        daemon = (systemd / "codereview-daemon.service").read_text()
        target = (systemd / "codereview-pair.target").read_text()
        self.assertEqual("codereview-pair.target", r.SUPERVISOR_UNIT)
        self.assertTrue((systemd / r.SUPERVISOR_UNIT).is_file())
        for text in (host, daemon):
            # PartOf is what makes `systemctl stop codereview-pair.target` actually release
            # the listeners; without it the release tool's ownership transfer is a no-op.
            self.assertIn("PartOf=codereview-pair.target", text)
            self.assertIn("WantedBy=codereview-pair.target", text)
        self.assertIn("WantedBy=default.target", target)
        self.assertIn("Requires=codereview-host.service codereview-daemon.service", target)


if __name__ == "__main__":
    unittest.main()
