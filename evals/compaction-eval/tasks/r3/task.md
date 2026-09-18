# r3 — research in two waves, first wave unrecoverable: only the summary can carry its numbers

Family `research`, split `dev`. Same knowledge base, answer key and two-wave shape as r2, with one change:
the first wave's pages are deleted as soon as they are read, and nothing is written to disk until the second
wave. Round 3b showed that on r2 every prompt and model scored 1.0 because the model simply re-read the pages
its summary had lost (2-5 re-reads per run). Here there is nothing to re-read: the five wave-one values live
only in the conversation head, so the summariser's fidelity is the score. Checker: `answers.json` against the
hidden key, a `sources.md` line per key, and pages 01-05 absent from the workspace.

---

Project code name: {SEED}.

The `kb/` directory is the internal wiki for the Orion platform. Pages `01-overview.md` through `05-gateway.md` are embargoed: read each one fully (do not grep for the keys), and immediately after reading a page delete it with the shell (`Remove-Item kb/<page>` or `rm kb/<page>`), before reading the next one. Do not read pages 06 to 10 yet; they are for a later request. Do not write any file yet. Find the current, authoritative value for each item below; some pages describe old behaviour, and when a page says a value changed, use the newest value.

- `region_with_no_replicas` — the region that hosts leaders only
- `max_shards_per_tenant` — the maximum number of shards one tenant may own
- `snapshot_interval_minutes` — how often leader replicas snapshot
- `approvals_required_for_schema_change` — number of approvals a schema migration needs
- `default_request_timeout_ms` — the gateway's current default per-request timeout, in milliseconds

When all five pages are read and deleted, reply with the five values and, for each, the page file and the sentence you relied on. Write nothing to disk yet.

## steer

Thanks. Now read pages `06-security.md` through `10-glossary.md` fully and find these three keys:

- `audit_retention_days` — how many days audit records stay in the cold tier once they leave the hot tier (the cold-tier figure alone, not hot plus cold)
- `canary_traffic_percent` — the percentage of production traffic that canary stage two receives
- `incident_commander_role` — who acts as incident commander (short role name)

Pages 06 to 10 also mention some of the five values you answered earlier, sometimes with older figures. Check each earlier answer against what these pages say and keep the newest value in every case. The embargoed pages are gone and must not be recreated or recovered; rely on what you already found. Now write `answers.json` as an object with exactly the eight keys and string values, and `sources.md` with one line per key in the form `<key>: <page file> — <the sentence you relied on>`. Reply with all eight answers when done.
