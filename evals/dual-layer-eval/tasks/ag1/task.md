# ag1 — exact aggregation over noisy warehouse ledgers (synthetic)

Family `aggregation`, split `dev`. Workspace = `fixtures/` (`ledgers/*.csv`, 48 files, 3.54 MB,
~54,274 rows). Every answer is an exact number over a small slice of the rows; the rest is
noise. Three traps a careless read or summary loses: 11 files say in their header notes that `qty` is in
cases of 12 (worded four ways; 8 other files carry decoy notes about cases that change nothing), re-sent rows
(same txn_id) count once, and REVERSAL rows cancel a transaction that may sit in an earlier week's file.
`hidden/build-report.json` records which trap flips which answer. Checker: exact integer / site code;
score = correct / 8. Generator + knobs: `hidden/gen_ledgers.py`, `hidden/build.json`.

---

Project code name: {SEED}.

`ledgers/` holds 48 warehouse movement exports: one CSV file per site per ISO week
(sites AMS, BCN, CPH, DUB, FRA, LIS, MIL, OSL; weeks 2026-W10 to 2026-W15), about 54,274 rows in all.
Columns: `txn_id,timestamp,site,type,sku,qty,region,carrier,ref_txn`.

How to read them:

- `type` is SHIP, RECEIVE, COUNT or REVERSAL. COUNT rows are stock counts, not movements.
- A REVERSAL row cancels the transaction named in its `ref_txn` column entirely (that transaction
  may be in an earlier week's file of the same site). The REVERSAL row itself moves nothing.
- The same transaction can appear on more than one row when a row was re-sent. Count each
  `txn_id` once.
- Lines starting with `#` are operator notes. Some notes change how a file's rows must be read;
  most do not. Read each file's notes and apply the ones that matter.
- "Units" means single items. "Shipped" means SHIP transactions that were not reversed.

Answer these 8 questions exactly (whole numbers, no rounding; q4 is a site code):

- **q1.** How many units of SKU K-1002 were shipped in total, across all sites and all six weeks?
- **q2.** How many units did site LIS receive in total across the six weeks?
- **q3.** How many shipments (distinct SHIP transactions) went to region NORD with carrier Brisa in 2026-W11, across all sites?
- **q4.** Which site shipped the most units of SKU K-1004 in 2026-W11? Answer with the three-letter site code.
- **q5.** How many units were shipped in total on 2026-04-03 (UTC date of the timestamp), all sites and SKUs?
- **q6.** How many SHIP transactions were cancelled by a REVERSAL row that sits in a LATER week's file than the shipment itself?
- **q7.** How many units did site DUB ship to region SUD with carrier Ostra, across the six weeks?
- **q8.** How many transaction ids appear on more than one row (re-sent rows), across all files?

Write `answers.json` in the workspace root: `{"q1": 1234, "q2": 567, ..., "q4": "XYZ", ...}` with every
question from q1 to q8. You may use scripts (Python 3, standard library) as well as reading files.

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
