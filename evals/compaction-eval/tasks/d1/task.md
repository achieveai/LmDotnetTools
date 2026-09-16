# d1 — data analysis over vendored CSVs

Family `data`, split `dev`. Workspace = `fixtures/` (four CSVs under `data/`). Checker compares
`answers.json` with the answer key (tolerances in `hidden/expected.json`).

---

Project code name: {SEED}.

The `data/` folder holds four CSV files: `diamonds.csv`, `penguins.csv`, `flights.csv`, `tips.csv`. Answer the nine questions below and write the results to `answers.json` in the workspace root, using exactly the keys shown. Use Python 3 from the standard library only (no pandas, no pip). Work in steps:

1. Inspect each file first: print its header and the first three rows, and note anything odd (missing values, quoting) in `notes.md`.
2. Write one script per file under `scripts/` (`diamonds.py`, `penguins.py`, `flights.py`, `tips.py`). Each script prints its answers as JSON to stdout. Run each one and check its output looks sane before moving on.
3. Write `scripts/combine.py` that runs the four scripts and merges their output into `answers.json`.
4. Write `report.md`: one short section per question with the number and a one-line method note.

Questions (keys in `answers.json`):

- `diamonds_rows` — number of data rows in diamonds.csv.
- `diamonds_mean_price` — mean of `price`, 4 decimals.
- `diamonds_median_price` — median of `price`.
- `diamonds_premium_mean_price` — mean `price` where `cut == "Premium"`, 4 decimals.
- `diamonds_max_price_per_carat` — the largest `price / carat` over all rows, 4 decimals.
- `diamonds_if_2ct_count` — rows with `carat >= 2` and `clarity == "IF"`.
- `penguins_gentoo_mean_mass` — mean `body_mass_g` for `species == "Gentoo"`, ignoring rows with a missing mass, 2 decimals.
- `flights_top_year` — the `year` (as an integer) with the highest total `passengers`.
- `tips_best_day` — the `day` with the highest mean of `tip / total_bill`.

Numbers must be JSON numbers, not strings. When you are done, reply with the contents of `answers.json`.
