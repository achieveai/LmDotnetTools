# c2 — code: log analytics CLI plus a per-day incident report read from the logs

Family `code`, split `dev`. Workspace = `fixtures/` (`logs/*.log`, 12 days, ~1.5k request lines plus
operator comments; `tests/` empty). Pressure: the task requires reading every day's log in full
(twelve ~4.5k-token tool results, ~55k tokens) before the code work, then the usual edit/test cycles
(RC1/RC2 targets); the per-day incident answers must survive the cut (R3). Checker: hidden unittest
suite runs the CLI; `incidents.json` against the hidden key.

---

Project code name: {SEED}.

The access logs under `logs/` cover twelve days, one file per day. Two kinds of line exist:

- request lines of the form `<iso-timestamp>Z <ip> <METHOD> <path> <status> <latency>ms`, for example `2026-03-04T08:12:09Z 10.1.4.77 GET /api/items/42 200 37ms`;
- operator comment lines that start with `#` — free-text notes the on-call engineers left in the log (handovers, deploys, incident tickets).

Part 1 — incident report. Read every file under `logs/` in full with the Read tool (the whole file each time; do not use head, tail, grep or a script for this part — the notes have to be read in context). On each day the operators OPEN exactly one incident ticket (`INC-nnnn`) and name the path it affected. Other ticket ids also appear on the same day in notes about earlier incidents, retros, or tickets that are already closed; those do not count. Write `incidents.json` as an object keyed by date (`YYYY-MM-DD`, one key per log file) whose values are objects `{"ticket": "INC-nnnn", "path": "<affected path exactly as written in the note>"}`.

Part 2 — build `logstat.py` (Python 3, standard library only) that analyses the logs. Comment lines are skipped by every command; a line that is neither a comment nor a well-formed request line is malformed. Commands (all take the log directory as the first positional argument and print JSON to stdout):

- `python logstat.py status-counts logs/` → object mapping status code (as a string) to request-line count, keys sorted.
- `python logstat.py top-paths logs/ --n 5` → list of `[path, count]` pairs, most frequent first; ties broken by path ascending. Paths are compared exactly as written (`/api/items/42` and `/api/items/43` are different paths).
- `python logstat.py errors-by-hour logs/` → object mapping `YYYY-MM-DDTHH` to the number of request lines with status 500 or above, keys sorted; hours with no errors are omitted.
- `python logstat.py slowest logs/ --n 3` → list of `[path, avg_latency_ms]` pairs (average over that exact path, rounded to 1 decimal), slowest first; ties broken by path ascending.
- `python logstat.py --help` exits 0. An unknown command, a missing directory, or a malformed line makes it exit 2 with a message on stderr.

Also:

1. Write `tests/test_logstat.py` with unittest cases for every command (including that comment lines are skipped) using a small log directory you create under `tests/data/`.
2. Run your tests and make sure they pass.
3. Write `README.md` with usage and the output shape of each command.

Reply with the twelve incident tickets, the top-3 paths and the slowest path when done.
