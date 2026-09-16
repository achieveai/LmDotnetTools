# c1 — CSV aggregator CLI (Python, stdlib only)

Family `coding-py`, split `dev`. Workspace = `fixtures/` (a sample CSV and an empty `tests/`).
Checker copies `hidden/test_csvagg.py` into `tests/` and runs `python -m unittest`.

---

Project code name: {SEED}.

Build `csvagg.py`, a command-line CSV aggregator using only the Python 3 standard library. Work test-first: write your own tests in `tests/test_csvagg.py` for each feature before implementing it, run them with `python -m unittest discover -s tests`, and keep them green. Use `data/sample.csv` to try things by hand.

Contract:

```
python csvagg.py FILE --group-by COL [--sum COL]... [--avg COL]... [--min COL]... [--max COL]... [--count] [--where COL=VALUE]... [--sort KEY] [--desc] [--top N]
```

- Output is a JSON array on stdout, one object per group, always containing the group-by column under its own name; aggregate keys are `sum_<col>`, `avg_<col>`, `min_<col>`, `max_<col>`, `count`.
- Numeric columns are parsed as floats; a value that is empty or not numeric is skipped for that aggregate (it still counts in `count`). `avg` of a group with no numeric values is `null`.
- `--where COL=VALUE` filters rows before grouping (exact string match; repeatable, all must match).
- `--sort KEY` sorts groups by that output key ascending; `--desc` reverses; `--top N` keeps the first N after sorting. Without `--sort`, groups appear in first-seen order.
- Floats are rounded to 4 decimals in the output.
- Errors: a missing file, an unknown column, or a malformed `--where` exit with code 2 and a one-line message on stderr; nothing on stdout.
- `python csvagg.py --help` prints usage and exits 0.

Also write `README.md` with three usage examples run against `data/sample.csv` and their actual output. Reply with a short summary when done.
