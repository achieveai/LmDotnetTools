import fcntl
import importlib.util
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release", HERE / "release.py")
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)


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
    def start_host(self, release): self.calls.append("host-start")
    def canary_host(self, identity): self.calls.append("host-canary")
    def start_daemon_held(self, release): self.calls.append("daemon-held")
    def handshake(self, identity): self.calls.append("handshake")
    def enable_admission(self, release_id): self.calls.append("activate")
    def stabilize(self, release_id): self.calls.append("stabilize")
    def rollback(self, previous, backup): self.calls.append("rollback")
    def health(self, component): return self.health_rows.get(component, {})


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
        self.assertEqual(["backup", "migration-copy", "drain", "host-start", "host-canary", "daemon-held", "handshake", "activate", "stabilize"], adapter.calls)
        self.assertEqual(release_id, (root / "pointers" / "active").read_text().strip())
        events = [row["event"] for row in r._events(root)]
        self.assertLess(events.index("handshake"), events.index("pointers-updated"))

    def test_every_activation_crash_point_rolls_back_and_replay_is_clean(self):
        steps = ["database-gated", "drained", "host-canary", "daemon-held", "handshake", "pointers-updated", "admission-active", "stabilized"]
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


if __name__ == "__main__":
    unittest.main()
