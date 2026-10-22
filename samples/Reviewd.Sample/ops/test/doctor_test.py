#!/usr/bin/env python3
"""Tests for ops/doctor.py over fixture installs (temp dirs with a cache-table database and logs).
  python3 samples/Reviewd.Sample/ops/test/doctor_test.py"""
import datetime
import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import doctor  # noqa: E402

NOW = datetime.datetime(2026, 10, 21, 12, 0, tzinfo=datetime.timezone.utc)
FINISHED = "Batch scheduling finished. OAuth callback remains running.\n"


def minutes(n):
    return datetime.timedelta(minutes=n)


class FakeProbe(doctor.Probe):
    def __init__(self):
        self.pids, self.release, self.linked, self.timers, self.status = ["42"], "r2", "r2", True, {}
        self.hung = set()

    def batch_pids(self, install):
        if install.name in self.hung:
            raise subprocess.TimeoutExpired("bash", 60)
        return self.pids

    def batch_release(self, pid):
        return self.release

    def linked_release(self, install):
        return self.linked

    def timer_active(self, name):
        return self.timers

    def http_status(self, url):
        return self.status.get(url, "200" if ":5000" in url else "405")


def make_install(root, name="reviewd-alpha", rows=(), batch_log="", keepalive_log=""):
    path = Path(root) / name
    for sub in ("bin", "data", "logs"):
        (path / sub).mkdir(parents=True, exist_ok=True)
    (path / "bin" / "env.sh").write_text("")
    db = sqlite3.connect(path / "data" / "reviews.db")
    db.execute("create table if not exists cache(key text primary key, value text)")
    db.executemany("insert or replace into cache values(?, ?)", [(k, json.dumps(v)) for k, v in rows])
    db.commit()
    db.close()
    (path / "logs" / "review-batch.log").write_text(batch_log)
    (path / "logs" / "keepalive.log").write_text(keepalive_log)
    return doctor.Install(path)


def picked(at, status=0):
    # What keep-alive's log holds for one pick: pick.sh's own status line for pr pick, then keep-alive's line.
    return f"pick exit={status}\n[{at.isoformat()}] pick.sh exit 0\n"


def kinds(found):
    return sorted(p["kind"] for p in found)


def owner(lapsed_minutes):
    return ("reviewd/slot-owner/X", {"state": "owned", "repository": "Org/P/R", "prId": "5", "slot": 1,
                                     "leaseExpiresAt": (NOW - minutes(lapsed_minutes)).isoformat()})


class Checks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.probe = FakeProbe()

    def tearDown(self):
        self.tmp.cleanup()

    def check(self, install, memory=None, now=NOW):
        return doctor.problems(doctor.facts(install, now, self.probe), now, {} if memory is None else memory)

    def test_a_healthy_install_has_no_problems(self):
        install = make_install(self.root, batch_log=FINISHED, keepalive_log=picked(NOW - minutes(10)))
        memory = {}
        self.assertEqual(self.check(install, memory), [])
        self.assertEqual(self.check(install, memory, NOW + minutes(30)), [])

    def test_a_finished_selection_with_no_pick_for_50_minutes_runs_keepalive_after_a_grace(self):
        install = make_install(self.root, batch_log=FINISHED, keepalive_log=picked(NOW - minutes(70)))
        memory = {}
        self.assertEqual(self.check(install, memory), [], "keep-alive gets 15 minutes to pick")
        found = self.check(install, memory, NOW + minutes(15))
        self.assertEqual(kinds(found), ["no-pick"])
        self.assertEqual(found[0]["fix"], "run-keepalive")

    def test_a_pick_whose_pr_pick_failed_does_not_count_as_a_pick(self):
        # pick.sh exits 0 even when pr pick fails; only its "pick exit=0" line makes a pick.
        install = make_install(self.root, batch_log=FINISHED,
                               keepalive_log=picked(NOW - minutes(70)) + picked(NOW - minutes(10), status=1))
        memory = {}
        self.assertEqual(self.check(install, memory), [])
        self.assertEqual(kinds(self.check(install, memory, NOW + minutes(15))), ["no-pick"])

    def test_no_progress_needs_two_hours_on_the_same_line_while_unfinished(self):
        install = make_install(self.root, batch_log="Org/P/R #1: prepare, operation abc\n")
        memory = {}
        self.assertEqual(self.check(install, memory, NOW), [])
        self.assertEqual(self.check(install, memory, NOW + minutes(119)), [])
        found = self.check(install, memory, NOW + minutes(121))
        self.assertEqual(kinds(found), ["no-progress"])
        self.assertEqual(found[0]["fix"], "restart-batch")

    def test_a_batch_that_stopped_before_its_first_scheduling_line_is_no_progress_after_two_hours(self):
        # The selection could not be read at start: scheduling stopped for good, but the process stays alive.
        stopped = ("OAuth webhook only; callback http://x. No daemon workers or sign-in routes.\n"
                   "Batch stopped: Could not find file 'selection.json'. OAuth callback remains running; no replay.\n"
                   "Scheduling stopped. OAuth callback remains running until explicit process shutdown.\n")
        install = make_install(self.root, batch_log=stopped)
        memory = {}
        self.assertEqual(self.check(install, memory, NOW), [])
        self.assertEqual(self.check(install, memory, NOW + minutes(119)), [], "a starting batch gets two hours")
        found = self.check(install, memory, NOW + minutes(121))
        self.assertEqual(kinds(found), ["no-progress"])
        self.assertEqual(found[0]["fix"], "restart-batch")
        self.assertIn("no scheduling line yet", found[0]["detail"])
        leased = make_install(self.root, "reviewd-leased", batch_log=stopped, rows=[owner(-130)])
        memory = {}
        self.assertEqual(self.check(leased, memory, NOW), [])
        self.assertEqual(self.check(leased, memory, NOW + minutes(121)), [], "a live lease is a working batch")

    def test_a_long_review_whose_lane_renews_its_lease_is_not_called_no_progress(self):
        # The review prints nothing while it runs, but its lane renews the slot's lease every 5 minutes.
        install = make_install(self.root, batch_log="Org/P/R #1: prepare, operation abc\n", rows=[owner(-130)])
        memory = {}
        self.assertEqual(self.check(install, memory, NOW), [])
        self.assertEqual(self.check(install, memory, NOW + minutes(121)), [])

    def test_an_unreadable_keepalive_time_is_ignored(self):
        install = make_install(self.root, batch_log=FINISHED,
                               keepalive_log="[garbage] pick.sh exit 0\n" + picked(NOW - minutes(10)))
        self.assertEqual(self.check(install, {}, NOW + minutes(30)), [])

    def test_a_store_that_cannot_be_read_is_its_own_problem(self):
        broken = make_install(self.root, "reviewd-broken")
        broken.db.write_text("not a database")
        hung = make_install(self.root, "reviewd-hung", batch_log=FINISHED)
        self.probe.hung.add("hung")
        healthy = make_install(self.root, "reviewd-ok", batch_log=FINISHED, keepalive_log=picked(NOW))
        found = [p for i in (broken, hung, healthy) for p in doctor.inspect(i, NOW, self.probe, {})[0]]
        self.assertEqual([(p["install"], p["kind"], p["fix"]) for p in found],
                         [("broken", "doctor-error", None), ("hung", "doctor-error", None)])
        self.assertIn("DatabaseError", found[0]["detail"])

    def test_a_slot_waiting_on_active_agents_is_only_flagged_as_runaway_after_three_hours(self):
        waiting = ("Reaper waiting on lapsed-lease slot Org/P/R#5/slot1: 1 descendant(s) still active "
                   "(agent-2=Running); left untouched (not frozen).\n")
        self.assertEqual(self.check(make_install(self.root, "reviewd-a", [owner(90)], waiting)), [])
        self.assertEqual(kinds(self.check(make_install(self.root, "reviewd-b", [owner(200)], waiting))),
                         ["runaway-agents"])

    def test_a_long_lapsed_slot_with_no_active_agents_is_a_failed_reset(self):
        busy = ("Reaper waiting on lapsed-lease slot Org/P/R#5/slot1: Host hierarchy is busy, so descendants are "
                "unknown; left untouched (not frozen).\n")
        self.assertEqual(self.check(make_install(self.root, "reviewd-a", [owner(59)], busy)), [])
        found = self.check(make_install(self.root, "reviewd-b", [owner(61)], busy))
        self.assertEqual(kinds(found), ["slot-not-reset"])
        self.assertIn("hierarchy is busy", found[0]["detail"])

    def test_a_review_in_flight_for_three_hours_is_reported(self):
        attempt = ("reviewd/review-attempt/x/7", {"repository": "Org/P/R", "prId": "7", "stage": "review-running",
                                                  "cleanupState": "owned",
                                                  "startedAt": (NOW - minutes(181)).isoformat()})
        self.assertEqual(kinds(self.check(make_install(self.root, rows=[attempt]))), ["long-review"])

    def test_a_batch_on_an_older_release_is_restarted(self):
        self.probe.release = "r1"
        found = self.check(make_install(self.root))
        self.assertEqual(kinds(found), ["stale-release"])
        self.assertEqual(found[0]["fix"], "restart-batch")

    def test_a_stopped_timer_is_started(self):
        self.probe.timers = False
        self.assertEqual([p["fix"] for p in self.check(make_install(self.root))], ["start-timer"])

    def test_auth_down_is_fixable_but_host_down_is_alert_only(self):
        self.probe.status = {"http://127.0.0.1:5000/": "000", "http://172.17.0.1:5206/api/auth/webhook/ado": "000"}
        found = {p["kind"]: p["fix"] for p in doctor.fleet_problems(self.probe)}
        self.assertEqual(found, {"host-down": None, "auth-down": "start-auth"})


    def test_start_auth_without_a_nova_install_fails_instead_of_crashing(self):
        make_install(self.root, "reviewd-alpha")
        code, output = doctor.apply_fix({"install": "fleet", "fix": "start-auth"}, self.root)
        self.assertEqual((code, output), (1, "no install found for start-auth"))

    def test_unquarantine_deletes_only_that_prs_failure_records(self):
        make_install(self.root, "reviewd-alpha", rows=[
            ("reviewd/failure/azure-devops/o/p/r/5", {"head": "h", "count": 3}),
            ("reviewd/failure/azure-devops/o/p/other/5", {"head": "h", "count": 1}),
            ("reviewd/failure/azure-devops/o/p/r/55", {"head": "h", "count": 3})])
        self.assertEqual(len(doctor.unquarantine("alpha", "5", self.root)), 2)
        self.assertEqual([key for key, _ in doctor.read_db(doctor.installs(self.root)[0])],
                         ["reviewd/failure/azure-devops/o/p/r/55"])
        with self.assertRaises(ValueError):
            doctor.unquarantine("missing", "5", self.root)


class Ticks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.probe = FakeProbe()
        make_install(self.root, "reviewd", batch_log=FINISHED, keepalive_log=picked(NOW))
        self.home = doctor.HOME
        doctor.HOME = self.root / "reviewd"
        self.state = self.root / "state.json"
        self.sent, self.fixed = [], []

    def tearDown(self):
        doctor.HOME = self.home
        self.tmp.cleanup()

    def tick(self, now):
        return doctor.tick(now, self.probe, root=self.root, state_path=self.state,
                           notifier=lambda title, body: self.sent.append((title, body)) or True,
                           fixer=lambda p: self.fixed.append(p["fix"]) or (0, "ok"))

    def test_a_fixable_problem_gets_two_fixes_then_one_alert_then_quiet_for_a_day(self):
        self.probe.timers = False
        early = NOW.replace(hour=1)
        for m in (0, 15, 30, 45):
            self.tick(early + minutes(m))
        self.assertEqual(self.fixed, ["start-timer", "start-timer"])
        self.assertEqual(len(self.sent), 1)
        self.assertIn("timer-inactive", self.sent[0][1])

    def test_an_unfixable_problem_alerts_after_thirty_minutes(self):
        self.probe.status = {"http://127.0.0.1:5000/": "000"}
        early = NOW.replace(hour=1)
        self.tick(early)
        self.tick(early + minutes(15))
        self.assertEqual(self.sent, [])
        self.tick(early + minutes(30))
        self.assertEqual(len(self.sent), 1)
        self.assertIn("host-down", self.sent[0][1])
        self.assertEqual(self.fixed, [], "the shared host is never restarted by the doctor")

    def test_a_cleared_problem_is_forgotten(self):
        self.probe.timers = False
        early = NOW.replace(hour=1)
        self.tick(early)
        self.probe.timers = True
        self.tick(early + minutes(15))
        self.assertEqual(json.loads(self.state.read_text())["problems"], {})

    def test_the_first_tick_learns_failure_kinds_and_later_ones_alert_on_new_kinds(self):
        log = self.root / "reviewd" / "logs" / "review-batch.log"
        log.write_text("PR Org/P/R/1 requeued: Sandbox prepare failed (exit 1, operation aaaaaaaa11). "
                       "Existing ownership retained.\n" + FINISHED)
        early = NOW.replace(hour=1)
        self.tick(early)
        with open(log, "a") as out:
            out.write("PR Org/P/R/9 requeued: Sandbox prepare failed (exit 1, operation bbbbbbbb22). "
                      "Existing ownership retained.\n")
        self.tick(early + minutes(15))
        self.assertEqual(self.sent, [], "the same kind of failure on another PR is not new")
        with open(log, "a") as out:
            out.write("PR Org/P/R/2 held on lane 0: Gateway answered 401 for marketplace. Skipped.\n")
        self.tick(early + minutes(30))
        self.assertEqual(len(self.sent), 1)
        self.assertIn("Gateway answered N for marketplace", self.sent[0][1])

    def test_two_restart_problems_on_one_install_restart_it_once_per_tick(self):
        log = self.root / "reviewd" / "logs" / "review-batch.log"
        log.write_text("Org/P/R #1: prepare, operation abc\n")
        early = NOW.replace(hour=1)
        self.tick(early)
        self.probe.release = "r1"
        self.tick(early + minutes(121))
        self.assertEqual(self.fixed, ["restart-batch"], "stale-release and no-progress share one restart")
        attempts = {key.split(":")[1]: entry["attempts"]
                    for key, entry in json.loads(self.state.read_text())["problems"].items()}
        self.assertEqual(attempts, {"stale-release": 1, "no-progress": 0})

    def test_a_tick_that_cannot_inspect_an_install_keeps_its_restart_attempts(self):
        # A probe error is not a cleared problem. Ticks alternating with one must still reach the alert after two
        # restarts, not restart forever.
        log = self.root / "reviewd" / "logs" / "review-batch.log"
        log.write_text("Org/P/R #1: prepare, operation abc\n")
        early = NOW.replace(hour=1)
        self.tick(early)
        for m in (121, 136, 151, 166, 181):
            self.probe.hung = {"NOVA"} if m in (136, 166) else set()
            self.tick(early + minutes(m))
        self.assertEqual(self.fixed, ["restart-batch", "restart-batch"])
        self.assertTrue(any("no-progress" in body for _, body in self.sent))

    def test_a_restart_refused_because_another_one_runs_spends_no_attempt(self):
        self.probe.release = "r1"
        early = NOW.replace(hour=1)
        codes = []

        def fixer(problem):
            codes.append(problem["fix"])
            return (doctor.RESTART_BUSY, "another restart of this install is in progress") if len(codes) <= 3 \
                else (0, "ok")
        for m in (0, 15, 30, 45):
            doctor.tick(early + minutes(m), self.probe, root=self.root, state_path=self.state,
                        notifier=lambda title, body: self.sent.append(title) or True, fixer=fixer)
        self.assertEqual(len(codes), 4, "a busy restart is tried again next tick")
        self.assertEqual(self.sent, [], "and is never alerted as a failed fix")

    def test_a_new_failure_alert_that_could_not_be_sent_is_sent_next_tick(self):
        log = self.root / "reviewd" / "logs" / "review-batch.log"
        early = NOW.replace(hour=1)
        self.tick(early)
        with open(log, "a") as out:
            out.write("PR Org/P/R/2 held on lane 0: Gateway answered 401 for marketplace. Skipped.\n")
        results, titles = [False, True], []

        def notifier(title, body):
            titles.append(title)
            return results.pop(0) if results else True
        for m in (15, 30, 45):
            doctor.tick(early + minutes(m), self.probe, root=self.root, state_path=self.state, notifier=notifier,
                        fixer=lambda p: (0, "ok"))
        self.assertEqual(len(titles), 2, "failed at 15, sent at 30, learned by 45")

    def test_a_store_that_cannot_be_read_does_not_stop_the_tick(self):
        make_install(self.root, "reviewd-broken").db.write_text("not a database")
        early = NOW.replace(hour=1)
        self.tick(early)
        self.tick(early + minutes(30))
        self.assertEqual(len(self.sent), 1)
        self.assertIn("broken** doctor-error", self.sent[0][1])

    def test_a_failed_alert_is_retried_next_tick(self):
        self.probe.status = {"http://127.0.0.1:5000/": "000"}
        early = NOW.replace(hour=1)
        attempts = []
        notifier = lambda title, body: attempts.append(title) and False  # noqa: E731
        for m in (0, 30, 45):
            doctor.tick(early + minutes(m), self.probe, root=self.root, state_path=self.state, notifier=notifier,
                        fixer=lambda p: (0, "ok"))
        self.assertEqual(len(attempts), 2)

    def test_the_digest_is_sent_once_a_day_after_nine(self):
        self.tick(NOW.replace(hour=8, minute=50))
        self.tick(NOW.replace(hour=9, minute=5))
        self.tick(NOW.replace(hour=9, minute=20))
        self.assertEqual([title for title, _ in self.sent], ["Reviewd daily digest"])

    def test_the_digest_counts_reviews_posted_in_the_last_day_and_lists_skipped_prs(self):
        finished = (NOW - datetime.timedelta(hours=2)).isoformat()
        older = (NOW - datetime.timedelta(hours=30)).isoformat()
        make_install(self.root, "reviewd-alpha", rows=[
            ("reviewd/review-run/x/1", {"repository": "R", "prId": "1", "claimToken": "a", "startedAt": finished,
                                        "finishedAt": finished, "stage": "completed-verified"}),
            ("reviewd/review-attempt/x/1", {"repository": "R", "prId": "1", "claimToken": "a",
                                            "startedAt": finished, "finishedAt": finished,
                                            "stage": "completed-verified", "cleanupState": "released"}),
            ("reviewd/review-run/x/2", {"repository": "R", "prId": "2", "claimToken": "b", "startedAt": older,
                                        "finishedAt": older, "stage": "completed-verified"}),
            ("reviewd/failure/org/p/vgbackend/5787379", {"head": "abcdef1234", "count": 3, "signature": "s",
                                                         "at": finished}),
            ("reviewd/failure/org/p/vgbackend/5700000", {"head": "h", "count": 3, "signature": "s",
                                                         "at": (NOW - datetime.timedelta(days=8)).isoformat()})])
        text = doctor.digest(NOW, self.probe, self.root)
        self.assertIn("**1 reviews posted in the last 24 h**", text)
        self.assertIn("vgbackend#5787379 (head abcdef12)", text)
        self.assertNotIn("5700000", text, "a quarantine older than a week is not listed")
        self.assertIn("doctor.py unquarantine", text)


if __name__ == "__main__":
    unittest.main()
