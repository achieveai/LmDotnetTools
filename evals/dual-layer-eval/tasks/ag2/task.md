# ag2 — ledger aggregation governed by buried memo rules (synthetic)

Family `aggregation`, split `dev`. Workspace = `fixtures/` (48 `ledgers/*.csv`, ~35,766 rows, plus
150 `memos/*.md` bulletins; 2.89 MB, ~722k tokens). Hardens ag1 (retired: a script
solved it). The CSVs carry no notes; five bulletins change the counting (a carton unit at one site from a
week, a training-mirror site excluded from network figures, a void migration window, a unit switch whose
week a later bulletin corrects, and a rejected proposal to exclude another site). Each rule is one
paraphrased paragraph in the middle of a long bulletin. A memo-blind script scores
0.0. Checker: exact integer / site code; score = correct / 8.
Generator + knobs: `hidden/gen_ledgers.py`, `hidden/build.json`.

---

Project code name: {SEED}.

`ledgers/` holds 48 warehouse movement exports, one CSV per site per ISO week (sites
AMS, BCN, CPH, DUB, FRA, LIS, MIL, OSL; weeks 2026-W09 to 2026-W14; about 35,766 rows). Columns: `txn_id,timestamp,site,type,sku,qty,region,carrier,ref_txn`.
`memos/` holds 150 operations bulletins from the same period.

How to count:

- `type` is SHIP, RECEIVE, COUNT or REVERSAL. COUNT rows are stock counts, not movements.
- A REVERSAL row cancels the transaction named in its `ref_txn` column entirely (it may be in an earlier
  week's file of the same site). The REVERSAL row itself moves nothing.
- A transaction can appear on more than one row when a row was re-sent. Count each `txn_id` once.
- "Units" means single pieces. "Shipped" means SHIP transactions that were not cancelled.
- The bulletins are part of the data. A few of them change how the ledgers must be read or which rows
  count, and some later bulletins correct earlier ones. Every rule that is in force applies to every
  question; most bulletins change nothing. The CSV files themselves carry no notes.

Answer these 8 questions exactly (whole numbers; q4 is a site code):

- **q1.** How many units of SKU K-2003 were shipped across the network over the whole period?
- **q2.** How many units did site LIS receive in weeks 2026-W12 to 2026-W14?
- **q3.** How many shipments (distinct SHIP transactions) does the network count for 2026-03-14 (UTC date of the timestamp)?
- **q4.** Which site shipped the most units of SKU K-2001 in 2026-W13? Answer with the three-letter site code.
- **q5.** How many units did the network ship to region WEST with carrier Halden over the whole period?
- **q6.** How many units did site BCN ship in weeks 2026-W09 to 2026-W12?
- **q7.** How many SHIP transactions across the network were cancelled by a REVERSAL?
- **q8.** How many units did the network ship in total in 2026-W11?

Write `answers.json` in the workspace root: `{"q1": 1234, "q2": 567, ..., "q4": "XYZ", ...}` with every
question from q1 to q8. You may use scripts (Python 3, standard library) as well as reading files.

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
