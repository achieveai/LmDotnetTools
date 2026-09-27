# Review cost and token collection

`collect-review-usage.py` is a read-only operator companion for a running LmStreaming review host. It does not start reviews, change the daemon database, post comments, or push artifacts. Python 3 standard library on Linux/WSL is sufficient; no .NET build is required.

Supply credentials **through the process environment**, never command-line arguments or files created by this tool:

- `REVIEW_USAGE_APP_ID`: the daemon's Gateway application identity.
- `REVIEW_USAGE_APP_KEY`: its registered application key.
- `REVIEW_USAGE_S2S_SECRET`: optional review-host S2S secret, when required.

```bash
python3 samples/CodeReviewDaemon.Sample/tools/collect-review-usage.py \
  --database "$PWD/.run/nova/review.db" \
  --base-url http://127.0.0.1:5000 \
  --output "$PWD/.run/nova/usage"
```

Use the actual configured review-host URL. Add `--interval 120` for ongoing collection. Stop with Ctrl+C. Only one collector may own an output directory at a time. This is a companion process, **not built into the daemon**; it must be running or invoked after reviews to collect new usage.

## Output

- `review-usage.md`: readable per-run, cumulative PR, and per-run model tables.
- `review-usage.json`: full per-run and cumulative per-PR model breakdowns, root snapshots, capture timestamps, availability, and completeness.
- `usage-snapshots.sqlite`: durable latest successful snapshots. A temporarily missing endpoint does not erase already collected usage.

Reports are rebuilt, not incrementally charged. Each unique root conversation is counted once. Re-reading a root replaces its snapshot rather than adding it again. Different review runs are summed under **normalized repository identity + PR id**, regardless of head, variant, or generation. Failed and retry-pending runs remain included. If a root is attributed to two runs, collection stops rather than guessing.

The host's root `/usage` aggregate already includes descendant agents, provider attempts, continuations, and compaction when recorded by the host. Never add child `/usage` totals to it. Reviewer and grader roots are distinct and both counted; repeated publication/continuation turns on an existing root are not separate snapshots to add.

## Interpretation

- Costs are integer **micro-USD** (`1 USD = 1,000,000 micros`), not floating-point accumulated dollars.
- Public estimates, provider-reported cost, and preferred known subtotals remain separate. Estimates are **not invoices or negotiated billing rates**. This tool does not reprice old usage.
- Missing prices, deleted threads, unbound runs, and missing usage are not proven zero. Known subtotals may be lower bounds. Inspect completeness and errors alongside every figure.
- A completed workflow does not prove the usage ledger is complete. The host may still label usage `inProgress`; the report preserves that uncertainty.
- Expected token semantics are SDK-normalized: **cache reads ⊆ input**, **reasoning ⊆ output**, and **total = input + cache writes + output**. Do not add cached reads or reasoning twice. Historical rows violating the subset rules are preserved with `accountingWarnings`, not silently repaired or discarded. Totals containing them are not marked complete.
- The collector can only account for usage reported by the host. Unreported provider-side charges, infrastructure costs, and search-service charges are outside this report.
- Only durable `workflow-session:*` bindings are included. A run with no such binding is explicitly unavailable rather than assumed free. Keep the ledger before deleting hosted conversations.

Use a dedicated output directory: the collector restricts it to mode `0700`, its ledger/lock to `0600`, and newly generated reports to `0600` (also using process umask `077`). The source database is opened read-only. HTTP redirects are refused; TLS verification is retained. Request failures record only safe status/type information, never credential-bearing bodies or headers.
