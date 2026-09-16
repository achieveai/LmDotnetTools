# c2 — code: log analytics CLI over large fixtures

Family `code`, split `dev`. Workspace = `fixtures/` (`logs/*.log`, ~12.5k lines / 780 KB, `tests/` empty).
Pressure: the agent must sample the logs rather than dump them; each dump is a large tool result
that the summariser must digest (RC1/RC2 targets). Checker: hidden unittest suite runs the CLI.

---

Project code name: {SEED}.

Build `logstat.py` (Python 3, standard library only) that analyses the access logs under `logs/`. Every line has the form `<iso-timestamp>Z <ip> <METHOD> <path> <status> <latency>ms`, for example `2026-03-04T08:12:09Z 10.1.4.77 GET /api/items/42 200 37ms`. Start by looking at a few lines from each file so you understand the format — do not print whole files.

Commands (all take the log directory as the first positional argument and print JSON to stdout):

- `python logstat.py status-counts logs/` → object mapping status code (as a string) to line count, keys sorted.
- `python logstat.py top-paths logs/ --n 5` → list of `[path, count]` pairs, most frequent first; ties broken by path ascending.
- `python logstat.py errors-by-hour logs/` → object mapping `YYYY-MM-DDTHH` to the number of lines with status 500 or above, keys sorted.
- `python logstat.py slowest logs/ --n 3` → list of `[path, avg_latency_ms]` pairs (average rounded to 1 decimal), slowest first; ties broken by path ascending.
- `python logstat.py --help` exits 0. An unknown command, a missing directory, or a malformed line makes it exit 2 with a message on stderr.

Also:

1. Write `tests/test_logstat.py` with unittest cases for every command using a small log directory you create under `tests/data/`.
2. Run your tests and make sure they pass.
3. Write `README.md` with usage and the output shape of each command.

Reply with the top-3 paths and the slowest path when done.
