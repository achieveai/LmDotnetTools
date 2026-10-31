"""Windows offline capture contract. All state is synthetic; no installed Reviewd data is read."""

import hashlib
import ctypes
from contextlib import closing
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timezone


SCRIPT = Path(__file__).resolve().parents[1] / "backup-offline-state.ps1"


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "Requires Windows and PowerShell 7")
class OfflineBackupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="reviewd-offline-")
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.databases = []
        for role in ("conversations", "identity", "github", "ado"):
            path = self.source / f"{role}.db"
            with sqlite3.connect(path) as db:
                db.execute("PRAGMA journal_mode=WAL")
                db.execute("CREATE TABLE evidence(value TEXT)")
                db.execute("INSERT INTO evidence VALUES ('saved')")
            db.close()
            self.databases.append({"name": role, "path": str(path)})
        config = self.source / "config"
        config.mkdir()
        (config / "empty").mkdir()
        (config / "settings.json").write_text('{"synthetic":"private"}', encoding="utf-8")
        self.scope = {
            "schema": 1,
            "databases": self.databases,
            "entries": [{"name": "config", "path": str(config)}],
            "runtime": {"processPaths": [], "commandPaths": [], "tasks": [], "ports": []},
        }
        self.scope_path = self.root / "scope.json"
        self.proof_path = self.root / "quiescence.json"
        self.destination = self.root / "capture"
        self.save_scope()

    def tearDown(self):
        self.temp.cleanup()

    def save_scope(self):
        self.scope_path.write_text(json.dumps(self.scope), encoding="utf-8")
        self.proof = {
            "schema": 1,
            "observedAtUtc": datetime.now(timezone.utc).isoformat(),
            "scopeSha256": digest(self.scope_path),
            "producersDisabled": True,
            "writersStopped": True,
            "shutdownDurabilityPassed": True,
            "unknownWriters": False,
            "owners": 0,
            "unreleasedAttempts": 0,
        }
        self.save_proof()

    def save_proof(self):
        self.proof_path.write_text(json.dumps(self.proof), encoding="utf-8")

    def snapshot(self):
        return {str(p.relative_to(self.source)): digest(p) for p in self.source.rglob("*") if p.is_file()}

    def run_backup(self, prelude=""):
        if not prelude:
            return subprocess.run([
                "pwsh", "-NoProfile", "-NonInteractive", "-File", str(SCRIPT),
                "-ScopePath", str(self.scope_path), "-QuiescencePath", str(self.proof_path),
                "-ReviewedQuiescenceSha256", digest(self.proof_path), "-Destination", str(self.destination),
                "-PythonPath", sys.executable,
            ], capture_output=True, text=True, timeout=120)
        command = (
            f". {quote(SCRIPT)}; {prelude}\n"
            f"Invoke-OfflineBackup -ScopePath {quote(self.scope_path)} "
            f"-QuiescencePath {quote(self.proof_path)} -ReviewedQuiescenceSha256 {quote(digest(self.proof_path))} "
            f"-Destination {quote(self.destination)} -PythonPath {quote(sys.executable)}"
        )
        return subprocess.run(
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
            capture_output=True, text=True, timeout=120,
        )

    def assert_success(self, result):
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        report = json.loads((self.destination / "complete.json").read_text(encoding="utf-8-sig"))
        self.assertTrue(report["complete"])
        self.assertEqual(report["shutdownDurabilityPassed"], self.proof["shutdownDurabilityPassed"])
        self.assertEqual(len(report["databases"]), len(self.databases))
        self.assertTrue(all(row["integrity"] == "ok" for row in report["databases"]))

    def test_capture_clean_wal_databases_without_source_sidecars(self):
        extra = self.source / "notify-waits.db"
        shutil.copyfile(self.databases[0]["path"], extra)
        self.databases.append({"name": "notify-waits", "path": str(extra)})
        self.save_scope()
        # Evidence capture is useful after failed final writes; it must not imply upgrade readiness.
        self.proof["shutdownDurabilityPassed"] = False
        self.save_proof()
        before = self.snapshot()
        self.assertFalse(any(name.endswith("-wal") for name in before))
        self.assert_success(self.run_backup())
        self.assertEqual(before, self.snapshot())
        self.assertTrue((self.destination / "raw" / "entries" / "config" / "empty").is_dir())
        for db in self.databases:
            self.assertEqual(digest(db["path"]), digest(self.destination / "raw" / "databases" / f'{db["name"]}.db'))

    def test_empty_wal_is_preserved_byte_for_byte(self):
        Path(self.databases[0]["path"] + "-wal").touch()
        before = self.snapshot()
        self.assert_success(self.run_backup())
        self.assertEqual(before, self.snapshot())
        self.assertEqual((self.destination / "raw/databases/conversations.db-wal").stat().st_size, 0)

    def test_nonempty_wal_is_replayed_only_in_working_copy(self):
        # Copy a transaction's main/WAL into the stopped fixture before closing its producer.
        staging = self.root / "producer.db"
        db = sqlite3.connect(staging)
        try:
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("PRAGMA wal_autocheckpoint=0")
            db.execute("CREATE TABLE evidence(value TEXT)")
            db.execute("INSERT INTO evidence VALUES ('wal-only')")
            db.commit()
            shutil.copyfile(staging, self.databases[0]["path"])
            shutil.copyfile(str(staging) + "-wal", self.databases[0]["path"] + "-wal")
        finally:
            db.close()
        before = self.snapshot()
        self.assert_success(self.run_backup())
        self.assertEqual(before, self.snapshot())
        raw = self.destination / "raw/databases/conversations.db-wal"
        self.assertEqual(digest(raw), digest(self.databases[0]["path"] + "-wal"))
        with closing(sqlite3.connect(self.destination / "working/conversations.db")) as saved:
            self.assertEqual(saved.execute("SELECT value FROM evidence").fetchone()[0], "wal-only")

    def test_existing_destination_is_never_overwritten(self):
        self.destination.mkdir()
        sentinel = self.destination / "keep"
        sentinel.write_text("retained", encoding="utf-8")
        result = self.run_backup()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("DestinationExists", result.stderr)
        self.assertEqual(sentinel.read_text(), "retained")
        self.assertEqual(len(list(self.destination.iterdir())), 1)

    def test_refuses_stale_or_unsafe_quiescence_before_capture(self):
        for field, value in (("observedAtUtc", "2000-01-01T00:00:00Z"), ("unknownWriters", True),
                             ("owners", 1), ("shutdownDurabilityPassed", "true"), ("scopeSha256", "0" * 64),
                             ("writersStopped", "false"), ("unknownWriters", "false"), ("owners", "0"),
                             ("observedAtUtc", "2999-01-01T00:00:00Z"), ("schema", "1")):
            with self.subTest(field=field):
                self.save_scope()
                self.proof[field] = value
                self.save_proof()
                result = self.run_backup()
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.destination.exists())

    def test_refuses_destination_inside_source(self):
        self.destination = self.source / "config" / "capture"
        result = self.run_backup()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("PathOverlap", result.stderr)
        self.assertFalse(self.destination.exists())

    def test_absent_sqlite_sidecars_are_reserved_destination_paths(self):
        def inventory():
            return {
                str(path.relative_to(self.source)): ("directory", None) if path.is_dir() else ("file", digest(path))
                for path in self.source.rglob("*")
            }

        for suffix, database in zip(("-shm", "-wal", "-journal"), self.databases):
            with self.subTest(suffix=suffix):
                self.destination = Path(database["path"] + suffix)
                self.assertFalse(self.destination.exists())
                before = inventory()
                result = self.run_backup()
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn("PathOverlap", result.stderr)
                self.assertFalse(self.destination.exists())
                self.assertEqual(before, inventory())
                with closing(sqlite3.connect(database["path"])) as saved:
                    self.assertEqual(saved.execute("SELECT value FROM evidence").fetchone()[0], "saved")

    def test_source_file_set_drift_retains_failed_capture(self):
        added = self.source / "config" / "late.json"
        prelude = (
            "$originalCopy = ${function:Copy-OfflineFile}; "
            "function Copy-OfflineFile { param($From,$To,$InputStream) "
            "& $originalCopy $From $To $InputStream; "
            f"[IO.File]::WriteAllText({quote(added)},'late') }}"
        )
        result = self.run_backup(prelude)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("SourceDrift", result.stderr)
        self.assertTrue((self.destination / "source-inventory.json").exists())
        self.assertTrue((self.destination / "failed.json").exists())
        self.assertFalse((self.destination / "complete.json").exists())

    def test_corrupt_database_fails_without_mutating_raw_evidence(self):
        Path(self.databases[0]["path"]).write_bytes(b"not a database")
        before = self.snapshot()
        result = self.run_backup()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("WorkingCopyVerificationFailed", result.stderr)
        self.assertEqual(before, self.snapshot())
        self.assertEqual(digest(self.databases[0]["path"]), digest(self.destination / "raw/databases/conversations.db"))
        self.assertTrue((self.destination / "failed.json").exists())

    def test_read_only_sqlite_can_create_sidecars_on_original(self):
        path = Path(self.databases[0]["path"])
        self.assertFalse(Path(str(path) + "-wal").exists())
        before = digest(path)
        database = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)
        try:
            database.execute("PRAGMA query_only=ON")
            database.execute("BEGIN")
            database.execute("SELECT count(*) FROM evidence").fetchone()
            self.assertTrue(Path(str(path) + "-wal").exists())
            self.assertTrue(Path(str(path) + "-shm").exists())
            self.assertEqual(digest(path), before)
        finally:
            database.close()

    def test_locked_source_refuses_capture(self):
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                      ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        kernel.CreateFileW.restype = ctypes.c_void_p
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.CreateFileW(self.databases[0]["path"], 0x40000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        try:
            result = self.run_backup()
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(self.destination.exists())
        finally:
            kernel.CloseHandle(handle)

    def test_junction_source_is_refused_before_traversal(self):
        link = self.root / "linked-config"
        result = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(self.source / "config")],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.scope["entries"][0]["path"] = str(link)
        self.save_scope()
        result = self.run_backup()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ReparsePoint", result.stderr)
        self.assertFalse(self.destination.exists())

    def test_access_protection_failure_copies_no_private_bytes(self):
        prelude = (
            "$originalProtect = ${function:Protect-OfflinePath}; "
            "function Protect-OfflinePath { param($From,$To) "
            "if ($To.EndsWith('settings.json')) { throw 'InjectedAccessFailure' }; "
            "& $originalProtect $From $To }"
        )
        result = self.run_backup(prelude)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("InjectedAccessFailure", result.stderr)
        target = self.destination / "raw/entries/config/settings.json"
        self.assertTrue(target.exists())
        self.assertEqual(target.stat().st_size, 0)
        self.assertFalse((self.destination / "complete.json").exists())
        self.assertTrue((self.destination / "failed.json").exists())

    def test_known_writer_is_refused(self):
        self.scope["runtime"]["processPaths"] = [sys.executable]
        self.save_scope()
        result = self.run_backup()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("WriterPresentOrUnreadable", result.stderr)
        self.assertFalse(self.destination.exists())

    def test_declared_task_and_listener_guards_refuse_capture(self):
        cases = (
            ("tasks", ["synthetic-discovery"],
             "function Get-ScheduledTask { [pscustomobject]@{State='Ready'} }", "ProducerTaskEnabledOrAmbiguous"),
            ("ports", [54321],
             "function Get-NetTCPConnection { [pscustomobject]@{LocalPort=54321} }", "ListenerPresent"),
        )
        for key, values, prelude, reason in cases:
            with self.subTest(key=key):
                self.scope["runtime"] = {"processPaths": [], "commandPaths": [], "tasks": [], "ports": []}
                self.scope["runtime"][key] = values
                self.save_scope()
                result = self.run_backup(prelude)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(reason, result.stderr)
                self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    print(f"Python {sys.version.split()[0]}, SQLite {sqlite3.sqlite_version}")
    unittest.main()
