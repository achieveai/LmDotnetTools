#!/usr/bin/env python3
"""Tests for the selection check inside ops/keepalive.sh (the Python between << 'PY' and PY), run as keep-alive runs it.
  python3 samples/Reviewd.Sample/ops/test/keepalive_test.py"""
import contextlib
import hashlib
import io
import json
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = (Path(__file__).resolve().parent.parent / "keepalive.sh").read_text()
CHECK = SCRIPT.split("<< 'PY'\n", 1)[1].split("\nPY\n", 1)[0]
IDLE = ("Batch scheduling finished: nothing left to start for this selection; polling for the next one. "
        "OAuth callback remains running.\n")
REVIEWING = "Org/P/R #7: prepare, operation abc\n"


def selection(*prs):
    rows = [{"repository": "Org/P/R", "prId": pr, "headSha": "a" * 40} for pr in prs]
    return json.dumps({"repositories": {"Org/P/R": rows} if rows else {}, "lanes": {"Org/P/R": [0, 1]}})


def digest(text):
    # What the batch stamps: SlotRelease.Digest over the selection text, upper-case hex.
    return hashlib.sha256(text.encode("utf-8")).hexdigest().upper()


class SelectionCheck(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "logs").mkdir()

    def tearDown(self):
        self.tmp.cleanup()

    def state(self, text, log, rows=(), bom=False):
        (self.root / "selection.json").write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        (self.root / "logs" / "review-batch.log").write_text(log)
        db = sqlite3.connect(self.root / "reviews.db")
        db.execute("create table if not exists cache(key text primary key, value text)")
        db.executemany("insert or replace into cache values(?, ?)", [(k, json.dumps(v)) for k, v in rows])
        db.commit()
        db.close()
        # Run in-process with keep-alive's arguments; the check ends with sys.exit() or by falling off the end.
        argv, out, scope = sys.argv, io.StringIO(), {"__name__": "__main__"}
        sys.argv = ["-", str(self.root / "reviews.db"), str(self.root / "selection.json"), str(self.root / "logs")]
        try:
            with contextlib.redirect_stdout(out), contextlib.suppress(SystemExit):
                exec(compile(CHECK, "keepalive.sh", "exec"), scope)
        finally:
            sys.argv = argv
            if "db" in scope:
                scope["db"].close()
        return out.getvalue().strip()

    def test_a_queue_idle_on_this_selection_is_finished(self):
        text = selection("1", "2")
        queue = ("reviewd/batch-queue/w", {"schemaVersion": 2, "idle": digest(text), "entries": []})
        self.assertEqual(self.state(text, REVIEWING + IDLE, [queue]), "finished")

    def test_the_digest_matches_a_selection_written_with_a_byte_order_mark(self):
        text = selection("1")
        queue = ("reviewd/batch-queue/w", {"schemaVersion": 2, "idle": digest(text), "entries": []})
        self.assertEqual(self.state(text, IDLE, [queue], bom=True), "finished")

    def test_a_selection_the_batch_has_not_merged_yet_is_running(self):
        queue = ("reviewd/batch-queue/w", {"schemaVersion": 2, "idle": digest(selection("1")), "entries": []})
        self.assertEqual(self.state(selection("1", "2"), IDLE, [queue]), "running")

    def test_an_active_review_after_the_idle_line_is_running(self):
        text = selection("1")
        queue = ("reviewd/batch-queue/w", {"schemaVersion": 2, "idle": digest(text), "entries": []})
        self.assertEqual(self.state(text, IDLE + REVIEWING, [queue]), "running")

    def test_an_old_round_row_no_longer_finishes_a_selection(self):
        text = selection("1")
        entries = [{"repo": "Org/P/R", "pr": "1", "head": "a" * 40, "status": "verified"}]
        legacy = ("reviewd/batch-state/" + digest(text), {"schemaVersion": 1, "entries": entries})
        self.assertEqual(self.state(text, IDLE, [legacy]), "running")

    def test_an_empty_selection_is_finished(self):
        self.assertEqual(self.state(selection(), IDLE), "finished")

    def test_no_scheduling_line_is_unknown(self):
        self.assertTrue(self.state(selection("1"), "").startswith("unknown"))


if __name__ == "__main__":
    unittest.main()
