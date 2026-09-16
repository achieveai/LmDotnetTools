"""Hidden acceptance tests for c2. Copied into <workspace>/tests/ by check.ps1; runs the CLI as a subprocess."""
import json, os, subprocess, sys, unittest

WS = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
EXPECTED = json.load(open(os.path.join(os.path.dirname(__file__), "logstat_expected.json")))


def run(*args):
    return subprocess.run([sys.executable, "logstat.py", *args], cwd=WS, capture_output=True, text=True, timeout=120)


class LogStatTests(unittest.TestCase):
    def test_status_counts(self):
        r = run("status-counts", "logs/")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(json.loads(r.stdout), EXPECTED["status_counts"])

    def test_top_paths(self):
        r = run("top-paths", "logs/", "--n", "5")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual([list(x) for x in json.loads(r.stdout)], EXPECTED["top_paths_5"])

    def test_errors_by_hour(self):
        r = run("errors-by-hour", "logs/")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(json.loads(r.stdout), EXPECTED["errors_by_hour"])

    def test_slowest(self):
        r = run("slowest", "logs/", "--n", "3")
        self.assertEqual(r.returncode, 0, r.stderr)
        got = [[p, round(float(v), 1)] for p, v in json.loads(r.stdout)]
        self.assertEqual(got, EXPECTED["slowest_3"])

    def test_help_exits_zero(self):
        self.assertEqual(run("--help").returncode, 0)

    def test_unknown_command_exits_two(self):
        self.assertEqual(run("frobnicate", "logs/").returncode, 2)

    def test_missing_dir_exits_two(self):
        self.assertEqual(run("status-counts", "no-such-dir/").returncode, 2)

    def test_malformed_line_exits_two(self):
        bad = os.path.join(WS, "tests", "_bad_logs")
        os.makedirs(bad, exist_ok=True)
        with open(os.path.join(bad, "x.log"), "w") as f:
            f.write("2026-03-04T08:12:09Z 10.1.4.77 GET /api/items/42 200 37ms\nthis is not a log line\n")
        self.assertEqual(run("status-counts", "tests/_bad_logs/").returncode, 2)


if __name__ == "__main__":
    unittest.main()
