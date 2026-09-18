"""Hidden tests for c1. Copied into <workspace>/tests/ by check.ps1; each test is one check."""

import json
import os
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(ROOT, "csvagg.py")
SAMPLE = os.path.join(ROOT, "data", "sample.csv")


def run(*args):
    return subprocess.run([sys.executable, CLI, *args], capture_output=True, text=True, cwd=ROOT)


def rows(*args):
    proc = run(*args)
    assert proc.returncode == 0, proc.stderr
    return json.loads(proc.stdout)


class CsvAggTests(unittest.TestCase):
    def test_group_sum_count_first_seen_order(self):
        out = rows(SAMPLE, "--group-by", "region", "--sum", "units", "--count")
        self.assertEqual([r["region"] for r in out], ["north", "south", "east"])
        north = out[0]
        self.assertEqual(north["sum_units"], 17)
        self.assertEqual(north["count"], 3)

    def test_avg_skips_non_numeric_and_rounds(self):
        out = rows(SAMPLE, "--group-by", "region", "--avg", "price")
        north = next(r for r in out if r["region"] == "north")
        self.assertEqual(north["avg_price"], 6.25)  # (2.5 + 10) / 2, "n/a" skipped
        south = next(r for r in out if r["region"] == "south")
        self.assertEqual(south["avg_price"], 5.75)  # (2.5 + 4.75 + 10) / 3

    def test_avg_of_no_numeric_values_is_null(self):
        with tempfile.NamedTemporaryFile("w", suffix=".csv", delete=False, dir=ROOT) as f:
            f.write("k,v\na,\na,x\n")
            path = f.name
        try:
            out = rows(path, "--group-by", "k", "--avg", "v", "--count")
            self.assertIsNone(out[0]["avg_v"])
            self.assertEqual(out[0]["count"], 2)
        finally:
            os.remove(path)

    def test_where_min_max(self):
        out = rows(SAMPLE, "--group-by", "product", "--where", "returned=no", "--min", "units", "--max", "units")
        widget = next(r for r in out if r["product"] == "widget")
        self.assertEqual((widget["min_units"], widget["max_units"]), (10, 12))
        self.assertNotIn("gizmo", [r["product"] for r in out if r.get("min_units") == 7])

    def test_sort_desc_top(self):
        out = rows(SAMPLE, "--group-by", "rep", "--sum", "units", "--sort", "sum_units", "--desc", "--top", "2")
        self.assertEqual([r["rep"] for r in out], ["ana", "dee"])

    def test_errors_exit_2_with_empty_stdout(self):
        missing = run(os.path.join(ROOT, "nope.csv"), "--group-by", "region")
        self.assertEqual(missing.returncode, 2)
        self.assertEqual(missing.stdout, "")
        self.assertTrue(missing.stderr.strip())
        unknown = run(SAMPLE, "--group-by", "nocolumn")
        self.assertEqual(unknown.returncode, 2)
        bad_where = run(SAMPLE, "--group-by", "region", "--where", "region")
        self.assertEqual(bad_where.returncode, 2)

    def test_help_exits_zero(self):
        proc = run("--help")
        self.assertEqual(proc.returncode, 0)
        self.assertIn("group-by", proc.stdout)


if __name__ == "__main__":
    unittest.main()
